import {test} from "node:test";
import assert from "node:assert/strict";
import {DEMO_ACCOUNTS} from "../lib/domain/accounts";
import {runCommand, journal} from "../lib/domain/engine";
import {emptyState} from "../lib/domain/seed";
import {financialReport, invoiceTotal, lineProgress, stocktakeStatus} from "../lib/domain/selectors";
import {matchesOrderQueue, orderFulfillmentLabel, substitutionNeedsDecision} from "../lib/domain/order-views";

function fixture() {
  let state = emptyState();
  state.users = structuredClone(DEMO_ACCOUNTS);
  state.divisions = [{id:"div-ops",name:"Ops",code:"OPS",address:"Jakarta"},{id:"div-ti",name:"TI",code:"TI",address:"Jakarta"}];
  state.products = [{id:"p",name:"Produk",sku:"P",category:"OMI",unit:"pcs",price:10000,minimum:2,returnMonths:1,active:true},{id:"q",name:"Pengganti",sku:"Q",category:"OMI",unit:"pcs",price:12000,minimum:2,returnMonths:1,active:true}];
  state.batches = [{id:"b",productId:"p",code:"B",qty:20,held:0,cost:6000,expiry:"2028-01-01",location:"Toko"},{id:"bq",productId:"q",code:"BQ",qty:20,held:0,cost:7000,expiry:"2028-01-01",location:"Toko"}];
  state.suppliers = [{id:"supplier",name:"Pemasok",category:"OMI"}];
  journal(state,"2026-09-01","opening","Saldo awal",[["inventory",260000,0],["capital",0,260000]]);
  return {
    get s() {return state;},
    act(who:string,type:string,data:Record<string,unknown>,date="2026-09-17") {
      const result=runCommand(state,state.users.find(user=>user.id===who)!,{id:crypto.randomUUID(),type,data,date});
      state=result.state;
      return result.result.id;
    },
  };
}
type Fixture = ReturnType<typeof fixture>;
function denied(f:Fixture,action:()=>unknown,pattern:RegExp) {
  const before=structuredClone(f.s);
  assert.throws(action,pattern);
  assert.deepEqual(f.s,before,"rejection must leave the entire state, audits and journals unchanged");
}
function order(f:Fixture,qty=4,date="2026-09-17") {
  const id=f.act("pic-a","order.create",{neededAt:date,lines:[{productId:"p",qty}]},date);
  return {id,lineId:f.s.orderLines.find(line=>line.orderId===id)!.id};
}
function ready(f:Fixture,qty=4,date="2026-09-17") {
  const o=order(f,qty,date);
  f.act("kepala","order.review",{id:o.id,approve:true},date);
  f.act("staf","stock.reserve",{orderLineId:o.lineId,qty},date);
  const shipmentId=f.act("staf","shipment.create",{orderId:o.id,courierId:"kurir",lines:[{orderLineId:o.lineId,qty}]},date);
  return {...o,shipmentId,shipmentLineId:f.s.shipmentLines.find(line=>line.shipmentId===shipmentId)!.id};
}
function purchase(f:Fixture,kind="DDO",date="2026-09-17") {
  const maker=["OMI reguler","DDO"].includes(kind)?"staf":"kepala";
  const id=f.act(maker,"purchase.create",{kind,supplierId:"supplier",lines:[{productId:"p",qty:2,cost:6000}]},date);
  return {id,maker,lineId:f.s.purchaseLines.find(line=>line.purchaseId===id)!.id};
}

test("v6 B02 order dates reject transitions before their prerequisite atomically",()=>{
  const f=fixture(),o=order(f);
  denied(f,()=>f.act("kepala","order.review",{id:o.id,approve:true},"2026-09-16"),/mendahului/);
  f.act("kepala","order.review",{id:o.id,approve:true},"2026-09-18");
  denied(f,()=>f.act("staf","stock.reserve",{orderLineId:o.lineId,qty:4}),/mendahului/);
  f.act("staf","stock.reserve",{orderLineId:o.lineId,qty:4},"2026-09-19");
  denied(f,()=>f.act("staf","shipment.create",{orderId:o.id,courierId:"kurir",lines:[{orderLineId:o.lineId,qty:4}]},"2026-09-18"),/mendahului/);
  denied(f,()=>f.act("pic-a","order.cancel",{id:o.id,reason:"Batal"},"2026-09-16"),/mendahului/);
});

