import { createClient, type Client, type InValue } from "@libsql/client";
import { readFile } from "node:fs/promises";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

export interface Statement {
  bind(...values:unknown[]):Statement;
  first<T=Record<string,unknown>>():Promise<T|null>;
  run():Promise<unknown>;
}
export interface Database { prepare(sql:string):Statement; batch(statements:Statement[]):Promise<{results:Record<string,unknown>[]}[]> }
let client:Client|undefined;
let initialized:Promise<void>|undefined;
let testDatabase:Database|undefined;
export function setTestDatabase(db:Database){if(process.env.NODE_ENV==="production")throw new Error("Test adapter unavailable");testDatabase=db;}
function connection(){
  if(client)return client;
  const url=process.env.TURSO_DATABASE_URL||"file:.data/unit-toko.db";
  if(process.env.VERCEL&&(!url.startsWith("libsql://")||!process.env.TURSO_AUTH_TOKEN))throw new Error("Configure remote TURSO_DATABASE_URL and TURSO_AUTH_TOKEN for Vercel.");
  if(url.startsWith("file:"))mkdirSync(".data",{recursive:true});
  return client=createClient({url,authToken:process.env.TURSO_AUTH_TOKEN});
}
/** Exposed for isolated migration tests; callers must never point tests at a live database. */
export async function migrateDatabase(db:Client){
    await db.execute("CREATE TABLE IF NOT EXISTS app_migrations (id TEXT PRIMARY KEY)");
    for(const id of ["0000_common_wallflower.sql","0001_auth.sql","0002_customer_buyers.sql"]){
      if((await db.execute({sql:"SELECT id FROM app_migrations WHERE id=?",args:[id]})).rows.length)continue;
      if(id==="0000_common_wallflower.sql"&&(await db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='system_version'")).rows.length){
        await db.execute("INSERT OR IGNORE INTO app_migrations(id) VALUES('0000_common_wallflower.sql')");continue;
      }
      const sql=await readFile(join(process.cwd(),"drizzle",id),"utf8");
      const statements=sql.split(";").map(s=>s.trim()).filter(Boolean).map(sql=>({sql,args:[]}));
      const migration=[...statements,{sql:"INSERT INTO app_migrations(id) VALUES(?)",args:[id]}];
      // libSQL migrate toggles foreign keys on the same connection around an atomic
      // transaction. Ordinary batch cannot safely rebuild referenced parent tables.
      try {if(id==="0002_customer_buyers.sql")await db.migrate(migration);else await db.batch(migration,"write");}
      catch(error){if(!(await db.execute({sql:"SELECT id FROM app_migrations WHERE id=?",args:[id]})).rows.length)throw error;}
    }
}
export async function initializeDatabase(){
  if(testDatabase)return;
  if(!initialized)initialized=migrateDatabase(connection()).catch(error=>{initialized=undefined;throw error;});
  return initialized;
}
class Query implements Statement {
  constructor(public sql:string,public args:InValue[]=[]){ }
  bind(...values:unknown[]){return new Query(this.sql,values as InValue[]);}
  async first<T>(){await initializeDatabase();return ((await connection().execute(this)).rows[0] as T)||null;}
  async run(){await initializeDatabase();return connection().execute(this);}
}
const adapter:Database={prepare:sql=>new Query(sql),async batch(statements){await initializeDatabase();return (await connection().batch(statements as Query[],"write")).map(r=>({results:r.rows as Record<string,unknown>[]}));}};
export function database(){return testDatabase||adapter;}
