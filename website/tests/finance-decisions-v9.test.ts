import {test} from "node:test";
import assert from "node:assert/strict";
import {DatabaseSync} from "node:sqlite";
import {readFileSync} from "node:fs";
import {DEMO_ACCOUNTS, type Role} from "../lib/domain/accounts";
import {runCommand, journal} from "../lib/domain/engine";
import {emptyState} from "../lib/domain/seed";
import {attachmentContext, financialReport, invoiceBalance, paymentAvailable, scopeState} from "../lib/domain/selectors";
import {setTestDatabase, type Statement} from "../lib/server/database";
import {ensureSeed, execute, loadState} from "../lib/server/repository";

function fixture() {
  let state=emptyState();state.users=structuredClone(DEMO_ACCOUNTS);
  state.divisions=[{id:"div-ops",name:"Ops",code:"OPS",address:"Jakarta"},{id:"div-ti",name:"TI",code:"TI",address:"Jakarta"}];
  state.products=[{id:"p",name:"Barang",sku:"P",category:"OMI",unit:"pcs",price:10000,minimum:1,returnMonths:1,active:true}];
  state.batches=[{id:"b",productId:"p",code:"B",qty:20,held:0,cost:6000,expiry:"2028-01-01",location:"Toko"}];
  journal(state,"2026-09-01","opening","Awal",[["inventory",120000,0],["capital",0,120000]]);
  return {get s(){return state;},act(who:string,type:string,data:Record<string,unknown>,date="2026-09-22") {
    const result=runCommand(state,state.users.find(u=>u.id===who)!,{id:crypto.randomUUID(),type,data,date});state=result.state;return result.result.id;
  }};
}
type Fixture=ReturnType<typeof fixture>;
function deny(f:Fixture,call:()=>unknown,pattern:RegExp) {const before=structuredClone(f.s);assert.throws(call,pattern);assert.deepEqual(f.s,before,"rejection must be atomic");}
function payment(f:Fixture,verified=true) {
  const id=f.act("pic-a","payment.record",{amount:100000,payer:"Ops",reference:"V9-DEMO"});
  if(verified)f.act("penagihan","payment.verify",{id});return id;
}
function invoice(f:Fixture) {
  const order=f.act("pic-a","order.create",{neededAt:"2026-09-22",lines:[{productId:"p",qty:2}]});
  f.act("kepala","order.review",{id:order,approve:true});const line=f.s.orderLines.find(l=>l.orderId===order)!;
  f.act("staf","stock.reserve",{orderLineId:line.id,qty:2});
  const sh=f.act("staf","shipment.create",{orderId:order,courierId:"kurir",lines:[{orderLineId:line.id,qty:2}]});
  f.act("kurir","shipment.dispatch",{id:sh});
  f.act("pic-a","shipment.receive",{id:sh,lines:f.s.shipmentLines.filter(l=>l.shipmentId===sh).map(l=>({id:l.id,accepted:l.qty}))});
  f.act("staf","sale.finalize",{id:sh});
  return f.act("penagihan","invoice.issue",{divisionId:"div-ops",dueDate:"2026-10-22",lines:f.s.shipmentLines.filter(l=>l.shipmentId===sh).map(l=>({shipmentLineId:l.id}))});
}
function expense(f:Fixture) {
  invoice(f);return f.act("staf","expense.create",{shipmentId:f.s.shipments[0].id,amount:10000,category:"Parkir",description:"Parkir pengiriman"});
}
function assertOnlyRole(f:Fixture,type:string,data:Record<string,unknown>,role:Role) {
  for(const actor of f.s.users.filter(u=>u.role!==role))deny(f,()=>f.act(actor.id,type,data),/akses/);
}