test("v6 B02 valid backdated order through invoice stays supported",()=>{
  const f=fixture(),o=ready(f,4,"2026-09-05");
  f.act("kurir","shipment.dispatch",{id:o.shipmentId},"2026-09-05");
  f.act("pic-a","shipment.receive",{id:o.shipmentId,lines:[{id:o.shipmentLineId,accepted:4}]},"2026-09-05");
  f.act("staf","sale.finalize",{id:o.shipmentId},"2026-09-05");
  const invoice=f.act("penagihan","invoice.issue",{divisionId:"div-ops",dueDate:"2026-10-05",lines:[{shipmentLineId:o.shipmentLineId}]},"2026-09-05");
  assert.equal(invoiceTotal(f.s,invoice),40000);
  assert.equal(f.s.invoices[0].date,"2026-09-05");
  assert.equal(f.s.orders[0].reviewedDate,"2026-09-05");
  const report=financialReport(f.s,"2026-09-30");
  assert.equal(report.debit,report.credit);
});

test("v6 B02 legacy order dates use effective audit dates instead of wall-clock creation",()=>{
  const f=fixture(),o=order(f,4,"2026-09-05");
  delete f.s.orders[0].createdDate;
  denied(f,()=>f.act("kepala","order.review",{id:o.id,approve:true},"2026-09-04"),/mendahului/);
  f.act("kepala","order.review",{id:o.id,approve:true},"2026-09-06");
  delete f.s.orders[0].reviewedDate;
  delete f.s.orderLines[0].createdDate;
  denied(f,()=>f.act("staf","stock.reserve",{orderLineId:o.lineId,qty:4},"2026-09-05"),/mendahului/);
  f.act("staf","stock.reserve",{orderLineId:o.lineId,qty:4},"2026-09-07");
  delete f.s.reservations[0].date;
  denied(f,()=>f.act("staf","shipment.create",{orderId:o.id,courierId:"kurir",lines:[{orderLineId:o.lineId,qty:4}]},"2026-09-06"),/mendahului/);
});

test("v6 B02 substitute stock cannot be reserved before the PIC decision",()=>{
  const f=fixture(),o=order(f);
  f.act("kepala","order.review",{id:o.id,approve:true});
  f.act("staf","substitution.propose",{orderLineId:o.lineId,productId:"q",reason:"Pilihan pengganti"},"2026-09-18");
  const sub=f.s.substitutions[0].id;
  denied(f,()=>f.act("pic-a","substitution.decide",{id:sub,approve:true}),/mendahului/);
  f.act("pic-a","substitution.decide",{id:sub,approve:true},"2026-09-19");
  const replacement=f.s.substitutions[0].replacementLineId!;
  denied(f,()=>f.act("staf","stock.reserve",{orderLineId:replacement,qty:4},"2026-09-18"),/mendahului/);
  f.act("staf","stock.reserve",{orderLineId:replacement,qty:4},"2026-09-19");
});

test("v6 B02 failed delivery and returns follow the effective delivery dates",()=>{
  const f=fixture(),o=ready(f);
  f.act("kurir","shipment.dispatch",{id:o.shipmentId},"2026-09-18");
  denied(f,()=>f.act("kurir","shipment.proof",{id:o.shipmentId,receiver:"PIC",evidence:"Bukti"}),/mendahului/);
  denied(f,()=>f.act("kurir","shipment.fail",{id:o.shipmentId,reason:"Penerima tidak tersedia"}),/mendahului/);
  f.act("kurir","shipment.fail",{id:o.shipmentId,reason:"Penerima tidak tersedia"},"2026-09-19");
  denied(f,()=>f.act("staf","shipment.return",{shipmentLineId:o.shipmentLineId,qty:4,good:true,reason:"Kembali"},"2026-09-18"),/mendahului/);
  f.act("staf","shipment.return",{shipmentLineId:o.shipmentLineId,qty:4,good:true,reason:"Kembali"},"2026-09-19");
  assert.equal(f.s.batches[0].qty,20);
});

test("v6 B02 finalization cannot precede complaint resolution",()=>{
  const f=fixture(),o=ready(f);
  f.act("kurir","shipment.dispatch",{id:o.shipmentId});
  f.act("pic-a","shipment.receive",{id:o.shipmentId,lines:[{id:o.shipmentLineId,accepted:3,reason:"Perlu diperiksa"}]},"2026-09-18");
  const complaint=f.s.complaints[0].id;
  denied(f,()=>f.act("staf","complaint.resolve",{id:complaint,outcome:"accepted",resolution:"Sesuai"}),/mendahului/);
  f.act("staf","complaint.resolve",{id:complaint,outcome:"accepted",resolution:"Sesuai"},"2026-09-20");
  denied(f,()=>f.act("staf","sale.finalize",{id:o.shipmentId},"2026-09-19"),/mendahului/);
  f.act("staf","sale.finalize",{id:o.shipmentId},"2026-09-20");
  assert.equal(f.s.shipmentLines[0].accepted,4);
});

