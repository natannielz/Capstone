import {test} from "node:test";
import assert from "node:assert/strict";
import {emptyState} from "../lib/domain/seed";
import {allocationInvoices, billableRows, invoiceBuyerOptions, invoiceSelection} from "../lib/domain/invoice-selection";
import type {BuyerRef} from "../lib/domain/buyers";
import type {Payment} from "../lib/domain/model";

const customerA:BuyerRef={divisionId:null,customerId:"same-id"};
const customerB:BuyerRef={divisionId:null,customerId:"customer-b"};
const division:BuyerRef={divisionId:"same-id"};

function fixture() {
  const state=emptyState();
  state.divisions=[{id:"same-id",name:"Divisi Contoh",code:"DIV",address:"Gedung divisi"}];
  // Operational projections deliberately contain no customer account directory.
  state.users=[];
  state.products=[{id:"product",sku:"SKU-01",name:"Produk contoh",category:"OMI",unit:"pak",price:900,minimum:1,returnMonths:1,active:true}];
  function receipt(id:string,buyer:BuyerRef,name:string) {
    state.orders.push({id:`order-${id}`,number:`PO-${id}`,...buyer,buyerName:name,createdBy:"fixture",createdAt:"2026-09-20T08:00:00Z",neededAt:"2026-09-20",address:"Alamat penerima",note:"",status:"approved",origin:"fixture"});
    state.orderLines.push({id:`order-line-${id}`,orderId:`order-${id}`,productId:"product",requestedProductId:"product",qty:2,cancelled:0,price:500,note:""});
    state.shipments.push({id:`shipment-${id}`,number:`SJ-${id}`,orderId:`order-${id}`,courierId:"courier",vehicle:"DEMO",date:"2026-09-20",receivedDate:"2026-09-21",status:"received",receiver:"Penerima",receivedAt:"2026-09-21T08:00:00Z",evidence:"",note:""});
    state.shipmentLines.push({id,shipmentId:`shipment-${id}`,orderLineId:`order-line-${id}`,batchId:"batch",qty:2,accepted:2,returned:0,returnGood:0,finalized:true,finalizedDate:"2026-09-21",price:500,cost:300});
  }
  receipt("line-a",customerA,"Nama pelanggan A");
  receipt("line-b",customerB,"Nama pelanggan B");
  receipt("line-division",division,"Divisi Contoh");
  function invoice(id:string,buyer:BuyerRef,paid=false) {
    state.invoices.push({id,number:`INV-${id}`,...buyer,date:"2026-09-21",dueDate:"2026-10-21",status:"issued",taxBps:0,taxNote:""});
    state.invoiceLines.push({id:`invoice-line-${id}`,invoiceId:id,shipmentLineId:`previous-receipt-${id}`,qty:1,price:1000,subtotal:1000,tax:0});
    if(paid)state.allocations.push({id:`allocation-${id}`,paymentId:"earlier-payment",invoiceId:id,amount:1000,date:"2026-09-21"});
  }
  invoice("invoice-a",customerA);
  invoice("invoice-b",customerB);
  invoice("invoice-division",division);
  invoice("invoice-paid",customerA,true);
  return state;
}

function payment(buyer:BuyerRef):Payment {
  return {id:"payment",...buyer,amount:1000,date:"2026-09-21",reference:"SIMULATION",payer:"Pembayar",note:"",status:"verified",verifiedBy:"penagihan",evidence:"",createdBy:"fixture"};
}

test("invoice chooser derives distinct customer and division options from receipts without account-directory access",()=>{
  const state=fixture();
  const options=invoiceBuyerOptions(state);
  assert.equal(options.length,3);
  assert.equal(options.find(option=>option.key==="customer:same-id")?.label,"Nama pelanggan A");
  assert.equal(options.find(option=>option.key==="customer:customer-b")?.label,"Nama pelanggan B");
  assert.equal(options.find(option=>option.key==="division:same-id")?.label,"Divisi Contoh");
  assert.ok(options.every(option=>option.count===1));
  assert.equal(billableRows(state).find(row=>row.id==="line-a")?.customerId,"same-id");
});

test("invoice selection accepts legacy division IDs and customer refs but never matches null or a different customer",()=>{
  const state=fixture();
  assert.equal(invoiceSelection(state,"same-id",["line-division"]).error,"");
  assert.equal(invoiceSelection(state,customerA,["line-a"]).error,"");
  assert.equal(invoiceSelection(state,customerA,["line-a"]).total,1000);
  assert.equal(invoiceSelection(state,customerA,["line-a"]).minimumDate,"2026-09-21");
  for(const [buyer,ids] of [[customerA,["line-b"]],[customerA,["line-division"]],[customerB,["line-a"]],[null,["line-a"]],[{divisionId:"same-id",customerId:"same-id"},["line-a"]]] as [BuyerRef|null,string[]][]) {
    assert.notEqual(invoiceSelection(state,buyer,ids).error,"");
    assert.equal(invoiceSelection(state,buyer,ids).selected.length,0);
  }
});

test("invoice selection refuses mixed customer receipt IDs and removed selections",()=>{
  const state=fixture();
  assert.match(invoiceSelection(state,customerA,["line-a","line-b"]).error,/tidak tersedia/);
  state.invoiceLines.push({id:"newly-billed",invoiceId:"invoice-a",shipmentLineId:"line-a",qty:2,price:500,subtotal:1000,tax:0});
  assert.match(invoiceSelection(state,customerA,["line-a"]).error,/sudah ditagih/);
  assert.equal(invoiceBuyerOptions(state).some(option=>option.key==="customer:same-id"),false);
});

test("payment allocation lists only the exact buyer even when customers share null division IDs",()=>{
  const state=fixture();
  assert.deepEqual(allocationInvoices(state,payment(customerA)).map(invoice=>invoice.id),["invoice-a"]);
  assert.deepEqual(allocationInvoices(state,payment(customerB)).map(invoice=>invoice.id),["invoice-b"]);
  assert.deepEqual(allocationInvoices(state,payment(division)).map(invoice=>invoice.id),["invoice-division"]);
  assert.deepEqual(allocationInvoices(state,payment({divisionId:null})),[]);
  assert.deepEqual(allocationInvoices(state,payment({divisionId:"same-id",customerId:"same-id"})),[]);
});

test("refresh retains selected settled invoices for validation without allowing another buyer's selection",()=>{
  const state=fixture();
  const rows=allocationInvoices(state,payment(customerA),["invoice-paid","invoice-b","invoice-division"]);
  assert.deepEqual(rows.map(invoice=>invoice.id),["invoice-a","invoice-paid"]);
});