test("v9 recorded payment rejection has strict roles, reason, chronology and unchanged balances",()=>{
  const f=fixture(),id=payment(f,false),journalBefore=structuredClone(f.s.journals);
  assertOnlyRole(f,"payment.reject",{id,reason:"Referensi tidak cocok"},"penagihan");
  deny(f,()=>f.act("penagihan","payment.reject",{id,reason:"  "}),/Alasan/);
  deny(f,()=>f.act("penagihan","payment.reject",{id,reason:"Salah"},"2026-09-21"),/mendahului/);
  f.act("penagihan","payment.reject",{id,reason:"Referensi tidak cocok"});
  const p=f.s.payments.find(p=>p.id===id)!;
  assert.equal(p.status,"rejected");assert.equal(p.rejectionReason,"Referensi tidak cocok");assert.equal(p.rejectedBy,"penagihan");assert.equal(p.rejectedDate,"2026-09-22");
  assert.equal(paymentAvailable(f.s,id),0);assert.deepEqual(f.s.journals,journalBefore);assert.equal(f.s.allocations.length,0);
  assert.match(f.s.audits.at(-1)!.description,/Referensi tidak cocok/);assert.equal(f.s.audits.at(-1)!.actorId,"penagihan");
  assert.equal(scopeState(f.s,f.s.users.find(u=>u.id==="pic-a")!).payments[0].rejectionReason,"Referensi tidak cocok");
  assert.equal(scopeState(f.s,f.s.users.find(u=>u.id==="pic-b")!).payments.length,0);
  for(const type of ["payment.verify","payment.reject","payment.identify"])deny(f,()=>f.act("penagihan",type,{id,reason:"Ulang",divisionId:"div-ops"}),/ditolak|menunggu/);
  deny(f,()=>f.act("penagihan","payment.allocate",{paymentId:id,lines:[]}),/Verifikasi/);
  assert.throws(()=>attachmentContext(f.s,f.s.users.find(u=>u.id==="pic-a")!,"payment",id),/catatan pembayaran baru/);
  const replacement=payment(f);assert.equal(paymentAvailable(f.s,replacement),100000);assert.equal(f.s.payments.length,2);
  deny(f,()=>f.act("penagihan","payment.reject",{id:replacement,reason:"Tidak boleh"}),/menunggu/);
});

test("v9 rejected credit retains invoice and requires an independent Pimpinan decision",()=>{
  const f=fixture(),inv=invoice(f),id=f.act("penagihan","credit.request",{invoiceId:inv,amount:5000,reason:"Koreksi salah"});
  const before=financialReport(f.s,"2026-09-30");
  assertOnlyRole(f,"credit.reject",{id,reason:"Tidak sesuai penerimaan"},"pimpinan");
  deny(f,()=>f.act("pimpinan","credit.reject",{id,reason:""}),/Alasan/);
  f.act("pimpinan","credit.reject",{id,reason:"Tidak sesuai penerimaan"});
  assert.equal(invoiceBalance(f.s,inv),20000);assert.deepEqual(financialReport(f.s,"2026-09-30"),before);
  assert.equal(f.s.credits[0].rejectedBy,"pimpinan");assert.equal(f.s.credits[0].date,"2026-09-22");
  deny(f,()=>f.act("pimpinan","credit.approve",{id}),/diproses/);
  deny(f,()=>f.act("pimpinan","credit.reject",{id,reason:"Ulang"}),/diproses/);
  const next=f.act("penagihan","credit.request",{invoiceId:inv,amount:3000,reason:"Koreksi benar"});f.act("pimpinan","credit.approve",{id:next});
  assert.equal(invoiceBalance(f.s,inv),17000);deny(f,()=>f.act("pimpinan","credit.reject",{id:next,reason:"Ulang"}),/diproses/);
});

