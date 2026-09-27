import { ACCOUNTS, DomainError, type State, type OrderLine, type Invoice, type Batch, type Stocktake } from "./model";
import { now } from "./clock";
export const sum=(values:number[])=>values.reduce((a,b)=>a+b,0);
export const today=()=>now().toLocaleDateString("en-CA",{timeZone:"Asia/Jakarta"});
export const money=(v:number)=>new Intl.NumberFormat("id-ID",{style:"currency",currency:"IDR",maximumFractionDigits:0}).format(v);
export const get=<T extends {id:string}>(items:T[],id:unknown):T=>{const item=items.find(x=>x.id===id);if(!item)throw new DomainError("Data tidak ditemukan.",404);return item;};
export function available(state:State,batch:Batch,date=today()){return batch.expiry&&batch.expiry<=date?0:batch.qty-batch.held-sum(state.reservations.filter(r=>r.batchId===batch.id).map(r=>r.qty));}
export function productAvailable(s:State,id:string,date=today()){return sum(s.batches.filter(b=>b.productId===id).map(b=>available(s,b,date)));}
export function stocktakeStatus(s:State,stocktake:Stocktake):Stocktake["status"] {
  if(stocktake.status!=="requested")return stocktake.status;
  const batch=s.batches.find(b=>b.id===stocktake.batchId);
  const movementCount=s.movements.filter(m=>m.batchId===stocktake.batchId).length;
  return !batch||batch.qty!==stocktake.expected||(stocktake.movementCount!==undefined&&stocktake.movementCount!==movementCount)?"stale":"requested";
}
export function lineProgress(s:State,l:OrderLine){const lines=s.shipmentLines.filter(x=>x.orderLineId===l.id&&get(s.shipments,x.shipmentId).status!=="cancelled"); const sent=lines.filter(x=>get(s.shipments,x.shipmentId).status!=="ready");const staged=sum(lines.filter(x=>get(s.shipments,x.shipmentId).status==="ready").map(x=>x.qty));const shipped=sum(sent.map(x=>x.qty-x.returned)); const accepted=sum(lines.map(x=>x.accepted));return {reserved:l.reservedQty??sum(s.reservations.filter(r=>r.orderLineId===l.id).map(r=>r.qty)),staged,shipped,accepted,remaining:l.qty-l.cancelled-shipped,finalized:sum(lines.filter(x=>x.finalized).map(x=>x.accepted))};}
export function invoiceTotal(s:State,id:string){return sum(s.invoiceLines.filter(x=>x.invoiceId===id).map(x=>x.subtotal+x.tax));}
export function invoiceBalance(s:State,id:string){return Math.max(0,invoiceTotal(s,id)-sum(s.allocations.filter(a=>a.invoiceId===id).map(a=>a.amount))-sum(s.credits.filter(c=>c.invoiceId===id&&c.status==="approved").map(c=>c.amount)));}
export function paymentAvailable(s:State,id:string){const p=get(s.payments,id);return p.status!=="verified"?0:p.amount-sum(s.allocations.filter(a=>a.paymentId===id).map(a=>a.amount))-sum(s.refunds.filter(a=>a.paymentId===id&&["approved","paid"].includes(a.status)).map(a=>a.amount));}
export function invoiceStatus(s:State,i:Invoice){const balance=invoiceBalance(s,i.id);return balance===0?"Lunas":balance<invoiceTotal(s,i.id)?"Dibayar sebagian":i.dueDate<today()?"Lewat jatuh tempo":"Belum dibayar";}
export function financialReport(s:State,endDate:string){const journals=s.journals.filter(j=>j.date<=endDate);const ids=new Set(journals.map(j=>j.id));const lines=s.journalLines.filter(l=>ids.has(l.journalId));const accounts=Object.entries(ACCOUNTS).map(([code,name])=>({code,name,debit:sum(lines.filter(l=>l.account===code).map(l=>l.debit)),credit:sum(lines.filter(l=>l.account===code).map(l=>l.credit))}));return {endDate,accounts,debit:sum(accounts.map(a=>a.debit)),credit:sum(accounts.map(a=>a.credit)),journals};}
