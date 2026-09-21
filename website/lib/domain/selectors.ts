import { ACCOUNTS, DomainError, type State, type OrderLine, type Invoice, type Batch, type Stocktake } from "./model";
import type { Actor, Role } from "./accounts";
export const sum=(values:number[])=>values.reduce((a,b)=>a+b,0);
export const today=()=>new Date().toLocaleDateString("en-CA",{timeZone:"Asia/Jakarta"});
export const money=(v:number)=>new Intl.NumberFormat("id-ID",{style:"currency",currency:"IDR",maximumFractionDigits:0}).format(v);
export const get=<T extends {id:string}>(items:T[],id:unknown):T=>{const item=items.find(x=>x.id===id);if(!item)throw new DomainError("Data tidak ditemukan.",404);return item;};
export function allowed(actor:Actor,roles:Role[]){if(!actor.active||!roles.includes(actor.role))throw new DomainError("Peran Anda tidak memiliki akses untuk tindakan ini.",403);}
export function ownDivision(actor:Actor,divisionId:string){if(actor.role==="pic"&&actor.divisionId!==divisionId)throw new DomainError("Data divisi lain tidak dapat diakses.",403);}
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
export function paymentAvailable(s:State,id:string){const p=get(s.payments,id);return p.status!=="verified"?0:p.amount-sum(s.allocations.filter(a=>a.paymentId===id).map(a=>a.amount))-sum(s.refunds.filter(a=>a.paymentId===id&&a.status!=="requested").map(a=>a.amount));}
export function invoiceStatus(s:State,i:Invoice){const balance=invoiceBalance(s,i.id);return balance===0?"Lunas":balance<invoiceTotal(s,i.id)?"Dibayar sebagian":i.dueDate<today()?"Lewat jatuh tempo":"Belum dibayar";}
export function financialReport(s:State,endDate:string){const journals=s.journals.filter(j=>j.date<=endDate);const ids=new Set(journals.map(j=>j.id));const lines=s.journalLines.filter(l=>ids.has(l.journalId));const accounts=Object.entries(ACCOUNTS).map(([code,name])=>({code,name,debit:sum(lines.filter(l=>l.account===code).map(l=>l.debit)),credit:sum(lines.filter(l=>l.account===code).map(l=>l.credit))}));return {endDate,accounts,debit:sum(accounts.map(a=>a.debit)),credit:sum(accounts.map(a=>a.credit)),journals};}
export function scopeState(s:State,actor:Actor):State {
  allowed(actor,["pic","kepala","staf","kurir","laporan","penagihan","pimpinan","akuntansi","admin"]);
  const copy=Object.fromEntries(Object.keys(s).map(k=>[k,k==="revision"?s.revision:[]])) as unknown as State;
  const assign=(keys:(keyof State)[])=>{for(const key of keys)if(key!=="revision")(copy[key] as unknown[])=structuredClone(s[key]);};
  if(actor.role==="admin"){
    assign(["users","divisions","suppliers"]);copy.audits=s.audits.filter(a=>a.action.startsWith("admin.")||a.action.startsWith("profile."));return copy;
  }
  if(actor.role!=="pic"&&actor.role!=="kurir"){
    assign(["divisions","products","suppliers","batches","orders","orderLines","reservations","substitutions","shipments","shipmentLines","complaints","purchases","purchaseLines","supplierPayments","stockReturns","stocktakes","movements"]);
    copy.users=s.users.filter(u=>u.id===actor.id||u.role==="kurir").map(u=>u.id===actor.id?u:({id:u.id,name:u.name,role:u.role,divisionId:u.divisionId,active:u.active,avatar:u.avatar?.startsWith("/images/")?u.avatar:undefined}));
    copy.expenses=s.expenses.filter(e=>actor.role!=="staf"||e.createdBy===actor.id);
    if(actor.role!=="staf"){
      assign(["invoices","invoiceLines","payments","allocations","credits","refunds","journals","journalLines","periods"]);
      if(actor.role==="kepala"||actor.role==="laporan")copy.payments=copy.payments.map(p=>({...p,reference:"",payer:p.divisionId?"Pembayaran divisi":"Dana belum diidentifikasi",note:"",evidence:"",createdBy:"",verifiedBy:null}));
    }
    copy.audits=s.audits.filter(a=>!a.action.startsWith("admin.")&&!a.action.startsWith("profile.")&&(actor.role!=="staf"||/^(order|stock|shipment|substitution|complaint|sale|purchase|supplier)\./.test(a.action)));
    copy.attachments=s.attachments.filter(a=>canReadAttachment(s,actor,a.id));return copy;
  }
  copy.products=structuredClone(s.products);
  const orderIds=new Set(actor.role==="pic"?s.orders.filter(o=>o.divisionId===actor.divisionId).map(o=>o.id):s.shipments.filter(x=>x.courierId===actor.id).map(x=>x.orderId));
  copy.orders=s.orders.filter(o=>orderIds.has(o.id));copy.orderLines=s.orderLines.filter(l=>orderIds.has(l.orderId));const lineIds=new Set(copy.orderLines.map(l=>l.id));
  copy.shipments=s.shipments.filter(x=>orderIds.has(x.orderId)&&(actor.role!=="kurir"||x.courierId===actor.id));const shipIds=new Set(copy.shipments.map(x=>x.id));
  copy.shipmentLines=s.shipmentLines.filter(x=>shipIds.has(x.shipmentId));copy.complaints=s.complaints.filter(x=>shipIds.has(x.shipmentId));copy.substitutions=s.substitutions.filter(x=>lineIds.has(x.orderLineId));
  copy.invoices=actor.role==="pic"?s.invoices.filter(x=>x.divisionId===actor.divisionId):[];const invoiceIds=new Set(copy.invoices.map(x=>x.id));copy.invoiceLines=s.invoiceLines.filter(x=>invoiceIds.has(x.invoiceId));copy.credits=s.credits.filter(x=>invoiceIds.has(x.invoiceId));
  copy.payments=actor.role==="pic"?s.payments.filter(x=>x.divisionId===actor.divisionId):[];const paymentIds=new Set(copy.payments.map(x=>x.id));copy.allocations=s.allocations.filter(x=>invoiceIds.has(x.invoiceId)&&paymentIds.has(x.paymentId));copy.refunds=s.refunds.filter(x=>paymentIds.has(x.paymentId));
  copy.divisions=s.divisions.filter(d=>actor.role==="pic"?d.id===actor.divisionId:copy.orders.some(o=>o.divisionId===d.id));copy.users=s.users.filter(u=>u.id===actor.id);
  copy.attachments=s.attachments.filter(a=>canReadAttachment(s,actor,a.id));
  copy.expenses=s.expenses.filter(x=>x.createdBy===actor.id);copy.audits=[];copy.suppliers=[];copy.purchases=[];copy.purchaseLines=[];copy.supplierPayments=[];copy.stockReturns=[];copy.stocktakes=[];copy.movements=[];copy.journals=[];copy.journalLines=[];copy.periods=[];
  // Expose only aggregate available stock. Supplier costs and other divisions' reservations remain private.
  copy.batches=s.products.map(p=>({id:`availability-${p.id}`,productId:p.id,code:"Tersedia",qty:productAvailable(s,p.id),held:0,cost:0,expiry:"",location:"Toko"}));
  const usedBatchIds=new Set(copy.shipmentLines.map(l=>l.batchId));copy.batches.push(...s.batches.filter(b=>usedBatchIds.has(b.id)).map(b=>({...b,qty:0,held:0,cost:0})));
  copy.orderLines=copy.orderLines.map(l=>({...l,reservedQty:sum(s.reservations.filter(r=>r.orderLineId===l.id).map(r=>r.qty))}));copy.reservations=[];copy.shipmentLines=copy.shipmentLines.map(l=>({...l,cost:0}));
  if(actor.role==="kurir"){
    const assignedLines=new Set(copy.shipmentLines.map(l=>l.orderLineId));
    copy.orderLines=copy.orderLines.filter(l=>assignedLines.has(l.id)).map(l=>({...l,price:0,reservedQty:0,qty:sum(copy.shipmentLines.filter(x=>x.orderLineId===l.id).map(x=>x.qty)),cancelled:0}));
    const products=new Set(copy.orderLines.map(l=>l.productId));copy.products=copy.products.filter(p=>products.has(p.id)).map(p=>({...p,price:0,minimum:0}));
    copy.shipmentLines=copy.shipmentLines.map(l=>({...l,price:0,cost:0}));copy.batches=[];copy.substitutions=[];
  }
  return copy;
}
export function canReadAttachment(s:State,actor:Actor,id:string){
 const a=s.attachments.find(x=>x.id===id);if(!a||!actor.active)return false;
 if(actor.role==="admin")return false;
 if(actor.role==="pic")return a.divisionId===actor.divisionId&&["payment","receipt","complaint","order"].includes(a.scope);
 if(actor.role==="kurir")return (a.scope==="expense"&&a.ownerId===actor.id)||(a.scope==="receipt"&&!!a.shipmentId&&s.shipments.some(sh=>sh.id===a.shipmentId&&sh.courierId===actor.id));
 if(a.scope==="payment")return ["penagihan","pimpinan","akuntansi"].includes(actor.role);
 if(a.scope==="expense")return a.ownerId===actor.id||["kepala","penagihan","pimpinan","akuntansi"].includes(actor.role);
 return ["receipt","complaint","order"].includes(a.scope)&&["staf","kepala","laporan","penagihan","pimpinan","akuntansi"].includes(actor.role);
}
export function attachmentContext(s:State,actor:Actor,scope:string,targetId:string){
 if(scope==="payment"){allowed(actor,["pic","penagihan"]);const p=get(s.payments,targetId);if(actor.role==="pic"&&p.divisionId!==actor.divisionId)throw new DomainError("Bukti divisi lain tidak dapat diakses.",403);return {divisionId:p.divisionId,shipmentId:null};}
 if(scope==="receipt"){allowed(actor,["pic","staf","kurir"]);const sh=get(s.shipments,targetId),o=get(s.orders,sh.orderId);ownDivision(actor,o.divisionId);if(actor.role==="kurir"&&sh.courierId!==actor.id)throw new DomainError("Bukan tugas pengiriman Anda.",403);if(!["dispatched","received"].includes(sh.status))throw new DomainError("Bukti diunggah setelah pengiriman berangkat.");return {divisionId:o.divisionId,shipmentId:sh.id};}
 if(scope==="expense"){allowed(actor,["staf","kurir","kepala"]);const e=get(s.expenses,targetId);if(["kurir","staf"].includes(actor.role)&&e.createdBy!==actor.id)throw new DomainError("Bukan pengajuan biaya Anda.",403);return {divisionId:null,shipmentId:e.shipmentId};}
 throw new DomainError("Jenis lampiran tidak didukung.");
}
