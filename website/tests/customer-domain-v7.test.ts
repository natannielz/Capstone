import {test} from "node:test";
import assert from "node:assert/strict";
import {DEMO_ACCOUNTS, type Actor} from "../lib/domain/accounts";
import {assertBuyerAccess, buyerKey, buyerLabel, buyerRefForActor, sameBuyer} from "../lib/domain/buyers";
import {runCommand, journal, validateState} from "../lib/domain/engine";
import {DomainError, type Command} from "../lib/domain/model";
import {emptyState, seedState} from "../lib/domain/seed";
import {attachmentContext, canReadAttachment, financialReport, invoiceBalance, invoiceTotal, lineProgress, paymentAvailable, productAvailable, scopeState, today} from "../lib/domain/selectors";

function fixture(qty=10) {
  let state=emptyState();
  const date=today();
  state.users=structuredClone(DEMO_ACCOUNTS);
  state.users.push({id:"customer-b",name:"Pelanggan B",role:"customer",divisionId:null,active:true,email:"private-b@example.invalid",phone:"PRIVATE-CONTACT-B",address:"PRIVATE-ADDRESS-B"});
  state.divisions=[{id:"div-ops",name:"Operasional",code:"OPS",address:"Alamat divisi"},{id:"div-ti",name:"Teknologi",code:"TI",address:"Alamat TI"}];
  state.products=[{id:"p",sku:"P",name:"Produk uji",category:"OMI",unit:"pcs",price:10000,minimum:2,returnMonths:1,active:true},{id:"q",sku:"Q",name:"Pengganti",category:"OMI",unit:"pcs",price:12000,minimum:2,returnMonths:1,active:true}];
  state.batches=[{id:"b",productId:"p",supplierId:"supplier-private",code:"PRIVATE-BATCH",qty,held:0,cost:6000,expiry:"2099-01-01",location:"PRIVATE-WAREHOUSE"},{id:"bq",productId:"q",qty:20,held:0,cost:7000,expiry:"",location:"Toko",code:"Q"}];
  state.suppliers=[{id:"supplier-private",name:"PRIVATE-SUPPLIER",category:"OMI"}];
  journal(state,date,"opening","Saldo awal",[["inventory",qty*6000+140000,0],["capital",0,qty*6000+140000]]);
  const actor=(id:string)=>state.users.find(user=>user.id===id)!;
  return {date,actor,get s(){return state;},act(who:string,type:string,data:Record<string,unknown>,effective=date){const result=runCommand(state,actor(who),{id:crypto.randomUUID(),type,date:effective,data});state=result.state;return result.result.id;}};
}
type Fixture=ReturnType<typeof fixture>;
function denied(f:Fixture,action:()=>unknown,pattern:RegExp){const before=structuredClone(f.s);assert.throws(action,pattern);assert.deepEqual(f.s,before,"A denied action must leave every collection, journal and audit unchanged");}
function create(f:Fixture,who="customer-demo",qty=3){return f.act(who,"order.create",{neededAt:f.date,recipientName:"Penerima awal",recipientPhone:"0800000000",address:"Alamat awal",lines:[{productId:"p",qty,unitPrice:10000}]});}
function dispatch(f:Fixture,who="customer-demo",qty=3){const orderId=create(f,who,qty),line=f.s.orderLines.find(line=>line.orderId===orderId)!;f.act("kepala","order.review",{id:orderId,approve:true});f.act("staf","stock.reserve",{orderLineId:line.id,qty});const shipmentId=f.act("staf","shipment.create",{orderId,courierId:"kurir",lines:[{orderLineId:line.id,qty}]});f.act("kurir","shipment.dispatch",{id:shipmentId});return {orderId,lineId:line.id,shipmentId,shipmentLineId:f.s.shipmentLines.find(line=>line.shipmentId===shipmentId)!.id};}
function final(f:Fixture,who="customer-demo",qty=3){const ids=dispatch(f,who,qty);f.act(who,"shipment.receive",{id:ids.shipmentId,lines:[{id:ids.shipmentLineId,accepted:qty}]});f.act("staf","sale.finalize",{id:ids.shipmentId});return ids;}
function issue(f:Fixture,shipmentLineId:string){const line=f.s.shipmentLines.find(line=>line.id===shipmentLineId)!,sh=f.s.shipments.find(sh=>sh.id===line.shipmentId)!,order=f.s.orders.find(order=>order.id===sh.orderId)!;return f.act("penagihan","invoice.issue",{divisionId:order.divisionId,customerId:order.customerId,dueDate:f.date,lines:[{shipmentLineId}]});}
function file(scope:string,targetId:string){return {scope,targetId,attachmentId:crypto.randomUUID(),name:"Bukti simulasi.pdf",mime:"application/pdf",size:100};}