test("v6 B02 returned stock cannot be cancelled or substituted before the return exists",()=>{
  const f=fixture(),o=ready(f);
  f.act("kurir","shipment.dispatch",{id:o.shipmentId});
  f.act("kurir","shipment.fail",{id:o.shipmentId,reason:"Gagal"},"2026-09-18");
  f.act("staf","shipment.return",{shipmentLineId:o.shipmentLineId,qty:4,good:true,reason:"Kembali lengkap"},"2026-09-20");
  denied(f,()=>f.act("pic-a","order.cancel",{id:o.id,reason:"Batal"},"2026-09-19"),/mendahului/);
  denied(f,()=>f.act("staf","substitution.propose",{orderLineId:o.lineId,productId:"q",reason:"Ganti"},"2026-09-19"),/mendahului/);
  f.act("pic-a","order.cancel",{id:o.id,reason:"Tidak diperlukan lagi"},"2026-09-20");
  assert.equal(f.s.orders[0].status,"cancelled");
});

test("v6 B02 funding and procurement dates follow every prerequisite",()=>{
  const f=fixture(),p=purchase(f,"Pasar Kering");
  denied(f,()=>f.act("kepala","funding.request",{id:p.id,amount:12000},"2026-09-16"),/mendahului/);
  f.act("kepala","funding.request",{id:p.id,amount:12000},"2026-09-20");
  denied(f,()=>f.act("pimpinan","funding.approve",{id:p.id},"2026-09-19"),/mendahului/);
  f.act("pimpinan","funding.approve",{id:p.id},"2026-09-21");
  denied(f,()=>f.act("penagihan","funding.disburse",{id:p.id},"2026-09-20"),/mendahului/);
  f.act("penagihan","funding.disburse",{id:p.id},"2026-09-22");
  denied(f,()=>f.act("kepala","purchase.confirm",{id:p.id},"2026-09-21"),/mendahului/);
  f.act("kepala","purchase.confirm",{id:p.id},"2026-09-23");
  denied(f,()=>f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:2}]},"2026-09-22"),/mendahului/);
  denied(f,()=>f.act("kepala","supplier.pay",{purchaseId:p.id,amount:12000,reference:"SPH"},"2026-09-22"),/mendahului/);
  f.act("kepala","supplier.pay",{purchaseId:p.id,amount:12000,reference:"SPH"},"2026-09-23");
  f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:2}]},"2026-09-23");
  assert.equal(f.s.purchases[0].status,"complete");
  assert.equal(f.s.supplierPayments[0].applied,12000);
});

test("v6 B02 legacy procurement uses audit transitions and supports all four normal routes",()=>{
  for(const kind of ["OMI reguler","DDO","Smart","Pasar Kering"]){
    const f=fixture(),p=purchase(f,kind,"2026-09-05");
    if(kind==="Pasar Kering"){
      f.act("kepala","funding.request",{id:p.id,amount:12000},"2026-09-06");
      delete f.s.purchases[0].fundingRequestedDate;
      denied(f,()=>f.act("pimpinan","funding.approve",{id:p.id},"2026-09-05"),/mendahului/);
      f.act("pimpinan","funding.approve",{id:p.id},"2026-09-06");
      delete f.s.purchases[0].fundingApprovedDate;
      denied(f,()=>f.act("penagihan","funding.disburse",{id:p.id},"2026-09-05"),/mendahului/);
      f.act("penagihan","funding.disburse",{id:p.id},"2026-09-06");
      delete f.s.purchases[0].fundingDisbursedDate;
      denied(f,()=>f.act("kepala","purchase.confirm",{id:p.id},"2026-09-05"),/mendahului/);
    }
    f.act(p.maker,"purchase.confirm",{id:p.id},"2026-09-06");
    delete f.s.purchases[0].confirmedDate;
    denied(f,()=>f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:2}]},"2026-09-05"),/mendahului/);
    f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:2}]},"2026-09-06");
    f.act(p.maker,"supplier.pay",{purchaseId:p.id,amount:12000,reference:"SPH"},"2026-09-06");
    assert.equal(f.s.purchases[0].status,"complete");
  }
});