test("v9 stale refund requests can be rejected without reserving or releasing other approved funds",()=>{
  const f=fixture(),p=payment(f),first=f.act("penagihan","refund.request",{paymentId:p,amount:70000,reason:"Permintaan sah"}),stale=f.act("penagihan","refund.request",{paymentId:p,amount:100000,reason:"Duplikat"});
  f.act("pimpinan","refund.approve",{id:first});
  deny(f,()=>f.act("pimpinan","refund.approve",{id:stale}),/Dana telah dipakai/);
  assertOnlyRole(f,"refund.reject",{id:stale,reason:"Duplikat pengajuan"},"pimpinan");
  deny(f,()=>f.act("pimpinan","refund.reject",{id:stale,reason:""}),/Alasan/);
  const before=financialReport(f.s,"2026-09-30");f.act("pimpinan","refund.reject",{id:stale,reason:"Duplikat pengajuan"});
  assert.equal(paymentAvailable(f.s,p),30000);assert.deepEqual(financialReport(f.s,"2026-09-30"),before);
  deny(f,()=>f.act("pimpinan","refund.approve",{id:stale}),/diputuskan/);deny(f,()=>f.act("penagihan","refund.pay",{id:stale}),/belum disetujui/);
  deny(f,()=>f.act("pimpinan","refund.reject",{id:first,reason:"Batal"}),/diputuskan/);
  f.act("penagihan","refund.pay",{id:first});assert.equal(paymentAvailable(f.s,p),30000);
});

test("v9 expense rejection retains evidence history and cannot be paid or approved again",()=>{
  const f=fixture(),id=expense(f),before=financialReport(f.s,"2026-09-30");
  f.act("staf","attachment.add",{scope:"expense",targetId:id,attachmentId:"proof",name:"parkir.pdf",mime:"application/pdf",size:20});
  assertOnlyRole(f,"expense.reject",{id,reason:"Rincian belum sesuai"},"pimpinan");
  deny(f,()=>f.act("pimpinan","expense.reject",{id,reason:""}),/Alasan/);
  f.act("pimpinan","expense.reject",{id,reason:"Rincian belum sesuai"});
  assert.deepEqual(financialReport(f.s,"2026-09-30"),before);assert.equal(f.s.attachments.length,1);
  assert.equal(scopeState(f.s,f.s.users.find(u=>u.id==="staf")!).expenses[0].rejectionReason,"Rincian belum sesuai");
  assert.throws(()=>attachmentContext(f.s,f.s.users.find(u=>u.id==="staf")!,"expense",id),/pengajuan baru/);
  deny(f,()=>f.act("pimpinan","expense.approve",{id}),/diproses/);deny(f,()=>f.act("penagihan","expense.pay",{id}),/belum disetujui/);
});

test("v9 changing the requester's role cannot bypass independent financial review",()=>{
  for(const kind of ["credit","refund","expense"] as const){
    const f=fixture();let id:string;const requester=kind==="expense"?"staf":"penagihan";
    if(kind==="credit")id=f.act(requester,"credit.request",{invoiceId:invoice(f),amount:1000,reason:"Koreksi"});
    else if(kind==="refund")id=f.act(requester,"refund.request",{paymentId:payment(f),amount:1000,reason:"Pengembalian"});
    else id=expense(f);
    f.s.users.find(u=>u.id===requester)!.role="pimpinan";
    for(const action of ["approve","reject"])deny(f,()=>f.act(requester,`${kind}.${action}`,{id,reason:"Uji"}),/pengguna lain/);
  }
  const f=fixture();f.act("laporan","period.submit",{month:"2026-09"});f.s.users.find(u=>u.id==="laporan")!.role="pimpinan";
  for(const type of ["period.approve","period.return"])deny(f,()=>f.act("laporan",type,{id:"2026-09",revision:f.s.periods[0].revision,reason:"Koreksi"}),/pengguna lain/);
});

