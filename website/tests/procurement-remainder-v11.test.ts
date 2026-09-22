import {test} from "node:test";
import assert from "node:assert/strict";
import {DEMO_ACCOUNTS} from "../lib/domain/accounts";
import {runCommand,validateState} from "../lib/domain/engine";
import type {Purchase} from "../lib/domain/model";
import {emptyState} from "../lib/domain/seed";
import {purchaseSummary} from "../lib/domain/procurement";
import {documentHTML} from "../lib/server/documents";

function fixture(kind:Purchase["kind"]="DDO") {
  let state=emptyState();state.users=structuredClone(DEMO_ACCOUNTS);
  state.products=[{id:"item",sku:"ITEM",name:"Produk uji",category:"OMI",unit:"dus",price:2000,minimum:1,returnMonths:1,active:true}];
  state.suppliers=[{id:"vendor",name:"Pemasok",category:"OMI"}];
  const f={get s(){return state;},act(who:string,type:string,data:Record<string,unknown>,date="2026-09-22"){
    const outcome=runCommand(state,state.users.find(u=>u.id===who)!,{id:crypto.randomUUID(),type,data,date});state=outcome.state;return outcome.result.id;
  }};
  const id=f.act("kepala","purchase.create",{kind,supplierId:"vendor",lines:[{productId:"item",qty:30,cost:1000}]});
  const lineId=f.s.purchaseLines[0].id;
  return {...f,get s(){return f.s;},id,lineId,
    closeData(){return {id,reason:"Pemasok hanya mampu mengirim sebagian",lines:f.s.purchaseLines.map(l=>({purchaseLineId:l.id,received:l.received,cancelled:l.cancelled??0}))};},
    receive(qty:number,date="2026-09-22"){return f.act("staf","purchase.receive",{id,lines:[{purchaseLineId:lineId,qty}]},date);}
  };
}
type Fixture=ReturnType<typeof fixture>;
function denied(f:Fixture,action:()=>unknown,pattern:RegExp){const before=structuredClone(f.s);assert.throws(action,pattern);assert.deepEqual(f.s,before,"failed command must preserve all state");}
function ready(f:Fixture){if(f.s.purchases[0].kind==="Pasar Kering"){
  f.act("kepala","funding.request",{id:f.id,amount:35000});f.act("pimpinan","funding.approve",{id:f.id});f.act("penagihan","funding.disburse",{id:f.id});
}f.act("kepala","purchase.confirm",{id:f.id});}

test("v11 partial supplier closure preserves original quantities, received stock, journals and payments on all routes",()=>{
  for(const kind of ["OMI reguler","DDO","Smart","Pasar Kering"] as const){
    const f=fixture(kind);ready(f);f.receive(10);
    const payer=["OMI reguler","DDO"].includes(kind)?"staf":"kepala";
    f.act(payer,"supplier.pay",{purchaseId:f.id,amount:4000,reference:"SPH-1"});
    const before=structuredClone({batches:f.s.batches,movements:f.s.movements,journals:f.s.journals,journalLines:f.s.journalLines,payments:f.s.supplierPayments});
    f.act("kepala","purchase.close",f.closeData());
    assert.deepEqual({batches:f.s.batches,movements:f.s.movements,journals:f.s.journals,journalLines:f.s.journalLines,payments:f.s.supplierPayments},before);
    assert.equal(f.s.purchaseLines[0].qty,30);assert.equal(f.s.purchaseLines[0].received,10);assert.equal(f.s.purchaseLines[0].cancelled,20);
    assert.equal(f.s.purchases[0].closedBy,"kepala");assert.equal(f.s.purchases[0].closedDate,"2026-09-22");assert.equal(f.s.audits.at(-1)!.action,"purchase.close");
    assert.equal(purchaseSummary(f.s,f.s.purchases[0]).remainingQty,0);
    denied(f,()=>f.receive(1),/belum dipesan|lengkap/);
    denied(f,()=>f.act("kepala","purchase.close",f.closeData()),/lengkap|ditutup/);
    denied(f,()=>f.act(payer,"supplier.pay",{purchaseId:f.id,amount:6001,reference:"SPH-2"}),/melebihi/);
    f.act(payer,"supplier.pay",{purchaseId:f.id,amount:6000,reference:"SPH-2"});assert.equal(purchaseSummary(f.s,f.s.purchases[0]).unpaid,0);
    denied(f,()=>f.act(payer,"supplier.pay",{purchaseId:f.id,amount:1,reference:"SPH-3"}),/melebihi/);
  }
});

test("v11 cancellation of an unreceived legacy draft keeps history and rejects all further receipt/payment/funding actions",()=>{
  const f=fixture();assert.equal(f.s.purchaseLines[0].cancelled,undefined);
  f.act("kepala","purchase.close",f.closeData());
  assert.equal(f.s.purchaseLines[0].cancelled,30);assert.equal(purchaseSummary(f.s,f.s.purchases[0]).retained,0);assert.equal(f.s.journals.length,0);
  denied(f,()=>f.act("staf","purchase.confirm",{id:f.id}),/dikonfirmasi/);
  denied(f,()=>f.act("staf","supplier.pay",{purchaseId:f.id,amount:1,reference:"Wrong"}),/melebihi/);
  denied(f,()=>f.receive(1),/belum dipesan|lengkap/);
});