test("v6 B02 later supplier payments cannot be moved before earlier installments",()=>{
  const f=fixture(),p=purchase(f);
  f.act("staf","purchase.confirm",{id:p.id});
  f.act("staf","supplier.pay",{purchaseId:p.id,amount:6000,reference:"Pertama"},"2026-09-20");
  denied(f,()=>f.act("staf","supplier.pay",{purchaseId:p.id,amount:6000,reference:"Kedua"},"2026-09-19"),/mendahului/);
  f.act("staf","supplier.pay",{purchaseId:p.id,amount:6000,reference:"Kedua"},"2026-09-20");
  f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:1}]},"2026-09-21");
  denied(f,()=>f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:1}]},"2026-09-20"),/mendahului/);
  f.act("staf","purchase.receive",{id:p.id,lines:[{purchaseLineId:p.lineId,qty:1}]},"2026-09-21");
  assert.equal(f.s.supplierPayments.reduce((sum,payment)=>sum+payment.applied,0),12000);
});

test("v6 B04 a fully cancelled order with cancelled shipment history is closed, never fulfilled",()=>{
  const f=fixture(),o=ready(f);
  f.act("staf","shipment.cancel",{id:o.shipmentId,reason:"Tidak jadi dikirim"});
  f.act("pic-a","order.cancel",{id:o.id,reason:"Kebutuhan batal"});
  const record=f.s.orders[0];
  assert.equal(record.status,"cancelled");
  assert.equal(orderFulfillmentLabel(f.s,record),"Dibatalkan");
  assert.equal(matchesOrderQueue(f.s,record,"closed"),true);
  assert.equal(matchesOrderQueue(f.s,record,"fulfilled"),false);
  assert.equal(lineProgress(f.s,f.s.orderLines[0]).reserved,0);
  assert.equal(f.s.shipments[0].status,"cancelled");
  record.status="approved"; // Legacy data is rendered correctly without a production rewrite.
  assert.equal(orderFulfillmentLabel(f.s,record),"Dibatalkan");
  assert.equal(matchesOrderQueue(f.s,record,"closed"),true);
});

test("v6 B04 cancellation retires pending substitutes with truthful history",()=>{
  const f=fixture(),o=order(f);
  f.act("kepala","order.review",{id:o.id,approve:true});
  f.act("staf","substitution.propose",{orderLineId:o.lineId,productId:"q",reason:"Usulan toko"});
  const id=f.s.substitutions[0].id;
  denied(f,()=>f.act("pic-b","order.cancel",{id:o.id,reason:"Batal"}),/divisi lain/);
  f.act("pic-a","order.cancel",{id:o.id,reason:"Rapat dibatalkan"});
  assert.equal(f.s.substitutions[0].status,"cancelled");
  assert.match(f.s.substitutions[0].cancelledReason!,/Rapat dibatalkan/);
  assert.equal(substitutionNeedsDecision(f.s,f.s.substitutions[0]),false);
  assert.equal(matchesOrderQueue(f.s,f.s.orders[0],"needs-pic"),false);
  denied(f,()=>f.act("pic-a","substitution.decide",{id,approve:true}),/berubah|diputuskan/);
  f.s.substitutions[0].status="pending"; // Legacy orphaned substitute must not request a decision.
  assert.equal(substitutionNeedsDecision(f.s,f.s.substitutions[0]),false);
});

test("v6 B04 cancelling the remainder preserves partial reception, finalization and invoice",()=>{
  const f=fixture(),o=order(f,6);
  f.act("kepala","order.review",{id:o.id,approve:true});
  f.act("staf","stock.reserve",{orderLineId:o.lineId,qty:6});
  const sh=f.act("staf","shipment.create",{orderId:o.id,courierId:"kurir",lines:[{orderLineId:o.lineId,qty:4}]});
  f.act("kurir","shipment.dispatch",{id:sh});
  const sl=f.s.shipmentLines[0].id;
  f.act("pic-a","shipment.receive",{id:sh,lines:[{id:sl,accepted:4}]});
  f.act("pic-a","order.cancel",{id:o.id,reason:"Sisa tidak diperlukan"});
  assert.equal(f.s.orders[0].status,"approved");
  assert.equal(f.s.orderLines[0].cancelled,2);
  assert.equal(lineProgress(f.s,f.s.orderLines[0]).accepted,4);
  f.act("staf","sale.finalize",{id:sh});
  const invoice=f.act("penagihan","invoice.issue",{divisionId:"div-ops",dueDate:"2026-10-17",lines:[{shipmentLineId:sl}]});
  assert.equal(invoiceTotal(f.s,invoice),40000);
  denied(f,()=>f.act("pic-a","order.cancel",{id:o.id,reason:"Ulang"}),/Tidak ada sisa/);
});

