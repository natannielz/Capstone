import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createClient, type Client, type InValue } from "@libsql/client";
import { migrateDatabase, setTestDatabase, type Database, type Statement } from "../lib/server/database";
import { TABLES, ensureSeed, execute, loadState, provisionCustomerDemo } from "../lib/server/repository";
import { demoPassword } from "../lib/server/demo-credentials";
import { hashPassword } from "../lib/server/security";
import { seedState } from "../lib/domain/seed";
import { GET as catalogGET } from "../app/api/catalog/route";
import { POST as loginPOST } from "../app/api/auth/login/route";
import { PATCH as profilePATCH } from "../app/api/profile/route";

// Every client in this file is an isolated in-memory database. No environment URL,
// live credentials, local demo database or external storage is read by these tests.
class TestQuery implements Statement {
  constructor(readonly client:Client, readonly sql:string, readonly args:InValue[] = []) {}
  bind(...values:unknown[]) { return new TestQuery(this.client, this.sql, values as InValue[]); }
  async first<T>() { return (await this.client.execute({sql:this.sql,args:this.args})).rows[0] as T || null; }
  run() { return this.client.execute({sql:this.sql,args:this.args}); }
}
function adapter(client:Client):Database {
  return {
    prepare: sql => new TestQuery(client, sql),
    async batch(statements) {
      return (await client.batch(statements.map(statement => {
        const query = statement as TestQuery;
        return {sql:query.sql,args:query.args};
      }), "write")).map(result => ({results:result.rows as Record<string,unknown>[]}));
    },
  };
}
const sqlName = (name:string) => name.replace(/[A-Z]/g, character => `_${character.toLowerCase()}`);
async function v6Fixture(client:Client) {
  for (const name of ["0000_common_wallflower.sql", "0001_auth.sql"]) {
    const sql = await readFile(`drizzle/${name}`, "utf8");
    await client.batch(sql.split(";").map(value=>value.trim()).filter(Boolean), "write");
  }
  const state = seedState();
  state.users = state.users.filter(user=>user.role!=="customer");
  state.periods.push({id:"2025-01",revision:1,status:"closed",approvedRevision:1,approvedBy:"pimpinan",submittedBy:"laporan",closedBy:"akuntansi",closedAt:"2025-01-31",snapshot:{debit:71,credit:71,marker:"preserve historical snapshot"}});
  const statements:{sql:string;args:InValue[]}[] = [{sql:"INSERT INTO system_version(id,version) VALUES('global',?)",args:[state.revision]}];
  for (const [key, table, currentKeys] of TABLES) {
    const keys = currentKeys.filter(key=>key!=="customerId" && !(table==="payments"&&key==="invoiceId"));
    const columns = ["id","payload",...keys.map(sqlName)];
    for (const record of state[key]) {
      const item = record as unknown as Record<string,InValue>;
      statements.push({sql:`INSERT INTO ${table}(${columns.join(",")}) VALUES(${columns.map(()=>"?").join(",")})`,args:[item.id,JSON.stringify(item),...keys.map(key=>item[key]??null)]});
    }
  }
  for (const user of state.users) {
    statements.push({sql:"INSERT INTO credentials(user_id,salt,hash) VALUES(?,?,?)",args:[user.id,`fixture-salt-${user.id}`,`fixture-hash-${user.id}`]});
    statements.push({sql:"INSERT INTO user_emails(email,user_id) VALUES(?,?)",args:[user.email!,user.id]});
  }
  statements.push({sql:"INSERT INTO sessions(id,user_id,expires_at) VALUES('preserved-session','pic-a',9999999999999)",args:[]});
  await client.batch(statements,"write");
  return state;
}
async function snapshot(client:Client) {
  const tables = [...TABLES.map(([,table])=>table),"credentials","user_emails","sessions","system_version"];
  return Object.fromEntries(await Promise.all(tables.map(async table=>[table,(await client.execute(`SELECT * FROM ${table} ORDER BY 1`)).rows.map(row=>({
    // Added relational columns are intentionally null for unchanged v6 records.
    ...(table==="orders"||table==="invoices"||table==="complaints"||table==="payments"||table==="attachments"?{customer_id:null}:{}),
    ...(table==="payments"?{invoice_id:null}:{}),
    ...row,
  }))])));
}

