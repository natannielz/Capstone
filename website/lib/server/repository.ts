import { database, initializeDatabase, type Database } from "./database";
import { demoPassword } from "./demo-credentials";
export { database } from "./database";
import { DEMO_ACCOUNTS, type Actor } from "../domain/accounts";
import { DomainError, type State, type Collection, type Command, type CommandResult } from "../domain/model";
import { emptyState, seedState } from "../domain/seed";
import { runCommand } from "../domain/engine";
import { hashPassword, digest } from "./security";


// Explicit relational keys and constraints are stored alongside versioned domain records.
export const TABLES: [Collection,string,string[]][] = [
  ["divisions","divisions",[]],["users","users",["divisionId","role"]],["products","products",["sku"]],["suppliers","suppliers",[]],["batches","batches",["productId","qty","held"]],["orders","orders",["divisionId","customerId","number"]],["orderLines","order_lines",["orderId","productId"]],["reservations","reservations",["orderLineId","batchId","qty"]],["substitutions","substitutions",["orderLineId","productId"]],["shipments","shipments",["orderId","courierId","number"]],["shipmentLines","shipment_lines",["shipmentId","orderLineId","batchId"]],["complaints","complaints",["shipmentId","shipmentLineId","divisionId","customerId"]],["invoices","invoices",["divisionId","customerId","number"]],["invoiceLines","invoice_lines",["invoiceId","shipmentLineId"]],["payments","payments",["divisionId","customerId","invoiceId"]],["allocations","allocations",["paymentId","invoiceId"]],["credits","credits",["invoiceId"]],["refunds","refunds",["paymentId"]],["purchases","purchases",["supplierId","orderId","number"]],["purchaseLines","purchase_lines",["purchaseId","productId"]],["supplierPayments","supplier_payments",["purchaseId"]],["stockReturns","stock_returns",["batchId"]],["stocktakes","stocktakes",["batchId"]],["expenses","expenses",["shipmentId","purchaseId"]],["movements","movements",["batchId"]],["journals","journals",[]],["journalLines","journal_lines",["journalId","debit","credit"]],["periods","periods",[]],["attachments","attachments",["ownerId","divisionId","customerId","shipmentId"]],["audits","audits",["actorId"]]
];
const snake=(s:string)=>s.replace(/[A-Z]/g,m=>`_${m.toLowerCase()}`);
function writeRecord(db:Database,table:string,keys:string[],record:object){const item=record as Record<string,unknown>;const cols=["id","payload",...keys.map(snake)];const values=[item.id,JSON.stringify(item),...keys.map(k=>item[k]??null)];return db.prepare(`INSERT INTO ${table} (${cols.join(",")}) VALUES (${cols.map(()=>"?").join(",")}) ON CONFLICT(id) DO UPDATE SET ${cols.slice(1).map(c=>`${c}=excluded.${c}`).join(",")}`).bind(...values);}
let initializing:Promise<void>|undefined;
export async function ensureSeed() {
  if (initializing) return initializing;
  initializing = (async () => {
    await initializeDatabase();
    const db = database();
    if (!await db.prepare("SELECT version FROM system_version WHERE id='global'").first()) {
      const state = seedState();
      const queries = [db.prepare("INSERT INTO system_version(id,version) VALUES('global',?)").bind(state.revision)];
      for (const [key, table, keys] of TABLES) for (const item of state[key]) queries.push(writeRecord(db, table, keys, item));
      for (const user of state.users) {
        const salt = crypto.randomUUID(), hash = await hashPassword(demoPassword(user.id), salt);
        queries.push(db.prepare("INSERT INTO user_emails(email,user_id) VALUES(?,?)").bind(user.email?.toLowerCase(), user.id));
        queries.push(db.prepare("INSERT INTO credentials(user_id,salt,hash) VALUES(?,?,?)").bind(user.id, salt, hash));
      }
      try { await db.batch(queries); }
      catch (error) { if (!await db.prepare("SELECT version FROM system_version WHERE id='global'").first()) throw error; }
    }
    await provisionCustomerDemo(db);
    await upgradeProfiles(db);
  })();
  try { await initializing; }
  catch (error) { initializing = undefined; throw error; }
}