test("v6 B07 stale stocktakes cannot revive when quantity returns to the old number",()=>{
  const f=fixture(),old=f.act("staf","stocktake.create",{batchId:"b",counted:18,reason:"Hitung pertama"});
  const changed=f.act("staf","stocktake.create",{batchId:"b",counted:19,reason:"Hitung lain"});
  f.act("kepala","stocktake.approve",{id:changed});
  assert.equal(f.s.stocktakes.find(st=>st.id===old)!.status,"stale");
  const restored=f.act("staf","stocktake.create",{batchId:"b",counted:20,reason:"Barang ditemukan"});
  f.act("kepala","stocktake.approve",{id:restored});
  assert.equal(f.s.batches[0].qty,20);
  denied(f,()=>f.act("kepala","stocktake.approve",{id:old}),/berubah/);
  const replacement=f.act("staf","stocktake.create",{batchId:"b",counted:20,reason:"Hitung ulang terbaru",replacesId:old});
  assert.equal(f.s.stocktakes.find(st=>st.id===old)!.status,"superseded");
  assert.equal(f.s.stocktakes.find(st=>st.id===old)!.supersededBy,replacement);
  assert.equal(f.s.stocktakes.find(st=>st.id===replacement)!.replacesId,old);
  const beforeJournals=f.s.journals.length;
  f.act("kepala","stocktake.approve",{id:replacement});
  assert.equal(f.s.journals.length,beforeJournals,"zero adjustment does not book another journal");
  denied(f,()=>f.act("kepala","stocktake.approve",{id:replacement}),/diproses/);
});

test("v6 B07 legacy stale documents can be cancelled or replaced without adjustments",()=>{
  const f=fixture(),old=f.act("staf","stocktake.create",{batchId:"b",counted:18,reason:"Hitung"});
  const record=f.s.stocktakes[0];delete record.movementCount;record.expected=21;
  assert.equal(stocktakeStatus(f.s,record),"stale");
  denied(f,()=>f.act("staf","stocktake.create",{batchId:"bq",counted:20,reason:"Salah batch",replacesId:old}),/batch yang sama/);
  f.s.users.push({...f.s.users.find(u=>u.id==="staf")!,id:"staf-lain"});
  denied(f,()=>f.act("staf-lain","stocktake.cancel",{id:old,reason:"Batal"}),/pemilik/);
  const beforeJournals=f.s.journals.length;
  f.act("kepala","stocktake.cancel",{id:old,reason:"Gunakan hitung ulang"});
  assert.equal(f.s.stocktakes[0].status,"cancelled");
  assert.equal(f.s.stocktakes[0].resolvedBy,"kepala");
  assert.equal(f.s.journals.length,beforeJournals);
  assert.equal(f.s.batches[0].qty,20);
});

test("v6 B10 resubmitted periods clear current approval but preserve its audit",()=>{
  const f=fixture();
  f.act("laporan","period.submit",{month:"2026-09"});
  f.act("pimpinan","period.approve",{id:"2026-09"});
  const approvedAudit=f.s.audits.find(a=>a.action==="period.approve")!.id;
  f.act("laporan","period.submit",{month:"2026-09"});
  assert.equal(f.s.periods[0].status,"review");
  assert.equal(f.s.periods[0].approvedBy,null);
  assert.equal(f.s.periods[0].approvedRevision,null);
  assert.ok(f.s.audits.some(a=>a.id===approvedAudit));
  denied(f,()=>f.act("akuntansi","period.close",{id:"2026-09"}),/belum disetujui/);
  f.act("pimpinan","period.approve",{id:"2026-09"});
  f.act("akuntansi","period.close",{id:"2026-09"});
  const snapshot=structuredClone(f.s.periods[0].snapshot);
  denied(f,()=>f.act("pic-a","order.create",{neededAt:"2026-09-17",lines:[{productId:"p",qty:1}]}),/Periode sudah ditutup/);
  f.act("pic-a","order.create",{neededAt:"2026-10-01",lines:[{productId:"p",qty:1}]},"2026-10-01");
  assert.deepEqual(f.s.periods[0].snapshot,snapshot);
});