test("v7 rebuild migrates populated v6 twice without changing payloads, profiles, passwords, sessions or closed snapshots", async () => {
  const client = createClient({url:":memory:"});
  try {
    await v6Fixture(client);
    const before = await snapshot(client);
    await migrateDatabase(client);
    await migrateDatabase(client);
    assert.deepEqual(await snapshot(client),before);
    assert.equal((await client.execute("SELECT id FROM app_migrations")).rows.length,3);
    assert.equal((await client.execute("PRAGMA foreign_key_check")).rows.length,0);
    assert.equal(Number((await client.execute("PRAGMA foreign_keys")).rows[0].foreign_keys),1);
    for (const table of ["orders","invoices"]) {
      await assert.rejects(client.execute(`INSERT INTO ${table}(id,payload,division_id,customer_id,number) VALUES('invalid','{}',NULL,NULL,'invalid')`),/CHECK/);
      await assert.rejects(client.execute(`INSERT INTO ${table}(id,payload,division_id,customer_id,number) VALUES('invalid','{}','div-ops','pic-a','invalid')`),/CHECK/);
      await assert.rejects(client.execute(`INSERT INTO ${table}(id,payload,division_id,customer_id,number) VALUES('invalid','{}',NULL,'missing-user','invalid')`),/FOREIGN KEY/);
    }
    const shipment=(await client.execute("SELECT id FROM shipments LIMIT 1")).rows[0].id;
    const shipmentLine=(await client.execute({sql:"SELECT id FROM shipment_lines WHERE shipment_id=?",args:[shipment]})).rows[0].id;
    await assert.rejects(client.execute({sql:"INSERT INTO complaints(id,payload,shipment_id,shipment_line_id,division_id,customer_id) VALUES('invalid','{}',?,?,NULL,NULL)",args:[shipment,shipmentLine]}),/CHECK/);
    await assert.rejects(client.execute({sql:"INSERT INTO complaints(id,payload,shipment_id,shipment_line_id,division_id,customer_id) VALUES('invalid','{}',?,?,'div-ops','pic-a')",args:[shipment,shipmentLine]}),/CHECK/);
    await client.execute("INSERT INTO payments(id,payload,division_id,customer_id) VALUES('unknown-payer','{}',NULL,NULL)");
    await client.execute("INSERT INTO attachments(id,payload,owner_id,division_id,customer_id) VALUES('cost-evidence','{}','staf',NULL,NULL)");
    await assert.rejects(client.execute("INSERT INTO payments(id,payload,division_id,customer_id) VALUES('two-buyers','{}','div-ops','pic-a')"),/CHECK/);
  } finally { client.close(); }
});

test("migration checks legacy relations before commit and rolls the rebuild back on invalid data", async () => {
  const client = createClient({url:":memory:"});
  try {
    await v6Fixture(client);
    await client.migrate(["UPDATE orders SET division_id='missing-division' WHERE id=(SELECT id FROM orders LIMIT 1)"]);
    await assert.rejects(migrateDatabase(client),/CHECK/);
    assert.equal((await client.execute("PRAGMA table_info(orders)")).rows.some(row=>row.name==="customer_id"),false);
    assert.equal((await client.execute("SELECT id FROM app_migrations WHERE id='0002_customer_buyers.sql'")).rows.length,0);
    assert.equal(Number((await client.execute("PRAGMA foreign_keys")).rows[0].foreign_keys),1);
  } finally { client.close(); }
});

test("customer provisioning is repeatable and preserves edited inactive profile and changed credentials", async () => {
  const oldSeed=process.env.DEMO_PASSWORD_SEED;
  process.env.DEMO_PASSWORD_SEED="isolated-v7-test-password-seed-not-a-live-secret";
  const client=createClient({url:":memory:"});
  try {
    await migrateDatabase(client);
    await client.execute("INSERT INTO system_version(id,version) VALUES('global',0)");
    const db=adapter(client);
    assert.equal(await provisionCustomerDemo(db),true);
    assert.equal(await provisionCustomerDemo(db),false);
    const credential=(await client.execute("SELECT salt,hash FROM credentials WHERE user_id='customer-demo'")).rows[0];
    assert.equal(await hashPassword(demoPassword("customer-demo"),String(credential.salt)),credential.hash);
    const row=(await client.execute("SELECT payload FROM users WHERE id='customer-demo'")).rows[0];
    const edited={...JSON.parse(String(row.payload)),name:"Pelanggan diperbarui",phone:"081234567890",address:"Alamat yang diperbarui",active:false};
    await client.batch([
      {sql:"UPDATE users SET payload=? WHERE id='customer-demo'",args:[JSON.stringify(edited)]},
      "UPDATE credentials SET salt='changed-fixture-salt',hash='changed-fixture-hash' WHERE user_id='customer-demo'",
    ],"write");
    const before=await snapshot(client);
    assert.equal(await provisionCustomerDemo(db),false);
    assert.deepEqual(await snapshot(client),before);
  } finally { client.close(); if(oldSeed===undefined)delete process.env.DEMO_PASSWORD_SEED;else process.env.DEMO_PASSWORD_SEED=oldSeed; }
});

