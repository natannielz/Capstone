import { test } from "node:test";
import assert from "node:assert/strict";
import { createClient, type Client, type InValue } from "@libsql/client";
import manifest from "../lib/domain/demo-products-v10.json";
import publicMetadata from "../lib/domain/demo-catalog-public-v10.json";
import { productCollections, productDescription, productFamilies, productGroup, productImage } from "../lib/domain/catalog";
import { catalogPage, paginateCatalog } from "../lib/domain/catalog-pagination";
import { runCommand } from "../lib/domain/engine";
import { seedState } from "../lib/domain/seed";
import { financialReport, productAvailable, scopeState, today } from "../lib/domain/selectors";
import { DomainError, type Product, type State } from "../lib/domain/model";
import { migrateDatabase, setTestDatabase, type Database, type Statement } from "../lib/server/database";
import { DEMO_CATALOG_MIGRATION, provisionDemoCatalog } from "../lib/server/demo-catalog";
import { TABLES } from "../lib/server/repository";
import { GET as catalogGET } from "../app/api/catalog/route";
import { inShopCollection, shopCategory, shopFamilies, type PublicProduct } from "../components/shop/shop-data";

// Every test uses isolated SQLite memory. No live database URL, credential or file is read.
class Query implements Statement {
  constructor(readonly client: Client, readonly sql: string, readonly args: InValue[] = []) {}
  bind(...values: unknown[]) { return new Query(this.client, this.sql, values as InValue[]); }
  async first<T>() { return (await this.client.execute({sql: this.sql, args: this.args})).rows[0] as T || null; }
  run() { return this.client.execute({sql: this.sql, args: this.args}); }
}
function adapter(client: Client): Database {
  return {prepare: (sql) => new Query(client, sql), async batch(statements) {
    return (await client.batch(statements.map((statement) => {
      const query = statement as Query; return {sql: query.sql, args: query.args};
    }), "write")).map((result) => ({results: result.rows as Record<string, unknown>[]}));
  }};
}
async function fixture() {
  const client = createClient({url: ":memory:"});
  await migrateDatabase(client);
  const original = seedState();
  const statements: {sql: string; args: InValue[]}[] = [{sql: "INSERT INTO system_version(id,version) VALUES('global',?)", args: [original.revision]}];
  for (const [collection, table, keys] of TABLES) for (const record of original[collection]) {
    const item = record as unknown as Record<string, InValue>;
    const columns = ["id", "payload", ...keys.map((key) => key.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`))];
    statements.push({sql: `INSERT INTO ${table}(${columns.join(",")}) VALUES(${columns.map(() => "?").join(",")})`, args: [item.id, JSON.stringify(item), ...keys.map((key) => item[key] ?? null)]});
  }
  for (const user of original.users) {
    statements.push({sql: "INSERT INTO credentials(user_id,salt,hash) VALUES(?,?,?)", args: [user.id, "fixture-salt", "fixture-hash"]});
    statements.push({sql: "INSERT INTO user_emails(email,user_id) VALUES(?,?)", args: [user.email!, user.id]});
  }
  statements.push({sql: "INSERT INTO sessions(id,user_id,expires_at) VALUES('fixture-session','pic-a',9999999999999)", args: []});
  await client.batch(statements, "write");
  return {client, db: adapter(client), original};
}
async function state(client: Client): Promise<State> {
  const result = seedState();
  for (const [collection, table] of TABLES) {
    (result[collection] as unknown[]) = (await client.execute(`SELECT payload FROM ${table}`)).rows.map((row) => JSON.parse(String(row.payload)));
  }
  result.revision = Number((await client.execute("SELECT version FROM system_version WHERE id='global'")).rows[0].version);
  return result;
}
async function snapshot(client: Client): Promise<Record<string, Record<string, unknown>[]>> {
  const tables = [...TABLES.map(([,table]) => table), "credentials", "sessions", "user_emails", "system_version", "app_migrations", "mutation_guards"];
  return Object.fromEntries(await Promise.all(tables.map(async (table) => [table, (await client.execute(`SELECT * FROM ${table} ORDER BY 1`)).rows.map((row) => ({...row}))])));
}

test("v10 manifest has 200 unique usable products and matching public metadata without generation prompts", () => {
  assert.equal(manifest.length, 200);
  for (const key of ["id", "sku", "name", "description", "image", "imagePrompt"] as const) assert.equal(new Set(manifest.map((item) => item[key])).size, 200, key);
  assert.deepEqual(publicMetadata, manifest.map(({id, image, description, group, collection}) => ({id, image, description, group, collection})));
  for (const [index, item] of manifest.entries()) {
    const serial = String(index + 1).padStart(3, "0");
    assert.equal(item.id, `demo-${serial}`); assert.equal(item.sku, `DEMO-${serial}`);
    assert.equal(item.image, `/images/products/generated/demo-${serial}.webp`);
    assert.ok(item.description.length > 40); assert.ok(Number.isSafeInteger(item.price) && item.price > 0);
    const product = {...item, minimum: 5, returnMonths: 0, active: true} as Product;
    assert.equal(productImage(product), item.image); assert.equal(productDescription(product), item.description);
    assert.equal(productGroup(product), item.group); assert.deepEqual(productCollections(product), [item.collection]);
  }
});

test("v10 adds 200 SKUs once, preserves existing records, and books exactly the new inventory", async () => {
  const f = await fixture();
  try {
    const before = await snapshot(f.client);
    assert.equal(await provisionDemoCatalog(f.db), true);
    const s = await state(f.client), after = await snapshot(f.client);
    for (const [collection, table] of TABLES) {
      if (collection === "periods") continue;
      for (const old of before[table]) assert.deepEqual(after[table].find((row) => row.id === old.id), old, `${table}/${old.id} unchanged`);
    }
    for (const table of ["credentials", "sessions", "user_emails"]) assert.deepEqual(after[table], before[table]);
    assert.equal(s.products.length, 236); assert.equal(productFamilies(s.products).length, 212);
    const addedBatches = s.batches.filter((batch) => batch.id.endsWith("-v10"));
    assert.equal(addedBatches.length, 200);
    assert.ok(manifest.every((product) => productAvailable(s, product.id) > 0));
    const value = addedBatches.reduce((total, batch) => total + batch.qty * batch.cost, 0);
    const journal = s.journals.find((item) => item.sourceId === DEMO_CATALOG_MIGRATION)!;
    assert.ok(journal); assert.equal(journal.date, today());
    const entries = s.journalLines.filter((line) => line.journalId === journal.id);
    assert.deepEqual(entries.map(({account, debit, credit}) => ({account, debit, credit})), [{account: "inventory", debit: value, credit: 0}, {account: "capital", debit: 0, credit: value}]);
    const report = financialReport(s, today()); assert.equal(report.debit, report.credit);
    assert.ok(report.debit >= value);
    assert.equal(s.revision, f.original.revision + 1);
    assert.equal(await provisionDemoCatalog(f.db), false);
    assert.deepEqual(await snapshot(f.client), after);
    assert.equal((await f.client.execute("PRAGMA foreign_key_check")).rows.length, 0);
  } finally {f.client.close();}
});

test("v10 retries never reset inactive products, changed prices, or stock already consumed", async () => {
  const f = await fixture();
  try {
    await provisionDemoCatalog(f.db);
    const s = await state(f.client), product = s.products.find((item) => item.id === "demo-001")!, batch = s.batches.find((item) => item.productId === product.id)!;
    product.name = "Nama yang disunting pengelola"; product.price = 93000; product.active = false;
    batch.qty = 0;
    await f.client.batch([
      {sql: "UPDATE products SET payload=? WHERE id=?", args: [JSON.stringify(product), product.id]},
      {sql: "UPDATE batches SET payload=?,qty=0 WHERE id=?", args: [JSON.stringify(batch), batch.id]},
      "UPDATE credentials SET hash='changed-fixture-hash' WHERE user_id='customer-demo'",
    ], "write");
    const before = await snapshot(f.client);
    assert.equal(await provisionDemoCatalog(f.db), false);
    assert.deepEqual(await snapshot(f.client), before);
  } finally {f.client.close();}
});

test("v10 skips an already owned SKU without changing it or adding stock, and fails closed on identity conflicts", async () => {
  for (const conflict of ["none", "id", "sku"]) {
    const f = await fixture();
    try {
      const product = {...f.original.products[0], id: conflict === "sku" ? "existing-owner" : "demo-001", sku: conflict === "id" ? "OTHER-SKU" : "DEMO-001", name: "Previously maintained product", active: false};
      await f.client.execute({sql: "INSERT INTO products(id,sku,payload) VALUES(?,?,?)", args: [product.id, product.sku, JSON.stringify(product)]});
      const before = await snapshot(f.client);
      if (conflict !== "none") {
        await assert.rejects(provisionDemoCatalog(f.db), /identity conflict/);
        assert.deepEqual(await snapshot(f.client), before);
      } else {
        await provisionDemoCatalog(f.db);
        const s = await state(f.client);
        assert.deepEqual(s.products.find((item) => item.id === product.id), product);
        assert.equal(s.batches.filter((batch) => batch.productId === product.id).length, 0);
        assert.equal(s.batches.filter((batch) => batch.id.endsWith("-v10")).length, 199);
      }
    } finally {f.client.close();}
  }
});

test("v10 preserves closed reports and reopens later approvals after adding the opening journal", async () => {
  const f = await fixture();
  try {
    const month = today().slice(0, 7), [year, numericMonth] = month.split("-").map(Number);
    const nextMonth = new Date(Date.UTC(year, numericMonth, 1)).toISOString().slice(0, 7);
    const closed = {...f.original.periods.find((period) => period.id === month)!, status: "closed", snapshot: {immutable: "historical closing"}};
    const approved = {...closed, id: nextMonth, status: "approved", revision: 7, approvedRevision: 7, approvedBy: "pimpinan", snapshot: null};
    await f.client.batch([
      {sql: "UPDATE periods SET payload=? WHERE id=?", args: [JSON.stringify(closed), month]},
      {sql: "INSERT INTO periods(id,payload) VALUES(?,?)", args: [nextMonth, JSON.stringify(approved)]},
    ], "write");
    await provisionDemoCatalog(f.db);
    const s = await state(f.client);
    assert.deepEqual(s.periods.find((period) => period.id === month), closed);
    assert.deepEqual(s.periods.find((period) => period.id === nextMonth), {...approved, status: "open", revision: 8, approvedRevision: null, approvedBy: null});
    assert.equal(s.journals.find((journal) => journal.sourceId === DEMO_CATALOG_MIGRATION)?.date, `${nextMonth}-01`);
  } finally {f.client.close();}
});

test("v10 rolls back a mid-transaction failure including stock, journals, marker, and version", async () => {
  const f = await fixture();
  try {
    const before = await snapshot(f.client);
    const failing: Database = {...f.db, batch(statements) {
      if ((statements[0] as Query).sql.startsWith("INSERT INTO mutation_guards")) {
        return f.db.batch([...statements.slice(0, 8), f.db.prepare("INSERT INTO nonexistent_fixture_table VALUES(1)"), ...statements.slice(8)]);
      }
      return f.db.batch(statements);
    }};
    await assert.rejects(provisionDemoCatalog(failing), /nonexistent_fixture_table/);
    assert.deepEqual(await snapshot(f.client), before);
    assert.equal(await provisionDemoCatalog(f.db), true);
  } finally {f.client.close();}
});

test("v10 retries a close-period race using the new open date, and concurrent initializers add inventory once", async () => {
  const f = await fixture();
  try {
    let raced = false;
    const closed = {...f.original.periods.find((period) => period.id === today().slice(0, 7))!, status: "closed", snapshot: {race: "preserved"}};
    const racing: Database = {...f.db, async batch(statements) {
      if (!raced && (statements[0] as Query).sql.startsWith("INSERT INTO mutation_guards")) {
        raced = true;
        await f.client.batch([{sql: "UPDATE periods SET payload=? WHERE id=?", args: [JSON.stringify(closed), closed.id]}, "UPDATE system_version SET version=version+1 WHERE id='global'"], "write");
      }
      return f.db.batch(statements);
    }};
    assert.equal(await provisionDemoCatalog(racing), true);
    let s = await state(f.client);
    assert.deepEqual(s.periods.find((period) => period.id === closed.id), closed);
    assert.ok(s.journals.find((journal) => journal.sourceId === DEMO_CATALOG_MIGRATION)!.date.slice(0, 7) > closed.id);
    assert.equal(s.revision, f.original.revision + 2);
    // A separate clean fixture exercises two initializers which both see no marker.
    const second = await fixture();
    try {
      const results = await Promise.all([provisionDemoCatalog(second.db), provisionDemoCatalog(second.db)]);
      assert.deepEqual(results.sort(), [false, true]);
      s = await state(second.client);
      assert.equal(s.products.length, 236);
      assert.equal(s.journals.filter((journal) => journal.sourceId === DEMO_CATALOG_MIGRATION).length, 1);
      assert.equal(s.revision, second.original.revision + 1);
    } finally {second.client.close();}
  } finally {f.client.close();}
});

test("v10 generated SKUs support checkout, ownership isolation, approved stock fulfillment and real price checks", async () => {
  const f = await fixture();
  try {
    await provisionDemoCatalog(f.db);
    let s = await state(f.client);
    const customer = s.users.find((actor) => actor.id === "customer-demo")!;
    const other = {...customer, id: "customer-other", name: "Another customer"}; s.users.push(other);
    const item = s.products.find((product) => product.id === "demo-001")!;
    const data = {recipientName: "Penerima", recipientPhone: "081234567890", address: "Alamat simulasi", lines: [{productId: item.id, qty: 2, unitPrice: item.price}]};
    const act = (id: string, type: string, payload: Record<string, unknown>) => {
      const result = runCommand(s, s.users.find((actor) => actor.id === id)!, {id: crypto.randomUUID(), type, data: payload});
      s = result.state; return result.result.id;
    };
    assert.throws(() => act(customer.id, "order.create", {...data, lines: [{productId: item.id, qty: 2, unitPrice: item.price - 1}]}), /Harga/);
    const orderId = act(customer.id, "order.create", data), line = s.orderLines.find((row) => row.orderId === orderId)!;
    assert.equal(s.orders.find((order) => order.id === orderId)?.customerId, customer.id);
    assert.equal(scopeState(s, other).orders.some((order) => order.id === orderId), false);
    assert.throws(() => act(other.id, "order.cancel", {id: orderId}), (error: unknown) => error instanceof DomainError && error.status === 403);
    assert.throws(() => act(customer.id, "product.update", {id: item.id, price: 1, minimum: 1, returnMonths: 0}), (error: unknown) => error instanceof DomainError && error.status === 403);
    act("kepala", "order.review", {id: orderId, approve: true});
    act("staf", "stock.reserve", {orderLineId: line.id, qty: 2});
    const shipmentId = act("staf", "shipment.create", {orderId, courierId: "kurir", vehicle: "DEMO", lines: [{orderLineId: line.id, qty: 2}]});
    const before = s.batches.find((batch) => batch.productId === item.id)!.qty;
    act("kurir", "shipment.dispatch", {id: shipmentId});
    assert.equal(s.batches.find((batch) => batch.productId === item.id)!.qty, before - 2);
    assert.equal(s.orderLines.find((row) => row.id === line.id)?.price, item.price);
  } finally {f.client.close();}
});

test("v10 catalog pagination covers every family exactly once and clamps stale or invalid page links", () => {
  const items = Array.from({length: 212}, (_, index) => index);
  const all = Array.from({length: 9}, (_, index) => paginateCatalog(items, String(index + 1)).items).flat();
  assert.deepEqual(all, items); assert.equal(paginateCatalog(items, "1").items.length, 24);
  assert.deepEqual(paginateCatalog(items, "999999").items, items.slice(192));
  for (const input of [null, "0", "-1", "NaN", "1.5", "1e3", "9999999"]) assert.equal(catalogPage(input), 1);
  assert.deepEqual(paginateCatalog([], "9"), {page: 1, pages: 1, total: 0, start: 0, end: 0, items: []});
});

test("v10 public catalog exposes all 200 images, descriptions and collection filters without stock costs or prompts", async () => {
  const f = await fixture();
  try {
    setTestDatabase(f.db);
    const response = await catalogGET(); assert.equal(response.status, 200);
    const {products} = await response.json() as {products: PublicProduct[]};
    assert.equal(products.length, 236); assert.equal(shopFamilies(products).length, 212);
    const keys = ["id", "familyId", "sku", "name", "unit", "price", "active", "category", "group", "collections", "image", "description", "packaging", "available"].sort();
    for (const item of manifest) {
      const product = products.find((row) => row.id === item.id)!;
      assert.deepEqual(Object.keys(product).sort(), keys);
      assert.equal(product.image, item.image); assert.equal(product.description, item.description);
      assert.equal(shopCategory(product), item.group); assert.equal(inShopCollection(product, item.collection), true);
      assert.ok(product.available > 0);
    }
    assert.equal(inShopCollection(products.find((item) => item.id === "kopi")!, "pantry"), true);
    assert.equal(inShopCollection(products.find((item) => item.id === "kopi")!, "rapat"), true);
  } finally {f.client.close();}
});
