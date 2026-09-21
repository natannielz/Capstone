import { DEMO_ACCOUNTS } from "./accounts";
import type { State } from "./model";
import { runCommand, journal } from "./engine";
import { today, sum } from "./selectors";
export function emptyState():State { return {revision:0,users:[],divisions:[],products:[],suppliers:[],batches:[],orders:[],orderLines:[],reservations:[],substitutions:[],shipments:[],shipmentLines:[],complaints:[],invoices:[],invoiceLines:[],payments:[],allocations:[],credits:[],refunds:[],purchases:[],purchaseLines:[],supplierPayments:[],stockReturns:[],stocktakes:[],expenses:[],movements:[],journals:[],journalLines:[],periods:[],attachments:[],audits:[]}; }
export function seedState(){
  let s=emptyState();const date=today();s.users=DEMO_ACCOUNTS.map(({id,name,role,divisionId,active})=>({id,name,role,divisionId,active,email:`${id}@unit-toko.demo`,avatar:`/images/avatars/${id}.png`,phone:"",position:role==="pic"?"Penanggung jawab kebutuhan divisi":"Tim Unit Toko"}));
  s.divisions=[{id:"div-ops",name:"Divisi Operasional",code:"OPS",address:"Gedung BNI Simulasi, Lantai 8, Jakarta"},{id:"div-ti",name:"Divisi Teknologi",code:"TI",address:"Gedung BNI Simulasi, Lantai 12, Jakarta"},{id:"div-sdm",name:"Divisi Human Capital",code:"HC",address:"Gedung BNI Simulasi, Lantai 6, Jakarta"}];
  s.suppliers=[{id:"sup-omi",name:"OMI • pemasok simulasi",category:"OMI"},{id:"sup-smart",name:"Vendor Merchandise Demo",category:"Smart"},{id:"sup-pasar",name:"Grosir Mitra Demo",category:"Pasar Kering"}];
  const catalog:[string,string,string,"OMI"|"Smart",string,number,number,number][]=[
    ["air","OMI-001","Air mineral 600 ml","OMI","dus",54000,45000,36],["teh","OMI-002","Teh celup 25 kantong","OMI","kotak",12000,9000,64],["kopi","OMI-003","Kopi sachet 20 pcs","OMI","pak",28500,23000,32],["gula","OMI-004","Gula pasir 1 kg","OMI","pak",18000,15500,25],["tisu","OMI-005","Tisu wajah 250 lembar","OMI","pak",15500,12000,48],["biskuit","OMI-006","Biskuit assorted 300 g","OMI","kaleng",38000,31000,8],["cup","OMI-007","Paper cup 8 oz","OMI","pak",22000,17000,42],["galon","SMT-001","Air mineral galon 15 L","Smart","galon",23500,19000,20],["tumbler","SMT-002","Tumbler stainless 500 ml","Smart","pcs",85000,62000,15],["tas","SMT-003","Tas belanja kanvas","Smart","pcs",45000,32000,24],["kaos","SMT-004","Kaos polo merchandise","Smart","pcs",110000,78000,10],["snack","OMI-008","Snack box rapat","OMI","box",25000,18500,8]
  ];
  for(const [id,sku,name,category,unit,price,cost,qty] of catalog){s.products.push({id,sku,name,category,unit,price,minimum:10,returnMonths:category==="OMI"?1:0,active:true});s.batches.push({id:`batch-${id}`,productId:id,code:`LOT-${sku}-01`,qty,held:0,cost,expiry:category==="OMI"?"2027-06-30":"",location:category==="OMI"?"Rak A":"Gudang merchandise"});}
  // Packaging variants are distinct SKUs with their own price and stock unit.
  for(const base of [...s.products])for(const count of [3,6]){
    const id=`${base.id}--${count}`,original=s.batches.find(b=>b.productId===base.id)!;
    s.products.push({...base,id,sku:`${base.sku}-P${count}`,name:`${base.name} · paket ${count} ${base.unit}`,unit:"paket",price:base.price*count,minimum:5});
    s.batches.push({...original,id:`batch-${id}`,productId:id,code:`LOT-${base.sku}-P${count}`,qty:10+count*2,cost:original.cost*count});
  }
  const value=sum(s.batches.map(b=>b.qty*b.cost));journal(s,date,"opening","Saldo awal demo",[["inventory",value,0],["cash",5000000,0],["capital",0,value+5000000]]);
  function act(account:string,type:string,data:Record<string,unknown>){const r=runCommand(s,s.users.find(x=>x.id===account)!,{id:crypto.randomUUID(),type,date,data});s=r.state;return r.result.id;}
  const first=act("pic-a","order.create",{neededAt:date,note:"Kebutuhan rapat koordinasi mingguan.",lines:[{productId:"air",qty:10},{productId:"teh",qty:6},{productId:"gula",qty:4}]});
  act("kepala","order.review",{id:first,approve:true});for(const l of s.orderLines.filter(l=>l.orderId===first))act("staf","stock.reserve",{orderLineId:l.id,qty:l.qty});
  const shipment=act("staf","shipment.create",{orderId:first,courierId:"kurir",vehicle:"B 1234 DEMO",lines:s.orderLines.filter(l=>l.orderId===first).map(l=>({orderLineId:l.id,qty:l.qty}))});
  act("staf","shipment.dispatch",{id:shipment});act("pic-a","shipment.receive",{id:shipment,receiver:"Nadia Putri",lines:s.shipmentLines.filter(l=>l.shipmentId===shipment).map(l=>({id:l.id,accepted:l.qty}))});act("staf","sale.finalize",{id:shipment});
  const due=new Date(date+"T12:00:00Z");due.setUTCDate(due.getUTCDate()+30);const invoice=act("penagihan","invoice.issue",{divisionId:"div-ops",dueDate:due.toISOString().slice(0,10),lines:s.shipmentLines.filter(l=>l.shipmentId===shipment).map(l=>({shipmentLineId:l.id}))});
  const payment=act("penagihan","payment.record",{divisionId:"div-ops",amount:350000,reference:"TRF-DEMO-001",payer:"Divisi Operasional"});act("penagihan","payment.verify",{id:payment});act("penagihan","payment.allocate",{paymentId:payment,lines:[{invoiceId:invoice,amount:350000}]});
  const second=act("pic-b","order.create",{neededAt:date,note:"Persiapan workshop tim teknologi.",lines:[{productId:"tumbler",qty:12},{productId:"tas",qty:12}]});act("kepala","order.review",{id:second,approve:true});for(const l of s.orderLines.filter(l=>l.orderId===second))act("staf","stock.reserve",{orderLineId:l.id,qty:l.qty});act("staf","shipment.create",{orderId:second,courierId:"kurir",vehicle:"B 5678 DEMO",lines:s.orderLines.filter(l=>l.orderId===second).map(l=>({orderLineId:l.id,qty:l.qty}))});
  act("pic-a","order.create",{neededAt:date,note:"Kebutuhan pantry akhir pekan.",lines:[{productId:"biskuit",qty:12},{productId:"kopi",qty:5}]});
  act("pic-b","order.create",{neededAt:date,note:"Kebutuhan ruang pelatihan.",lines:[{productId:"snack",qty:15},{productId:"air",qty:5}]});
  act("penagihan","payment.record",{amount:175000,reference:"",payer:"Transfer pusat — belum diketahui",note:"Menunggu konfirmasi divisi."});return s;
}