test("v7 buyer identity distinguishes two customers, divisions and unidentified funds",()=>{
  const f=fixture(),a={divisionId:null,customerId:"customer-demo"},b={divisionId:null,customerId:"customer-b"};
  assert.equal(buyerKey(a),"customer:customer-demo");assert.equal(buyerKey({divisionId:"div-ops"}),"division:div-ops");
  assert.equal(sameBuyer(a,b),false);assert.equal(sameBuyer({divisionId:null},{divisionId:null}),false);
  assert.equal(buyerKey({divisionId:"div-ops",customerId:"customer-demo"}),null);
  assert.deepEqual(buyerRefForActor(f.actor("customer-demo"),{divisionId:"div-ops",customerId:"customer-b"}),a);
  assert.equal(buyerLabel(f.s,a),"Pelanggan Demo");
  assert.throws(()=>assertBuyerAccess(f.actor("customer-demo"),b),/pembeli lain/);
  assert.throws(()=>assertBuyerAccess(f.actor("pic-a"),a),/pembeli lain/);
  const unknown={...f.actor("customer-demo"),role:"unknown"} as unknown as Actor;
  assert.throws(()=>scopeState(f.s,unknown),DomainError);
  denied(f,()=>runCommand(f.s,unknown,{id:crypto.randomUUID(),type:"profile.update",data:{name:"Escalation"}}),/peran/);
});

test("v7 fresh seed has exactly one additional customer with the explicit demo email",()=>{
  const customers=seedState().users.filter(user=>user.role==="customer");
  assert.equal(customers.length,1);assert.equal(customers[0].id,"customer-demo");assert.equal(customers[0].email,"customer@unit-toko.demo");assert.equal(customers[0].divisionId,null);
});

test("v7 customer checkout derives buyer/date and preserves recipient snapshots across profile edits",()=>{
  const f=fixture(),month=f.date.slice(0,7),[year,index]=month.split("-").map(Number),openDate=new Date(Date.UTC(year,index,1)).toISOString().slice(0,10);
  const closed={id:month,revision:5,status:"closed" as const,approvedRevision:5,approvedBy:"pimpinan",submittedBy:"laporan",closedBy:"akuntansi",closedAt:new Date().toISOString(),snapshot:{marker:"immutable"}};
  f.s.periods=f.s.periods.filter(p=>p.id!==month);f.s.periods.push(closed);
  const before=structuredClone(closed);
  const id=f.act("customer-demo","order.create",{divisionId:"div-ops",customerId:"customer-b",recipientName:"Penerima awal",recipientPhone:"0800000000",address:"Alamat awal",lines:[{productId:"p",qty:2,unitPrice:10000}]},"1990-01-01");
  const order=f.s.orders.find(order=>order.id===id)!;
  assert.equal(order.customerId,"customer-demo");assert.equal(order.divisionId,null);assert.equal(order.createdDate,openDate);assert.equal(order.neededAt,openDate);assert.equal(order.channel,"customer");assert.ok(Date.parse(order.createdAt)>Date.parse("2026-01-01"));
  f.act("customer-demo","profile.update",{name:"Nama baru",phone:"0811111111",position:"",address:"  Alamat bawaan baru  "});
  assert.equal(f.actor("customer-demo").address,"Alamat bawaan baru");assert.equal(f.s.orders.find(order=>order.id===id)!.address,"Alamat awal");assert.equal(order.recipientName,"Penerima awal");assert.equal(order.recipientPhone,"0800000000");
  assert.deepEqual(f.s.periods.find(p=>p.id===month),before);
  denied(f,()=>f.act("customer-demo","profile.update",{name:"Nama",customerId:"customer-b"}),/tidak diizinkan/);
  denied(f,()=>f.act("customer-demo","profile.update",{name:"Nama",address:"x".repeat(501)}),/panjang/);
});

test("v7 customer checkout rejects changed prices, unavailable stock and inactive SKUs atomically",()=>{
  const f=fixture();
  const input={recipientName:"Penerima",recipientPhone:"0800000000",address:"Alamat",lines:[{productId:"p",qty:2,unitPrice:9999}]};
  denied(f,()=>f.act("customer-demo","order.create",input),/Harga barang/);
  denied(f,()=>f.act("customer-demo","order.create",{...input,lines:[{productId:"p",qty:11,unitPrice:10000}]}),/Stok barang/);
  f.s.products[0].active=false;
  denied(f,()=>f.act("customer-demo","order.create",{...input,lines:[{productId:"p",qty:2,unitPrice:10000}]}),/tidak aktif/);
  f.s.products[0].active=true;
  const before=f.s.batches[0].qty;create(f);assert.equal(f.s.batches[0].qty,before,"Submission itself does not dispatch stock");
});

