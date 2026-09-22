import manifest from "../domain/demo-products-v10.json";
import { today } from "../domain/selectors";
import type { Batch, Journal, JournalLine, Movement, Period, Product } from "../domain/model";
import type { Database, Statement } from "./database";

export const DEMO_CATALOG_MIGRATION = "demo-catalog-v10";

function validateManifest() {
  if (manifest.length !== 200) throw new Error("Demo catalog must contain exactly 200 products.");
  const unique = new Set<string>();
  for (const [index, item] of manifest.entries()) {
    const number = String(index + 1).padStart(3, "0");
    if (item.id !== `demo-${number}` || item.sku !== `DEMO-${number}` ||
      !["OMI", "Smart"].includes(item.category) || !item.name.trim() || !item.unit.trim() ||
      !Number.isSafeInteger(item.price) || item.price <= 0 || unique.has(item.sku)) {
      throw new Error("Invalid demo catalog definition; no records were changed.");
    }
    unique.add(item.sku);
  }
}

function insert(db: Database, table: string, item: {id: string}, keys: string[] = []): Statement {
  const record = item as unknown as Record<string, unknown>;
  const columns = ["id", "payload", ...keys.map((key) => key.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`))];
  return db.prepare(`INSERT INTO ${table}(${columns.join(",")}) VALUES(${columns.map(() => "?").join(",")})`)
    .bind(item.id, JSON.stringify(item), ...keys.map((key) => record[key] ?? null));
}

/** Add one versioned fixture, never reset a product, restock an existing SKU, or rewrite history. */
export async function provisionDemoCatalog(db: Database): Promise<boolean> {
  validateManifest();
  // This table already belongs to the schema migrator. IF NOT EXISTS also supports
  // older local adapters that applied the SQL files before migration tracking existed.
  await db.prepare("CREATE TABLE IF NOT EXISTS app_migrations (id TEXT PRIMARY KEY)").run();
  for (let attempt = 0; attempt < 4; attempt++) {
    const [markers, versions, products, periods] = await db.batch([
      db.prepare("SELECT id FROM app_migrations WHERE id=?").bind(DEMO_CATALOG_MIGRATION),
      db.prepare("SELECT version FROM system_version WHERE id='global'"),
      db.prepare("SELECT id,sku FROM products"),
      db.prepare("SELECT payload FROM periods"),
    ]);
    if (markers.results.length) return false;
    const revision = Number(versions.results[0]?.version);
    if (!Number.isSafeInteger(revision)) throw new Error("Initialize the repository before provisioning the demo catalog.");
    const ids = new Map(products.results.map((row) => [String(row.id), String(row.sku)]));
    const skus = new Map(products.results.map((row) => [String(row.sku), String(row.id)]));
    for (const item of manifest) {
      if ((ids.has(item.id) && ids.get(item.id) !== item.sku) ||
        (skus.has(item.sku) && skus.get(item.sku) !== item.id)) {
        throw new Error(`Demo catalog identity conflict (${item.id}); provisioning stopped without changing records.`);
      }
    }
    const missing = manifest.filter((item) => !ids.has(item.id));
    const currentPeriods = periods.results.map((row) => JSON.parse(String(row.payload)) as Period);
    const latestClosed = currentPeriods.filter((period) => period.status === "closed").map((period) => period.id).sort().at(-1);
    let date = today();
    if (latestClosed && latestClosed >= date.slice(0, 7)) {
      const [year, month] = latestClosed.split("-").map(Number);
      date = new Date(Date.UTC(year, month, 1)).toISOString().slice(0, 10);
    }
    const expiry = new Date(`${date}T00:00:00Z`);
    expiry.setUTCFullYear(expiry.getUTCFullYear() + 1);
    const guard = `${DEMO_CATALOG_MIGRATION}:${crypto.randomUUID()}`;
    const statements = [db.prepare("INSERT INTO mutation_guards(id,valid) SELECT ?,CASE WHEN version=? THEN 1 ELSE 0 END FROM system_version WHERE id='global'").bind(guard, revision)];
    let inventoryValue = 0;
    for (const item of missing) {
      const index = Number(item.id.slice(-3));
      const product: Product = {id: item.id, sku: item.sku, name: item.name,
        category: item.category as Product["category"], unit: item.unit, price: item.price,
        minimum: item.category === "OMI" ? 10 : 5, returnMonths: item.category === "OMI" ? 1 : 0, active: true};
      const batch: Batch = {id: `batch-${item.id}-v10`, productId: item.id,
        code: `LOT-${item.sku}-V10`, qty: item.category === "OMI" ? 24 + (index % 7) * 6 : 12 + (index % 7) * 3,
        held: 0, cost: Math.max(100, Math.round(item.price * 0.74 / 100) * 100),
        expiry: item.category === "OMI" ? expiry.toISOString().slice(0, 10) : "",
        location: item.group === "Merchandise" ? "Gudang merchandise" : item.group === "Kebutuhan kantor" ? "Rak perlengkapan" : "Rak pantry"};
      const movement: Movement = {id: `opening-${item.id}-v10`, batchId: batch.id, date,
        type: "opening", qty: batch.qty, sourceId: DEMO_CATALOG_MIGRATION,
        note: "Persediaan awal katalog simulasi v10"};
      statements.push(insert(db, "products", product, ["sku"]), insert(db, "batches", batch, ["productId", "qty", "held"]), insert(db, "movements", movement, ["batchId"]));
      inventoryValue += batch.qty * batch.cost;
    }
    if (missing.length) {
      const journal: Journal = {id: `${DEMO_CATALOG_MIGRATION}-opening`, date, sourceId: DEMO_CATALOG_MIGRATION,
        description: `Persediaan awal ${missing.length} produk katalog simulasi v10`};
      const inventory: JournalLine = {id: `${journal.id}-inventory`, journalId: journal.id, account: "inventory", debit: inventoryValue, credit: 0};
      const capital: JournalLine = {id: `${journal.id}-capital`, journalId: journal.id, account: "capital", debit: 0, credit: inventoryValue};
      statements.push(insert(db, "journals", journal), insert(db, "journal_lines", inventory, ["journalId", "debit", "credit"]), insert(db, "journal_lines", capital, ["journalId", "debit", "credit"]));
      const month = date.slice(0, 7);
      if (!currentPeriods.some((period) => period.id === month)) {
        currentPeriods.push({id: month, revision: 0, status: "open", approvedRevision: null, approvedBy: null, submittedBy: null, closedBy: null, closedAt: "", snapshot: null});
      }
      // Match the command engine: a new journal invalidates this and later open reports.
      // Closed reports are strictly earlier than the chosen date and never touched.
      for (const period of currentPeriods.filter((period) => period.id >= month)) {
        period.revision++;
        if (period.status === "review" || period.status === "approved") {
          period.status = "open"; period.approvedRevision = null; period.approvedBy = null;
        }
        statements.push(db.prepare("INSERT INTO periods(id,payload) VALUES(?,?) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload").bind(period.id, JSON.stringify(period)));
      }
    }
    statements.push(
      db.prepare("UPDATE system_version SET version=version+1 WHERE id='global'"),
      db.prepare("INSERT INTO app_migrations(id) VALUES(?)").bind(DEMO_CATALOG_MIGRATION),
      db.prepare("DELETE FROM mutation_guards WHERE id=?").bind(guard),
    );
    try { await db.batch(statements); return true; }
    catch (error) {
      if (attempt < 3 && /concurrent_write_guard|UNIQUE constraint failed: app_migrations/.test(String(error))) continue;
      throw error;
    }
  }
  throw new Error("Demo catalog was updated concurrently. Retry initialization.");
}