export async function loadState():Promise<State>{await ensureSeed();const db=database();const results=await db.batch([db.prepare("SELECT version FROM system_version WHERE id='global'"),...TABLES.map(([,table])=>db.prepare(`SELECT payload FROM ${table}`))]);const s=emptyState();s.revision=(results[0].results[0] as {version:number}).version;for(let i=0;i<TABLES.length;i++){const key=TABLES[i][0];(s[key] as unknown[]) = results[i+1].results.map(row=>JSON.parse(String((row as {payload:string}).payload)));}return s;}
export async function execute(actor:Actor,command:Command):Promise<CommandResult>{
  const db=database(),fingerprint=await digest(JSON.stringify(command));await ensureSeed();
  for(let attempt=0;attempt<4;attempt++){
    const existing=await db.prepare("SELECT actor_id,fingerprint,result FROM commands WHERE id=?").bind(command.id).first<{actor_id:string;fingerprint:string;result:string}>();if(existing){if(existing.actor_id!==actor.id||existing.fingerprint!==fingerprint)throw new DomainError("Identitas tindakan sudah digunakan untuk permintaan berbeda.",409);return JSON.parse(existing.result);}
    const old=await loadState(),freshActor=old.users.find(u=>u.id===actor.id);if(!freshActor?.active)throw new DomainError("Akun tidak aktif.",403);const {state:s,result}=runCommand(old,freshActor,command);
    const queries=[db.prepare("INSERT INTO mutation_guards(id,valid) SELECT ?, CASE WHEN version=? THEN 1 ELSE 0 END FROM system_version WHERE id='global'").bind(command.id,old.revision)];
    for(const [key,table,keys] of TABLES){const previous=new Map(old[key].map(x=>[x.id,JSON.stringify(x)]));for(const item of s[key])if(previous.get(item.id)!==JSON.stringify(item))queries.push(writeRecord(db,table,keys,item));}
    queries.push(db.prepare("UPDATE system_version SET version=? WHERE id='global'").bind(s.revision),db.prepare("INSERT INTO commands(id,actor_id,fingerprint,result) VALUES(?,?,?,?)").bind(command.id,actor.id,fingerprint,JSON.stringify(result)),db.prepare("DELETE FROM mutation_guards WHERE id=?").bind(command.id));
    try{await db.batch(queries);return result;}catch(err){const msg=String(err);if(/concurrent_write_guard|UNIQUE constraint failed: commands|mutation_guards/.test(msg)&&attempt<3)continue;throw err;}
  }
  throw new DomainError("Data sedang diperbarui petugas lain. Coba kembali.",409);
}

async function upgradeProfiles(db:Database){
 const [result]=await db.batch([db.prepare("SELECT u.id,u.payload FROM users u LEFT JOIN user_emails e ON e.user_id=u.id WHERE e.user_id IS NULL")]);
 const statements=[];
 for(const row of result.results){const actor=JSON.parse(String(row.payload)) as Actor;actor.email=actor.email||`${actor.id}@unit-toko.demo`;actor.avatar=actor.avatar||`/images/avatars/${actor.id}.png`;
 statements.push(db.prepare("UPDATE users SET payload=? WHERE id=? AND payload=?").bind(JSON.stringify(actor),actor.id,row.payload),db.prepare("INSERT OR IGNORE INTO user_emails(email,user_id) VALUES(?,?)").bind(actor.email.toLowerCase(),actor.id));}
 if(statements.length)await db.batch(statements);
}

const CUSTOMER_DEMO_ID = "customer-demo";
const CUSTOMER_DEMO_EMAIL = "customer@unit-toko.demo";

/** Provision only a missing identity. Existing credentials, inactivity and profile edits are preserved. */
export async function provisionCustomerDemo(db:Database):Promise<boolean> {
  const existing = await db.prepare("SELECT payload,role,division_id FROM users WHERE id=?").bind(CUSTOMER_DEMO_ID).first<{payload:string;role:string;division_id:string|null}>();
  const emailOwner = await db.prepare("SELECT user_id FROM user_emails WHERE email=?").bind(CUSTOMER_DEMO_EMAIL).first<{user_id:string}>();
  if (emailOwner && emailOwner.user_id !== CUSTOMER_DEMO_ID) throw new Error("Customer demo email belongs to another account; provisioning stopped.");
  if (existing) {
    const actor = JSON.parse(existing.payload) as Actor;
    const credential = await db.prepare("SELECT user_id FROM credentials WHERE user_id=?").bind(CUSTOMER_DEMO_ID).first();
    if (existing.role !== "customer" || existing.division_id !== null || actor.id !== CUSTOMER_DEMO_ID || actor.role !== "customer" || actor.divisionId !== null || actor.email?.toLowerCase() !== CUSTOMER_DEMO_EMAIL || !emailOwner || !credential) {
      throw new Error("Customer demo identity is inconsistent; provisioning stopped without changing credentials or profile.");
    }
    return false;
  }
  const account = DEMO_ACCOUNTS.find(actor => actor.id === CUSTOMER_DEMO_ID);
  if (!account || account.role !== "customer") throw new Error("Customer demo account definition is missing.");
  const actor:Actor = {id:account.id, name:account.name, role:account.role, divisionId:null, active:true, email:CUSTOMER_DEMO_EMAIL, phone:"", address:"", position:"Pelanggan"};
  const salt = crypto.randomUUID(), hash = await hashPassword(demoPassword(actor.id), salt);
  try {
    await db.batch([
      db.prepare("INSERT INTO users(id,payload,division_id,role) VALUES(?,?,NULL,?)").bind(actor.id, JSON.stringify(actor), actor.role),
      db.prepare("INSERT INTO user_emails(email,user_id) VALUES(?,?)").bind(CUSTOMER_DEMO_EMAIL, actor.id),
      db.prepare("INSERT INTO credentials(user_id,salt,hash) VALUES(?,?,?)").bind(actor.id, salt, hash),
      db.prepare("UPDATE system_version SET version=version+1 WHERE id='global'"),
    ]);
    return true;
  } catch (error) {
    // A simultaneous initializer may have inserted this exact identity. Validate it
    // without treating an unrelated email/ID conflict as a successful initialization.
    if (!await db.prepare("SELECT id FROM users WHERE id=?").bind(CUSTOMER_DEMO_ID).first()) throw error;
    await provisionCustomerDemo(db);
    return false;
  }
}