test("v7 full customer sale reconciles reserve, dispatch, invoice and payment at frozen order price",()=>{
  const f=fixture(),orderId=create(f),line=f.s.orderLines.find(line=>line.orderId===orderId)!;
  f.act("kepala","order.review",{id:orderId,approve:true});f.act("staf","stock.reserve",{orderLineId:line.id,qty:3});assert.equal(productAvailable(f.s,"p"),7);assert.equal(f.s.batches[0].qty,10);
  f.act("kepala","product.update",{id:"p",price:15000,minimum:2,returnMonths:1});
  const shipmentId=f.act("staf","shipment.create",{orderId,courierId:"kurir",lines:[{orderLineId:line.id,qty:3}]});f.act("kurir","shipment.dispatch",{id:shipmentId});assert.equal(f.s.batches[0].qty,7);
  const sl=f.s.shipmentLines.find(line=>line.shipmentId===shipmentId)!;f.act("customer-demo","shipment.receive",{id:shipmentId,lines:[{id:sl.id,accepted:3}]});f.act("staf","sale.finalize",{id:shipmentId});const invoiceId=issue(f,sl.id);
  assert.equal(invoiceTotal(f.s,invoiceId),30000);assert.equal(f.s.invoices[0].customerId,"customer-demo");
  const payment=f.act("customer-demo","payment.record",{invoiceId,divisionId:"div-ops",customerId:"customer-b",amount:30000});assert.equal(f.s.payments[0].customerId,"customer-demo");assert.equal(invoiceBalance(f.s,invoiceId),30000);
  denied(f,()=>f.act("customer-demo","payment.verify",{id:payment}),/akses/);
  f.act("penagihan","payment.verify",{id:payment});assert.equal(invoiceBalance(f.s,invoiceId),30000);
  f.act("penagihan","payment.allocate",{paymentId:payment,lines:[{invoiceId,amount:30000}]});assert.equal(invoiceBalance(f.s,invoiceId),0);assert.equal(paymentAvailable(f.s,payment),0);
  const report=financialReport(f.s,f.date);assert.equal(report.debit,report.credit);
  denied(f,()=>f.act("customer-demo","payment.record",{invoiceId,amount:1}),/lunas/);
});

test("v7 partial customer delivery returns one unit and preserves buyer on excess funds, credit and refund",()=>{
  const f=fixture(),ids=dispatch(f);
  f.act("customer-demo","shipment.receive",{id:ids.shipmentId,lines:[{id:ids.shipmentLineId,accepted:2,reason:"Satu barang ditolak"}]});
  assert.equal(f.s.complaints[0].customerId,"customer-demo");
  f.act("staf","shipment.return",{shipmentLineId:ids.shipmentLineId,qty:1,good:true,reason:"Barang kembali layak"});assert.equal(f.s.batches[0].qty,8);
  denied(f,()=>f.act("staf","shipment.return",{shipmentLineId:ids.shipmentLineId,qty:1,good:true,reason:"Ulang"}),/melebihi/);
  f.act("staf","complaint.resolve",{id:f.s.complaints[0].id,outcome:"returned",resolution:"Barang sudah kembali"});f.act("customer-demo","order.cancel",{id:ids.orderId,reason:"Tidak perlu pengganti"});assert.equal(lineProgress(f.s,f.s.orderLines[0]).remaining,0);
  f.act("staf","sale.finalize",{id:ids.shipmentId});const invoiceId=issue(f,ids.shipmentLineId);assert.equal(invoiceTotal(f.s,invoiceId),20000);
  const payment=f.act("customer-demo","payment.record",{invoiceId,amount:25000});f.act("penagihan","payment.verify",{id:payment});f.act("penagihan","payment.allocate",{paymentId:payment,lines:[{invoiceId,amount:20000}]});assert.equal(paymentAvailable(f.s,payment),5000);
  const refund=f.act("penagihan","refund.request",{paymentId:payment,amount:5000,reason:"Kelebihan simulasi"});f.act("pimpinan","refund.approve",{id:refund});f.act("penagihan","refund.pay",{id:refund});assert.equal(paymentAvailable(f.s,payment),0);
  const credit=f.act("penagihan","credit.request",{invoiceId,amount:3000,reason:"Koreksi layanan"});f.act("pimpinan","credit.approve",{id:credit});const balance=f.s.payments.find(p=>p.sourceCreditId===credit)!;assert.equal(balance.customerId,"customer-demo");assert.equal(balance.divisionId,null);assert.equal(paymentAvailable(f.s,balance.id),3000);
  assert.equal(scopeState(f.s,f.actor("customer-b")).payments.length,0);assert.equal(scopeState(f.s,f.actor("customer-demo")).refunds[0].id,refund);
  denied(f,()=>f.act("penagihan","invoice.issue",{customerId:"customer-demo",dueDate:f.date,lines:[{shipmentLineId:ids.shipmentLineId}]}),/sudah ditagih/);
});