test("v11 closure requires Kepala, a reason and a current complete view of received quantities",()=>{
  const f=fixture();ready(f);const old=f.closeData();f.receive(10);
  for(const actor of f.s.users.filter(a=>a.role!=="kepala"))denied(f,()=>f.act(actor.id,"purchase.close",f.closeData()),/akses/);
  denied(f,()=>f.act("kepala","purchase.close",{...f.closeData(),reason:" "}),/Alasan/);
  denied(f,()=>f.act("kepala","purchase.close",old),/berubah/);
  denied(f,()=>f.act("kepala","purchase.close",{...f.closeData(),lines:[]}),/baris/);
  denied(f,()=>f.act("kepala","purchase.close",{...f.closeData(),lines:[{purchaseLineId:"other",received:10,cancelled:0}]}),/berubah/);
  const duplicate=f.closeData();duplicate.lines.push(duplicate.lines[0]);denied(f,()=>f.act("kepala","purchase.close",duplicate),/seluruh baris/);
});

test("v11 an unapplied supplier advance blocks closure until legitimate receipts consume it",()=>{
  const f=fixture("Smart");ready(f);f.act("kepala","supplier.pay",{purchaseId:f.id,amount:20000,reference:"DP"});f.receive(10);
  denied(f,()=>f.act("kepala","purchase.close",f.closeData()),/uang muka/);
  assert.equal(f.s.supplierPayments[0].applied,10000);f.receive(10);f.act("kepala","purchase.close",f.closeData());
  assert.equal(f.s.purchaseLines[0].cancelled,10);assert.equal(f.s.supplierPayments[0].amount,20000);assert.equal(f.s.supplierPayments[0].applied,20000);
});

test("v11 closed Pasar Kering retains funding history and unpaid allocation without phantom cash movement",()=>{
  for(const stage of ["none","requested","approved","disbursed"] as const){
    const f=fixture("Pasar Kering");if(stage!=="none")f.act("kepala","funding.request",{id:f.id,amount:35000});
    if(["approved","disbursed"].includes(stage))f.act("pimpinan","funding.approve",{id:f.id});
    if(stage==="disbursed")f.act("penagihan","funding.disburse",{id:f.id});
    f.act("kepala","purchase.close",f.closeData());assert.equal(f.s.purchases[0].fundingStatus,stage);assert.equal(f.s.purchases[0].fundingAmount,stage==="none"?0:35000);assert.equal(f.s.journals.length,0);
    denied(f,()=>f.act("kepala","funding.request",{id:f.id,amount:35000}),/tidak dapat/);
    denied(f,()=>f.act("pimpinan","funding.approve",{id:f.id}),/ditutup|belum tersedia/);
    denied(f,()=>f.act("penagihan","funding.disburse",{id:f.id}),/ditutup|belum disetujui/);
  }
  const f=fixture("Pasar Kering");ready(f);f.receive(10);f.act("kepala","supplier.pay",{purchaseId:f.id,amount:4000,reference:"Cash"});f.act("kepala","purchase.close",f.closeData());
  const summary=purchaseSummary(f.s,f.s.purchases[0]);assert.equal(summary.unusedFunding,31000);assert.equal(summary.unpaid,6000);assert.equal(summary.advance,0);
});

test("v11 closure respects chronology, legacy audit dates and frozen periods",()=>{
  const f=fixture();f.act("kepala","purchase.confirm",{id:f.id},"2026-09-23");delete f.s.purchases[0].confirmedDate;
  denied(f,()=>f.act("kepala","purchase.close",f.closeData(),"2026-09-22"),/mendahului/);
  f.receive(10,"2026-09-24");denied(f,()=>f.act("kepala","purchase.close",f.closeData(),"2026-09-23"),/mendahului/);
  f.act("laporan","period.submit",{month:"2026-09"});f.act("pimpinan","period.approve",{id:"2026-09"});f.act("akuntansi","period.close",{id:"2026-09"});
  const snapshot=structuredClone(f.s.periods[0].snapshot);denied(f,()=>f.act("kepala","purchase.close",f.closeData(),"2026-09-25"),/Periode sudah ditutup/);
  f.act("kepala","purchase.close",f.closeData(),"2026-10-02");assert.deepEqual(f.s.periods[0].snapshot,snapshot);
  denied(f,()=>f.act("staf","supplier.pay",{purchaseId:f.id,amount:1000,reference:"late"},"2026-10-01"),/mendahului/);
});

test("v11 printable purchase shows cancelled remainder, retained totals, funding and escaped closure reason",()=>{
  const f=fixture("Pasar Kering");ready(f);f.receive(10);f.act("kepala","purchase.close",{...f.closeData(),reason:"<script>bad</script> pemasok tidak dapat melanjutkan"});
  const html=documentHTML(f.s,f.s.users.find(u=>u.id==="kepala")!,"purchase",f.id);
  assert.match(html,/Sisa ditutup/);assert.match(html,/Dibatalkan/);assert.match(html,/Nilai setelah pembatalan/);assert.match(html,/Alokasi belum dipakai/);assert.match(html,/&lt;script&gt;bad&lt;\/script&gt;/);assert.doesNotMatch(html,/<script>bad/);
  const broken=structuredClone(f.s);broken.purchaseLines[0].cancelled=21;assert.throws(()=>validateState(broken),/Sisa dibatalkan/);
  const untouched=fixture();ready(untouched);untouched.receive(30);assert.equal(untouched.s.purchases[0].status,"complete");denied(untouched,()=>untouched.act("kepala","purchase.close",untouched.closeData()),/lengkap/);
});
