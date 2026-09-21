import {test} from "node:test";
import assert from "node:assert/strict";
import {seedState} from "../lib/domain/seed";
import {runCommand} from "../lib/domain/engine";
import {billableRows,invoiceSelection,suggestedInvoiceDates,earliestOpenInvoiceDate} from "../lib/domain/invoice-selection";

function fixture(){
  const state=seedState();
  const line=state.shipmentLines.find(row=>row.finalized)!;
  const ids=Array.from({length:101},(_,i)=>`bulk-line-${i}`);
  // A bounded synthetic workload exercises the invoice cap independently of creating 101 deliveries.
  for(const id of ids)state.shipmentLines.push({...line,id,qty:1,accepted:1,returned:0,returnGood:0});
  const divisionId=state.orders.find(order=>order.id===state.shipments.find(sh=>sh.id===line.shipmentId)!.orderId)!.divisionId;
  return {state,ids,divisionId};
}
test("101 billable rows can be intentionally split into 100 and 1 without duplicate billing",()=>{
  const {state,ids,divisionId}=fixture();
  assert.match(invoiceSelection(state,divisionId,ids).error,/maksimal 100/);
  const chosen=invoiceSelection(state,divisionId,ids.slice(0,100));
  assert.equal(chosen.error,"");assert.equal(chosen.selected.length,100);
  const actor=state.users.find(user=>user.role==="penagihan")!;
  const date=chosen.minimumDate;
  const first=runCommand(state,actor,{id:crypto.randomUUID(),type:"invoice.issue",date,data:{divisionId,dueDate:date,lines:chosen.selected.map(row=>({shipmentLineId:row.id}))}});
  assert.equal(first.state.invoiceLines.filter(row=>row.invoiceId===first.result.id).length,100);
  assert.equal(billableRows(first.state).filter(row=>ids.includes(row.id)).length,1);
  assert.match(invoiceSelection(first.state,divisionId,[ids[0]]).error,/sudah ditagih/);
  const last=invoiceSelection(first.state,divisionId,[ids[100]]);
  assert.equal(last.error,"");
  const second=runCommand(first.state,actor,{id:crypto.randomUUID(),type:"invoice.issue",date,data:{divisionId,dueDate:date,lines:last.selected.map(row=>({shipmentLineId:row.id}))}});
  assert.equal(billableRows(second.state).filter(row=>ids.includes(row.id)).length,0);
  assert.equal(second.state.invoiceLines.filter(row=>ids.includes(row.shipmentLineId)).length,101);
});
test("invoice selection rejects mixed divisions, duplicate IDs and preserves stored prices",()=>{
  const {state,ids,divisionId}=fixture();
  const row=billableRows(state).find(item=>item.id===ids[0])!;
  assert.match(invoiceSelection(state,"div-ti",[row.id]).error,/tidak tersedia/);
  assert.match(invoiceSelection(state,divisionId,[row.id,row.id]).error,/tidak tersedia/);
  const selected=invoiceSelection(state,divisionId,[row.id]);
  assert.equal(selected.total,row.qty*row.price);
  const product=state.products.find(item=>item.sku===row.sku)!;
  product.price+=100000;
  assert.equal(invoiceSelection(state,divisionId,[row.id]).total,selected.total);
});

test("invoice from a closed month starts in the next open month and can be issued there",()=>{
  const {state,ids,divisionId}=fixture();
  state.periods=[{...state.periods[0],id:"2026-09",status:"closed"}];
  state.shipmentLines.find(row=>row.id===ids[0])!.finalizedDate="2026-09-17";
  const selected=invoiceSelection(state,divisionId,[ids[0]]);
  const defaults=suggestedInvoiceDates(state,"2026-09-17");
  assert.equal(selected.minimumDate,"2026-10-01");
  assert.deepEqual(defaults,{date:"2026-10-01",dueDate:"2026-10-31"});
  const actor=state.users.find(user=>user.role==="penagihan")!;
  const before=structuredClone(state);
  const data={divisionId,dueDate:defaults.dueDate,lines:[{shipmentLineId:ids[0]}]};
  assert.throws(()=>runCommand(state,actor,{id:crypto.randomUUID(),type:"invoice.issue",date:"2026-09-17",data}),/Periode sudah ditutup/);
  assert.deepEqual(state,before);
  const issued=runCommand(state,actor,{id:crypto.randomUUID(),type:"invoice.issue",date:defaults.date,data});
  assert.equal(issued.state.invoices.find(invoice=>invoice.id===issued.result.id)!.date,"2026-10-01");
});

test("invoice date boundaries allow valid backdates and respect finalization and year rollover",()=>{
  const {state,ids,divisionId}=fixture();
  const line=state.shipmentLines.find(row=>row.id===ids[0])!;
  state.periods=[];line.finalizedDate="2026-09-05";
  assert.equal(earliestOpenInvoiceDate(state),"");
  assert.equal(invoiceSelection(state,divisionId,[line.id]).minimumDate,"2026-09-05");
  assert.equal(suggestedInvoiceDates(state,"2026-09-17").date,"2026-09-17");
  // The minimum is not today, so a legitimate historical invoice remains possible.
  const actor=state.users.find(user=>user.role==="penagihan")!;
  const historical=runCommand(state,actor,{id:crypto.randomUUID(),type:"invoice.issue",date:"2026-09-06",data:{divisionId,dueDate:"2026-10-06",lines:[{shipmentLineId:line.id}]}});
  assert.equal(historical.state.invoices.at(-1)!.date,"2026-09-06");
  state.periods=[{id:"2026-12",revision:0,status:"closed",approvedRevision:null,approvedBy:null,submittedBy:null,closedBy:null,closedAt:"",snapshot:null}];
  assert.deepEqual(suggestedInvoiceDates(state,"2026-12-17"),{date:"2027-01-01",dueDate:"2027-01-31"});
  line.finalizedDate="2027-02-03";
  assert.equal(invoiceSelection(state,divisionId,[line.id]).minimumDate,"2027-02-03");
});