test("v9 returned report requires a current version and a new review before Accounting can close",()=>{
  const f=fixture();payment(f);f.act("laporan","period.submit",{month:"2026-09"});
  const revision=f.s.periods[0].revision,data={id:"2026-09",revision,reason:"Periksa referensi pembayaran"};
  assertOnlyRole(f,"period.return",data,"pimpinan");
  deny(f,()=>f.act("pimpinan","period.return",{...data,revision:revision-1}),/berubah/);
  deny(f,()=>f.act("pimpinan","period.return",{id:data.id,reason:data.reason}),/Versi laporan/);
  deny(f,()=>f.act("pimpinan","period.return",{...data,reason:""}),/Catatan/);
  deny(f,()=>f.act("pimpinan","period.return",data,"2026-09-21"),/mendahului/);
  const report=financialReport(f.s,"2026-09-30");f.act("pimpinan","period.return",data);
  assert.equal(f.s.periods[0].status,"open");assert.equal(f.s.periods[0].returnReason,data.reason);assert.equal(f.s.periods[0].returnedBy,"pimpinan");assert.equal(f.s.periods[0].returnedRevision,revision);
  assert.deepEqual(financialReport(f.s,"2026-09-30"),report);
  deny(f,()=>f.act("akuntansi","period.close",{id:data.id}),/belum disetujui/);
  deny(f,()=>f.act("pimpinan","period.approve",{id:data.id}),/belum diajukan/);
  f.act("penagihan","period.submit",{month:data.id});
  deny(f,()=>f.act("pimpinan","period.return",data),/berubah/);
  deny(f,()=>f.act("pimpinan","period.approve",data),/berubah/);
  const current=f.s.periods[0].revision;f.act("pimpinan","period.approve",{id:data.id,revision:current});
  deny(f,()=>f.act("akuntansi","period.close",data),/berubah/);
  f.act("akuntansi","period.close",{id:data.id,revision:current});assert.equal(f.s.periods[0].status,"closed");assert.deepEqual(f.s.periods[0].snapshot,financialReport(f.s,"2026-09-31"));
  deny(f,()=>f.act("pimpinan","period.return",{...data,revision:current}),/berubah/);
});

test("v9 financial rejection respects a closed period and cannot change its saved snapshot",()=>{
  const f=fixture(),p=payment(f,false);f.act("laporan","period.submit",{month:"2026-09"});f.act("pimpinan","period.approve",{id:"2026-09"});f.act("akuntansi","period.close",{id:"2026-09"});
  const snapshot=structuredClone(f.s.periods[0].snapshot);
  deny(f,()=>f.act("penagihan","payment.reject",{id:p,reason:"Salah"}),/Periode sudah ditutup/);
  f.act("penagihan","payment.reject",{id:p,reason:"Koreksi bukti"},"2026-10-01");assert.deepEqual(f.s.periods[0].snapshot,snapshot);
});

test("v9 concurrent retry persists one financial rejection and retains metadata across reload",async()=>{
  process.env.DEMO_PASSWORD_SEED="v9-finance-isolated-test-seed-not-production";
  const db=new DatabaseSync(":memory:");
  for(const file of ["0000_common_wallflower.sql","0001_auth.sql","0002_customer_buyers.sql"])db.exec(readFileSync(`drizzle/${file}`,"utf8"));
  class Query implements Statement {
    values:unknown[]=[];constructor(readonly sql:string){}
    bind(...values:unknown[]){this.values=values;return this;}
    async first<T>(){await Promise.resolve();return (db.prepare(this.sql).get(...this.values as [])||null) as T|null;}
    async run(){return db.prepare(this.sql).run(...this.values as []);}
  }
  setTestDatabase({prepare:sql=>new Query(sql),async batch(queries){await Promise.resolve();db.exec("BEGIN");try{const result=queries.map(q=>{const query=q as Query;return {results:db.prepare(query.sql).all(...query.values as [])};});db.exec("COMMIT");return result;}catch(error){db.exec("ROLLBACK");throw error;}}});
  try {
    await ensureSeed();let s=await loadState();const actor=s.users.find(u=>u.id==="penagihan")!,p=s.payments.find(p=>p.status==="recorded")!;
    const command={id:crypto.randomUUID(),type:"payment.reject",data:{id:p.id,reason:"Referensi belum cocok"}};
    const [first,repeat]=await Promise.all([execute(actor,command),execute(actor,command)]);assert.deepEqual(first,repeat);
    s=await loadState();assert.equal(s.payments.find(x=>x.id===p.id)!.rejectionReason,"Referensi belum cocok");assert.equal(s.audits.filter(a=>a.action==="payment.reject"&&a.targetId===p.id).length,1);
    await assert.rejects(()=>execute(actor,{...command,data:{...command.data,reason:"Berbeda"}}),/berbeda/);
    await assert.rejects(()=>execute(actor,{...command,id:crypto.randomUUID()}),/menunggu verifikasi/);
    assert.equal((await loadState()).revision,s.revision);
  } finally {db.close();}
});