test("v7 no customer payment exists before an owned issued invoice and cross-buyer money never allocates",()=>{
  const f=fixture(40);
  denied(f,()=>f.act("customer-demo","payment.record",{amount:10000}),/Invoice/);
  denied(f,()=>f.act("customer-demo","payment.record",{invoiceId:"missing",amount:10000}),/ditemukan/);
  const a=final(f),b=final(f,"customer-b"),pic=final(f,"pic-a");
  denied(f,()=>f.act("penagihan","invoice.issue",{customerId:"customer-demo",dueDate:f.date,lines:[{shipmentLineId:a.shipmentLineId},{shipmentLineId:b.shipmentLineId}]}),/pembeli/);
  const ai=issue(f,a.shipmentLineId),bi=issue(f,b.shipmentLineId),pi=issue(f,pic.shipmentLineId);
  for(const invoiceId of [bi,pi])denied(f,()=>f.act("customer-demo","payment.record",{invoiceId,amount:1}),/pembeli lain/);
  const payment=f.act("customer-demo","payment.record",{invoiceId:ai,amount:30000});
  denied(f,()=>f.act("penagihan","payment.verify",{id:payment,customerId:"customer-b"}),/tidak dapat diganti/);
  f.act("penagihan","payment.verify",{id:payment});
  for(const invoiceId of [bi,pi])denied(f,()=>f.act("penagihan","payment.allocate",{paymentId:payment,lines:[{invoiceId,amount:1}]}),/pembayar yang sama/);
  denied(f,()=>f.act("penagihan","payment.identify",{id:payment,divisionId:"div-ops",reason:"Ganti"}),/tidak dapat dipindahkan/);
});

test("v7 customer scope and evidence are isolated across two customers and PIC without internal metadata",()=>{
  const f=fixture(40),records=[];
  for(const who of ["customer-demo","customer-b","pic-a"]){const ids=final(f,who),invoiceId=issue(f,ids.shipmentLineId),paymentId=f.act(who,"payment.record",{invoiceId,amount:20000});f.act("penagihan","payment.verify",{id:paymentId});f.act("penagihan","payment.allocate",{paymentId,lines:[{invoiceId,amount:10000}]});const receipt=f.act(who,"attachment.add",file("receipt",ids.shipmentId)),proof=f.act(who,"attachment.add",file("payment",paymentId));records.push({who,...ids,invoiceId,paymentId,receipt,proof});}
  f.act("penagihan","payment.record",{amount:1000,payer:"Unidentified"});
  for(const own of records){const view=scopeState(f.s,f.actor(own.who));assert.deepEqual(view.orders.map(o=>o.id),[own.orderId]);assert.deepEqual(view.invoices.map(i=>i.id),[own.invoiceId]);assert.deepEqual(view.payments.map(p=>p.id),[own.paymentId]);assert.equal(view.allocations.length,1);assert.equal(view.attachments.length,2);assert.deepEqual(view.users.map(u=>u.id),[own.who]);for(const other of records.filter(row=>row!==own)){assert.equal(canReadAttachment(f.s,f.actor(own.who),other.proof),false);assert.throws(()=>attachmentContext(f.s,f.actor(own.who),"receipt",other.shipmentId),/pembeli lain/);denied(f,()=>f.act(own.who,"attachment.add",file("payment",other.paymentId)),/pembeli lain/);denied(f,()=>f.act(own.who,"complaint.create",{shipmentLineId:other.shipmentLineId,qty:1,reason:"Foreign"}),/pembeli lain/);}}
  const customer=scopeState(f.s,f.actor("customer-demo")),serialized=JSON.stringify(customer);for(const secret of ["PRIVATE-SUPPLIER","PRIVATE-BATCH","PRIVATE-WAREHOUSE","PRIVATE-CONTACT-B","PRIVATE-ADDRESS-B","private-b@example.invalid"])assert.equal(serialized.includes(secret),false);
  for(const key of ["divisions","suppliers","purchases","purchaseLines","supplierPayments","stocktakes","stockReturns","movements","journals","journalLines","periods","audits","expenses"] as const)assert.equal(customer[key].length,0,key);
  const staff=scopeState(f.s,f.actor("staf"));assert.equal(staff.users.some(user=>user.id==="customer-demo"||user.id==="customer-b"),false);assert.equal(buyerLabel(staff,staff.orders.find(order=>order.customerId==="customer-demo")!),"Pelanggan Demo");assert.equal(staff.invoices.length,0);
});