test("customer provisioning stops on email and identity conflicts without changing existing accounts", async () => {
  for (const conflict of ["email","identity"]) {
    const client=createClient({url:":memory:"});
    try {
      await migrateDatabase(client);
      const id=conflict==="identity"?"customer-demo":"another-user";
      await client.execute({sql:"INSERT INTO users(id,payload,role) VALUES(?,?,?)",args:[id,JSON.stringify({id,name:"Existing",role:"admin",divisionId:null,active:false}),"admin"]});
      if(conflict==="email")await client.execute({sql:"INSERT INTO user_emails(email,user_id) VALUES(?,?)",args:["customer@unit-toko.demo",id]});
      const before=await snapshot(client);
      await assert.rejects(provisionCustomerDemo(adapter(client)),/provisioning stopped/);
      assert.deepEqual(await snapshot(client),before);
    } finally { client.close(); }
  }
});

test("fresh repository keeps customer buyer keys consistent, authenticates explicit email, and exposes only sale catalog fields", async () => {
  const oldSeed=process.env.DEMO_PASSWORD_SEED;
  process.env.DEMO_PASSWORD_SEED="isolated-v7-test-password-seed-not-a-live-secret";
  const client=createClient({url:":memory:"});
  try {
    await migrateDatabase(client);
    setTestDatabase(adapter(client));
    await ensureSeed();
    const state=await loadState();
    const customer=state.users.find(user=>user.id==="customer-demo")!;
    assert.equal(customer.email,"customer@unit-toko.demo");
    const login=await loginPOST(new Request("http://localhost/api/auth/login",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({email:customer.email,password:demoPassword(customer.id)})}));
    assert.equal(login.status,200);
    const cookie=login.headers.get("set-cookie")!.split(";")[0];
    const profile=await profilePATCH(new Request("http://localhost/api/profile",{method:"PATCH",headers:{cookie,"Content-Type":"application/json"},body:JSON.stringify({name:customer.name,phone:customer.phone||"",position:customer.position||"",address:"  Alamat pelanggan baru  "})}));
    assert.equal(profile.status,200);
    assert.equal((await loadState()).users.find(user=>user.id===customer.id)?.address,"Alamat pelanggan baru");
    const tooLong=await profilePATCH(new Request("http://localhost/api/profile",{method:"PATCH",headers:{cookie,"Content-Type":"application/json"},body:JSON.stringify({address:"x".repeat(501)})}));
    assert.equal(tooLong.status,400);
    const result=await execute(customer,{id:crypto.randomUUID(),type:"order.create",data:{recipientName:"Penerima",recipientPhone:"081234567890",address:"Alamat penerima",lines:[{productId:"air",qty:1,unitPrice:state.products.find(product=>product.id==="air")!.price}]}});
    const stored=(await client.execute({sql:"SELECT payload,division_id,customer_id FROM orders WHERE id=?",args:[result.id]})).rows[0];
    assert.equal(stored.division_id,null);
    assert.equal(stored.customer_id,customer.id);
    assert.equal(JSON.parse(String(stored.payload)).customerId,customer.id);
    const response=await catalogGET();
    assert.equal(response.status,200);
    assert.equal(response.headers.get("cache-control"),"no-store");
    const catalog=await response.json() as {products:Record<string,unknown>[];revision:number};
    assert.ok(catalog.products.length>0);
    const keys=["id","familyId","sku","name","unit","price","active","category","image","description","packaging","available"].sort();
    for(const product of catalog.products){assert.deepEqual(Object.keys(product).sort(),keys);assert.equal(product.active,true);assert.ok(Number(product.available)>=0);}
  } finally { client.close(); if(oldSeed===undefined)delete process.env.DEMO_PASSWORD_SEED;else process.env.DEMO_PASSWORD_SEED=oldSeed; }
});
