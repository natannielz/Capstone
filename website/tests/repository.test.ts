import { test } from "node:test";
import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { readFileSync,mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTestDatabase } from "../lib/server/database";
import type { Statement } from "../lib/server/database";
process.env.DEMO_PASSWORD_SEED="repository-test-only-not-a-production-secret";
import { execute,loadState,ensureSeed } from "../lib/server/repository";
import { invoiceTotal,invoiceBalance,paymentAvailable,scopeState } from "../lib/domain/selectors";
import { documentHTML,reportCSV } from "../lib/server/documents";
import type { Actor } from "../lib/domain/accounts";

// Exercise the real repository SQL/transactions against SQLite using D1's batch contract.
const path=join(mkdtempSync(join(tmpdir(),"unit-toko-test-")),"database.sqlite");let connection=new DatabaseSync(path);connection.exec(readFileSync("drizzle/0000_common_wallflower.sql","utf8"));
class Query implements Statement{values:unknown[]=[];constructor(public sql:string){}bind(...values:unknown[]){this.values=values;return this;}async first<T=Record<string,unknown>>(){await Promise.resolve();return (connection.prepare(this.sql).get(...this.values as [])||null) as T|null;}async run(){return connection.prepare(this.sql).run(...this.values as []);}allSync(){const stmt=connection.prepare(this.sql);return {results:stmt.all(...this.values as [])};}}
connection.exec(readFileSync("drizzle/0001_auth.sql","utf8"));
// Mirror libSQL Client.migrate(): disable FK enforcement outside the transaction
// while rebuilding referenced tables, then restore it whether commit succeeds or not.
connection.exec("PRAGMA foreign_keys=OFF; BEGIN");
try {
 connection.exec(readFileSync("drizzle/0002_customer_buyers.sql","utf8"));
 connection.exec("COMMIT");
} catch(error) {
 connection.exec("ROLLBACK");
 throw error;
} finally {
 connection.exec("PRAGMA foreign_keys=ON");
}
assert.equal(connection.prepare("PRAGMA foreign_key_check").all().length,0);
setTestDatabase({prepare:(sql:string)=>new Query(sql),async batch(queries:Statement[]){await Promise.resolve();connection.exec("BEGIN");try{const result=queries.map(q=>{if(!(q instanceof Query))throw new Error("Unsupported test statement");return q.allSync();});connection.exec("COMMIT");return result;}catch(e){connection.exec("ROLLBACK");throw e;}}});
let actors:Actor[]=[];const actor=(id:string)=>actors.find(a=>a.id===id)!;const command=(type:string,data:Record<string,unknown>)=>({id:crypto.randomUUID(),type,data});const act=(who:string,type:string,data:Record<string,unknown>)=>execute(actor(who),command(type,data));
test("U05/U06/U11/U13/U15 repository SQL atomik, idempotensi, sesi data dan dokumen",async()=>{
 await ensureSeed();let s=await loadState();actors=s.users;
 const product=s.products.find(p=>p.id==="kopi")||s.products[0],available=s.batches.filter(b=>b.productId===product.id).reduce((n,b)=>n+b.qty-b.held,0)-s.reservations.filter(r=>s.batches.find(b=>b.id===r.batchId)?.productId===product.id).reduce((n,r)=>n+r.qty,0);
 const order=await act("pic-a","order.create",{neededAt:"2026-09-30",lines:[{productId:product.id,qty:available}]});await act("kepala","order.review",{id:order.id,approve:true});s=await loadState();const line=s.orderLines.find(l=>l.orderId===order.id)!;
 const race=await Promise.allSettled([act("staf","stock.reserve",{orderLineId:line.id,qty:available}),act("staf","stock.reserve",{orderLineId:line.id,qty:available})]);assert.equal(race.filter(r=>r.status==="fulfilled").length,1);s=await loadState();assert.equal(s.reservations.filter(r=>r.orderLineId===line.id).reduce((n,r)=>n+r.qty,0),available);
 const sh=await act("staf","shipment.create",{orderId:order.id,courierId:"kurir",lines:[{orderLineId:line.id,qty:2}]});const dispatch=command("shipment.dispatch",{id:sh.id});const twice=await Promise.all([execute(actor("staf"),dispatch),execute(actor("staf"),dispatch)]);assert.deepEqual(twice[0],twice[1]);s=await loadState();assert.equal(s.movements.filter(m=>m.sourceId===sh.id&&m.type==="dispatch").length,1);await assert.rejects(()=>execute(actor("staf"),{...dispatch,data:{id:"different"}}),/berbeda/);
 const sl=s.shipmentLines.find(l=>l.shipmentId===sh.id)!;await act("pic-a","shipment.receive",{id:sh.id,lines:[{id:sl.id,accepted:2}]});const final=command("sale.finalize",{id:sh.id});await Promise.all([execute(actor("staf"),final),execute(actor("staf"),final)]);s=await loadState();assert.equal(s.journals.filter(j=>j.sourceId===sh.id).length,1);
 const bill=command("invoice.issue",{divisionId:"div-ops",dueDate:"2026-10-30",lines:[{shipmentLineId:sl.id}]});const [inv,repeat]=await Promise.all([execute(actor("penagihan"),bill),execute(actor("penagihan"),bill)]);assert.equal(inv.id,repeat.id);
 const p=await act("penagihan","payment.record",{divisionId:"div-ops",amount:1000000,payer:"Uji"});const verify=command("payment.verify",{id:p.id});await Promise.all([execute(actor("penagihan"),verify),execute(actor("penagihan"),verify)]);s=await loadState();const total=invoiceTotal(s,inv.id);const allocations=await Promise.allSettled([act("penagihan","payment.allocate",{paymentId:p.id,lines:[{invoiceId:inv.id,amount:total}]}),act("penagihan","payment.allocate",{paymentId:p.id,lines:[{invoiceId:inv.id,amount:total}]})]);assert.equal(allocations.filter(r=>r.status==="fulfilled").length,1);s=await loadState();assert.equal(invoiceBalance(s,inv.id),0);assert.equal(paymentAvailable(s,p.id),1000000-total);
 const version=s.revision;connection.close();connection=new DatabaseSync(path);const reopened=await loadState();assert.equal(reopened.revision,version);assert.equal(reopened.invoices.find(i=>i.id===inv.id)?.id,inv.id);
 const printable=documentHTML(reopened,actor("pic-a"),"invoice",inv.id);assert.ok(printable.includes(reopened.invoices.find(i=>i.id===inv.id)!.number));assert.ok(printable.includes(product.name));assert.throws(()=>documentHTML(reopened,actor("pic-b"),"invoice",inv.id),/ditemukan/);assert.throws(()=>documentHTML(reopened,actor("kurir"),"report","2026-09-30"),/akses/);assert.ok(reportCSV(reopened,"2099-12-31").includes("Penjualan"));assert.equal(scopeState(reopened,actor("pic-b")).orders.some(o=>o.id===order.id),false);
 const aid=crypto.randomUUID();await act("penagihan","attachment.add",{attachmentId:aid,scope:"payment",targetId:p.id,name:"bukti.pdf",mime:"application/pdf",size:100});const withAttachment=await loadState();assert.equal(scopeState(withAttachment,actor("pic-b")).attachments.some(a=>a.id===aid),false);await assert.rejects(()=>act("pic-b","attachment.add",{attachmentId:crypto.randomUUID(),scope:"payment",targetId:p.id,name:"salah.pdf",mime:"application/pdf",size:100}),/divisi lain/);connection.close();
});