test("v7 customer substitution, cancellation and receipt actions require the actual owner",()=>{
  const f=fixture(),orderId=create(f),line=f.s.orderLines.find(line=>line.orderId===orderId)!;f.act("kepala","order.review",{id:orderId,approve:true});f.act("staf","substitution.propose",{orderLineId:line.id,productId:"q",reason:"Pengganti tersedia"});const substitution=f.s.substitutions[0].id;
  for(const who of ["customer-b","pic-a"]){denied(f,()=>f.act(who,"substitution.decide",{id:substitution,approve:true}),/pembeli lain/);denied(f,()=>f.act(who,"order.cancel",{id:orderId,reason:"Foreign"}),/pembeli lain/);}
  f.act("customer-demo","substitution.decide",{id:substitution,approve:true});const replacement=f.s.orderLines.find(row=>row.productId==="q")!;f.act("staf","stock.reserve",{orderLineId:replacement.id,qty:3});const sh=f.act("staf","shipment.create",{orderId,courierId:"kurir",lines:[{orderLineId:replacement.id,qty:3}]});f.act("kurir","shipment.dispatch",{id:sh});const sl=f.s.shipmentLines.find(row=>row.shipmentId===sh)!;
  denied(f,()=>f.act("customer-b","shipment.receive",{id:sh,lines:[{id:sl.id,accepted:3}]}),/pembeli lain/);f.act("customer-demo","shipment.receive",{id:sh,lines:[{id:sl.id,accepted:3}]});f.act("customer-demo","complaint.create",{shipmentLineId:sl.id,qty:1,reason:"Satu cacat"});assert.equal(f.s.complaints[0].customerId,"customer-demo");
});

test("v7 business validation rejects ambiguous owners and corrupted cross-customer allocation",()=>{
  const f=fixture(30),a=final(f),b=final(f,"customer-b"),ai=issue(f,a.shipmentLineId),bi=issue(f,b.shipmentLineId),payment=f.act("customer-demo","payment.record",{invoiceId:ai,amount:30000});f.act("penagihan","payment.verify",{id:payment});
  const corrupt=structuredClone(f.s);corrupt.allocations.push({id:"bad",paymentId:payment,invoiceId:bi,amount:1,date:f.date});assert.throws(()=>validateState(corrupt),/lintas pembeli/);
  const ambiguous=structuredClone(f.s);ambiguous.orders[0].divisionId="div-ops";assert.throws(()=>validateState(ambiguous),/Identitas pembeli/);
});

test("v7 failed customer checkout is retryable with the same command after a transient stock issue",()=>{
  const f=fixture(),command:Command={id:crypto.randomUUID(),type:"order.create",data:{recipientName:"Penerima",recipientPhone:"0800000000",address:"Alamat",lines:[{productId:"p",qty:3,unitPrice:10000}]}};
  f.s.batches[0].held=9;denied(f,()=>runCommand(f.s,f.actor("customer-demo"),command),/Stok barang/);f.s.batches[0].held=0;
  const result=runCommand(f.s,f.actor("customer-demo"),command);assert.equal(result.state.orders.length,1);assert.equal(result.state.orders[0].customerId,"customer-demo");assert.equal(result.state.batches[0].qty,10);
  // Successful transport retries are deduplicated by repository.execute, not by this pure transition function.
});

test("v7 customer account type cannot be promoted or reused as an internal identity",()=>{
  const f=fixture();create(f);
  for(const role of ["pic","staf","admin"])denied(f,()=>f.act("admin","admin.user",{id:"customer-demo",role,divisionId:"div-ops",active:true}),/tetap terpisah/);
  denied(f,()=>f.act("admin","admin.user",{id:"staf",role:"customer",active:true}),/tetap terpisah/);
  denied(f,()=>f.act("admin","admin.user",{id:"customer-b",role:"staf",active:true}),/tetap terpisah/);
  f.act("admin","admin.user",{id:"customer-demo",role:"customer",active:false});
  assert.throws(()=>scopeState(f.s,f.actor("customer-demo")),/akses/);
  denied(f,()=>create(f),/tidak aktif/);
  f.act("admin","admin.user",{id:"customer-demo",role:"customer",active:true});assert.equal(scopeState(f.s,f.actor("customer-demo")).orders.length,1);
});
