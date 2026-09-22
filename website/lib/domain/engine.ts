import {isRole, type Actor, type Role} from "./accounts";
import { DomainError, type State, type Command, type CommandResult, type Order, type OrderLine, type Purchase } from "./model";
import { allowed, get, sum, available, productAvailable, lineProgress, invoiceTotal, invoiceBalance, paymentAvailable, financialReport, today, attachmentContext, stocktakeStatus } from "./selectors";
import {assertBuyerAccess, buyerKey, buyerRefForActor, sameBuyer, type BuyerRef} from "./buyers";

export function integer(v:unknown,name="Jumlah",min=1,max=100000000000){if(typeof v!=="number"||!Number.isSafeInteger(v)||v<min||v>max)throw new DomainError(`${name} harus bilangan bulat ${min}–${max}.`);return v;}
export function string(v:unknown,name="Isian",required=true,max=2000){if(typeof v!=="string"||(required&&!v.trim())||v.length>max)throw new DomainError(`${name} belum benar atau terlalu panjang.`);return v.trim();}
function rows(v:unknown){if(!Array.isArray(v)||!v.length||v.length>100)throw new DomainError("Pilih 1–100 baris barang.");return v as Record<string,unknown>[];}
function dateValue(v:unknown){const value=string(v,"Tanggal");if(!/^\d{4}-\d{2}-\d{2}$/.test(value)||!Number.isFinite(Date.parse(value))||new Date(value).toISOString().slice(0,10)!==value)throw new DomainError("Tanggal tidak valid.");return value;}
function chronology(date:string,sourceDate:string,label="dokumen sumber"){if(date<sourceDate)throw new DomainError(`Tanggal tindakan tidak boleh mendahului ${label}.`);}
const latestDate=(...dates:(string|undefined)[])=>dates.filter((value):value is string=>Boolean(value)).sort().at(-1)||"";
function auditDate(s:State,id:string,action:string){return latestDate(...s.audits.filter(a=>a.targetId===id&&a.action===action).map(a=>a.date));}
function orderCreatedDate(s:State,o:Order){return o.createdDate||auditDate(s,o.id,"order.create")||new Date(o.createdAt).toLocaleDateString("en-CA",{timeZone:"Asia/Jakarta"});}
function orderReviewedDate(s:State,o:Order){return latestDate(orderCreatedDate(s,o),o.reviewedDate||auditDate(s,o.id,"order.review"));}
function lineSourceDate(s:State,l:OrderLine){const o=get(s.orders,l.orderId),sub=s.substitutions.find(x=>x.replacementLineId===l.id);return latestDate(orderReviewedDate(s,o),l.createdDate,sub?.decidedDate||(sub?auditDate(s,o.id,"substitution.decide"):undefined));}
function lineActivityDate(s:State,l:OrderLine){
  const shipments=s.shipments.filter(sh=>s.shipmentLines.some(sl=>sl.shipmentId===sh.id&&sl.orderLineId===l.id));
  const shipmentIds=new Set(shipments.map(sh=>sh.id));
  return latestDate(lineSourceDate(s,l),auditDate(s,l.id,"stock.release"),...s.reservations.filter(r=>r.orderLineId===l.id).map(r=>r.date),
    ...shipments.flatMap(sh=>[sh.date,sh.receivedDate,sh.failedDate,sh.cancelledDate]),
    ...s.movements.filter(m=>shipmentIds.has(m.sourceId)&&m.type==="customer_return").map(m=>m.date));
}
function purchaseStageDate(s:State,p:Purchase,field:"confirmedDate"|"fundingRequestedDate"|"fundingApprovedDate"|"fundingDisbursedDate",action:string){return latestDate(p.date,p[field]||auditDate(s,p.id,action));}
function buyerFields(ref:BuyerRef):BuyerRef{return {divisionId:ref.divisionId,...(ref.customerId?{customerId:ref.customerId}:{})};}
function selectedBuyer(s:State,actor:Actor,data:Record<string,unknown>,allowUnknown=false):BuyerRef{
  const ref=buyerRefForActor(actor,{divisionId:data.divisionId?string(data.divisionId):null,...(data.customerId?{customerId:string(data.customerId)}:{})});
  if(!buyerKey(ref)){if(allowUnknown&&!ref.divisionId&&!ref.customerId)return ref;throw new DomainError("Pilih pembeli divisi atau pelanggan.");}
  if(ref.customerId){const customer=get(s.users,ref.customerId);if(customer.role!=="customer")throw new DomainError("Akun pembeli bukan pelanggan.");}
  else get(s.divisions,ref.divisionId);
  return ref;
}
function customerDate(s:State){const latest=s.periods.filter(p=>p.status==="closed").map(p=>p.id).sort().at(-1);if(!latest||latest<today().slice(0,7))return today();const [year,month]=latest.split("-").map(Number);return new Date(Date.UTC(year,month,1)).toISOString().slice(0,10);}
const uid=()=>crypto.randomUUID();
function period(s:State,date:string){const id=date.slice(0,7);let p=s.periods.find(p=>p.id===id);if(!p){p={id,revision:0,status:"open",approvedRevision:null,approvedBy:null,submittedBy:null,closedBy:null,closedAt:"",snapshot:null};s.periods.push(p);}return p;}
function open(s:State,date:string){if(s.periods.some(p=>p.status==="closed"&&p.id>=date.slice(0,7)))throw new DomainError("Periode sudah ditutup. Catat koreksi pada periode terbuka.",409);period(s,date);}
function touch(s:State,date:string){period(s,date);for(const p of s.periods.filter(p=>p.id>=date.slice(0,7))){p.revision++;if(p.status==="approved"||p.status==="review"){p.status="open";p.approvedRevision=null;p.approvedBy=null;}}}
export function journal(s:State,date:string,sourceId:string,description:string,entries:[string,number,number][]){open(s,date);if(sum(entries.map(e=>e[1]))!==sum(entries.map(e=>e[2])))throw new DomainError("Jurnal tidak seimbang.");const id=uid();s.journals.push({id,date,sourceId,description});for(const [account,debit,credit] of entries){integer(debit,"Debit",0);integer(credit,"Kredit",0);if(debit||credit)s.journalLines.push({id:uid(),journalId:id,account,debit,credit});}return id;}
function numbered(s:State,prefix:string,date:string,count:number){return `${prefix}/${date.slice(0,7).replace("-","")}/${String(count+1).padStart(4,"0")}`;}
export function runCommand(original:State,actor:Actor,command:Command):{state:State;result:CommandResult}{
  if(!actor.active||!isRole(actor.role))throw new DomainError("Akun tidak aktif atau peran tidak dikenal.",403);
  const s=structuredClone(original),d=command.data,date=actor.role==="customer"?customerDate(s):dateValue(command.date||today()); let target=String(d.id||command.id),message="Perubahan tersimpan.";
  const permit=(...roles:Role[])=>allowed(actor,roles);
  const batchDate=(batchId:string)=>{for(const m of s.movements.filter(m=>m.batchId===batchId))chronology(date,m.date,"mutasi batch sebelumnya");};
  const move=(batchId:string,qty:number,type:string,sourceId:string,note="")=>{
    batchDate(batchId);s.movements.push({id:uid(),batchId,date,type,qty,sourceId,note});
    if(qty!==0)for(const st of s.stocktakes.filter(x=>x.batchId===batchId&&x.status==="requested"&&x.id!==sourceId)){
      st.status="stale";st.invalidatedDate=date;st.resolutionReason="Stok berubah setelah penghitungan. Lakukan penghitungan ulang.";
    }
  };
  const ensureOrder=(id:unknown)=>{const o=get(s.orders,id);assertBuyerAccess(actor,o);return o;};
  if(!command.type.startsWith("period.")&&!command.type.startsWith("admin.")&&!command.type.startsWith("profile."))open(s,date);
  const independent=(id:string|null|undefined)=>{if(id===actor.id)throw new DomainError("Pengajuan harus disetujui oleh pengguna lain.",403);};
  switch(command.type){
    case "profile.update": {
      if(Object.keys(d).some(k=>!["name","phone","position",...(actor.role==="customer"?["address"]:[])].includes(k)))throw new DomainError("Perubahan profil tidak diizinkan.",403);
      const user=get(s.users,actor.id);user.name=string(d.name,"Nama lengkap",true,100);user.phone=string(d.phone||"","Nomor kontak",false,30);user.position=string(d.position||"","Jabatan",false,100);if(actor.role==="customer"&&d.address!==undefined)user.address=string(d.address,"Alamat bawaan",false,500);target=actor.id;message="Profil Anda diperbarui.";break;
    }
    case "profile.avatar": {const key=string(d.key);if(!/^avatar-[\w-]+$/.test(key))throw new DomainError("Foto tidak valid.");get(s.users,actor.id).avatar="/api/profile/avatar?id="+key;target=actor.id;message="Foto profil diperbarui.";break;}

    case "order.create": {
      permit("pic","kepala","customer");const buyer=selectedBuyer(s,actor,d);const id=uid();target=id;
      const lines=rows(d.lines);if(new Set(lines.map(l=>l.productId)).size!==lines.length)throw new DomainError("Gabungkan produk yang sama dalam satu baris.");
      const recipient=buyer.customerId?{recipientName:string(d.recipientName||actor.name,"Nama penerima",true,100),recipientPhone:string(d.recipientPhone||actor.phone,"Nomor kontak penerima",true,30)}:{};
      s.orders.push({id,number:numbered(s,"PO",date,s.orders.length),...buyer,...(buyer.customerId?{buyerName:get(s.users,buyer.customerId).name}:{}),createdBy:actor.id,createdAt:new Date().toISOString(),createdDate:date,neededAt:actor.role==="customer"?date:dateValue(d.neededAt),address:string(d.address||(buyer.divisionId?get(s.divisions,buyer.divisionId).address:actor.address),"Alamat"),...recipient,channel:buyer.customerId?"customer":"division",note:string(d.note||"","Catatan",false),status:"submitted",origin:actor.role==="customer"?"Etalase pelanggan":actor.role==="pic"?"Portal divisi":"Dicatat Kepala Toko"});
      for(const l of lines){const product=get(s.products,l.productId);if(!product.active)throw new DomainError("Produk tidak aktif.");const qty=integer(l.qty,"Jumlah",1,actor.role==="customer"?10000:100000000000);if(actor.role==="customer"){if(l.unitPrice!==product.price)throw new DomainError("Harga barang berubah atau belum ditinjau. Muat ulang dan tinjau pesanan kembali.",409);if(qty>productAvailable(s,product.id,date))throw new DomainError("Stok barang berubah. Sesuaikan jumlah sebelum membuat pesanan.",409);}s.orderLines.push({id:uid(),orderId:id,productId:product.id,requestedProductId:product.id,qty,cancelled:0,createdDate:date,price:product.price,note:string(l.note||"","Catatan barang",false)});}message="Pesanan diajukan kepada Kepala Toko.";break;
    }
    case "order.review": {
      permit("kepala");const o=ensureOrder(d.id);chronology(date,orderCreatedDate(s,o),"pembuatan pesanan");if(o.status!=="submitted")throw new DomainError("Pesanan sudah ditinjau.",409);o.reviewedDate=date;o.status=d.approve===true?"approved":"rejected";if(o.status==="rejected")o.note+=`\nDitolak: ${string(d.reason,"Alasan")}`;message=o.status==="approved"?"Pesanan diteruskan ke Staf Toko.":"Pesanan ditolak.";break;
    }
    case "order.cancel": {
      permit("pic","kepala","customer");const o=ensureOrder(d.id);if(o.status==="cancelled"||o.status==="rejected")throw new DomainError("Pesanan tidak aktif.");const reason=string(d.reason,"Alasan pembatalan");
      const lines=s.orderLines.filter(l=>l.orderId===o.id),shipments=s.shipments.filter(sh=>sh.orderId===o.id);
      chronology(date,latestDate(orderReviewedDate(s,o),...s.audits.filter(a=>a.targetId===o.id).map(a=>a.date),...lines.map(l=>lineActivityDate(s,l))),"aktivitas pesanan sebelumnya");
      if(shipments.some(sh=>sh.status==="ready"))throw new DomainError("Pesanan sudah memiliki Surat Jalan siap kirim. Selesaikan penanganannya sebelum membatalkan sisa.");
      if(!lines.some(l=>lineProgress(s,l).remaining>0))throw new DomainError("Tidak ada sisa pesanan yang dapat dibatalkan.");
      for(const l of lines){const progress=lineProgress(s,l);l.cancelled+=progress.remaining;for(const r of s.reservations.filter(r=>r.orderLineId===l.id))r.qty=0;}
      for(const sub of s.substitutions.filter(x=>x.status==="pending"&&lines.some(l=>l.id===x.orderLineId))){sub.status="cancelled";sub.cancelledReason=`Pesanan dibatalkan: ${reason}`;sub.decidedDate=date;sub.decidedBy=actor.id;}
      if(lines.every(l=>lineProgress(s,l).shipped===0))o.status="cancelled";
      o.cancelledDate=date;o.note+=`\nSisa dibatalkan: ${reason}`;message="Sisa pesanan yang belum dikirim dibatalkan.";break;
    }
    case "stock.reserve": {
      permit("staf");const l=get(s.orderLines,d.orderLineId),o=ensureOrder(l.orderId);chronology(date,lineActivityDate(s,l),"persetujuan atau pemenuhan barang pesanan");if(o.status!=="approved")throw new DomainError("Pesanan harus ditinjau lebih dahulu.");if(s.substitutions.some(x=>x.orderLineId===l.id&&x.status==="pending"))throw new DomainError("Tunggu konfirmasi barang pengganti.");
      let qty=integer(d.qty);const p=lineProgress(s,l);if(qty>p.remaining-p.reserved)throw new DomainError("Jumlah reservasi melebihi sisa pesanan.");
      const batches=s.batches.filter(b=>b.productId===l.productId&&available(s,b,date)>0).sort((a,b)=>(a.expiry||"9999").localeCompare(b.expiry||"9999"));if(sum(batches.map(b=>available(s,b,date)))<qty)throw new DomainError("Stok tersedia tidak cukup. Sesuaikan jumlah atau lakukan pengadaan.",409);
      for(const b of batches){const n=Math.min(qty,available(s,b,date));if(n){batchDate(b.id);s.reservations.push({id:uid(),orderLineId:l.id,batchId:b.id,qty:n,date});qty-=n;}if(!qty)break;}target=o.id;message="Stok dicadangkan untuk pesanan ini.";break;
    }
    case "stock.release": {
      permit("staf");const l=get(s.orderLines,d.orderLineId),o=ensureOrder(l.orderId);
      if(o.status!=="approved")throw new DomainError("Pesanan tidak sedang diproses.");
      chronology(date,lineActivityDate(s,l),"aktivitas pemenuhan pesanan sebelumnya");
      const b=get(s.batches,d.batchId),reason=string(d.reason,"Alasan pelepasan cadangan"),qty=integer(d.qty);
      batchDate(b.id);
      const reservations=s.reservations.filter(r=>r.orderLineId===l.id&&r.batchId===b.id&&r.qty>0);
      const staged=sum(s.shipmentLines.filter(sl=>sl.orderLineId===l.id&&sl.batchId===b.id&&get(s.shipments,sl.shipmentId).status==="ready").map(sl=>sl.qty));
      if(qty>sum(reservations.map(r=>r.qty))-staged)throw new DomainError("Jumlah melebihi cadangan bebas pada batch ini. Batalkan Surat Jalan siap kirim terlebih dahulu bila perlu.",409);
      let remaining=qty;
      for(const r of reservations){const released=Math.min(remaining,r.qty);r.qty-=released;remaining-=released;if(!remaining)break;}
      target=l.id;message=`Cadangan ${qty} ${get(s.products,l.productId).unit} dari batch ${b.code} dilepas. Alasan: ${reason}`;break;
    }
    case "substitution.propose": {
      permit("staf","kepala");const l=get(s.orderLines,d.orderLineId);const o=ensureOrder(l.orderId);chronology(date,lineActivityDate(s,l),"persetujuan atau pemenuhan barang pesanan");if(o.status!=="approved")throw new DomainError("Pesanan belum disetujui.");if(lineProgress(s,l).staged>0||lineProgress(s,l).remaining<=0)throw new DomainError("Batalkan Surat Jalan yang belum berangkat sebelum mengganti sisa barang.");if(s.substitutions.some(x=>x.orderLineId===l.id&&x.status==="pending"))throw new DomainError("Usulan pengganti masih menunggu keputusan.");const p=get(s.products,d.productId);if(p.id===l.productId||!p.active)throw new DomainError("Pilih produk pengganti yang aktif dan berbeda.");
      const id=uid();s.substitutions.push({id,orderLineId:l.id,productId:p.id,price:p.price,qty:lineProgress(s,l).remaining,reason:string(d.reason,"Alasan"),status:"pending",createdDate:date,requestedBy:actor.id,decidedBy:null});target=l.orderId;message="Usulan pengganti menunggu persetujuan PIC.";break;
    }
    case "substitution.decide": {
      permit("pic","customer");const sub=get(s.substitutions,d.id),l=get(s.orderLines,sub.orderLineId);const order=ensureOrder(l.orderId);chronology(date,latestDate(lineActivityDate(s,l),sub.createdDate||auditDate(s,order.id,"substitution.propose")),"usulan barang pengganti");if(d.approve===true&&(order.status!=="approved"||lineProgress(s,l).staged>0||lineProgress(s,l).remaining!==(sub.qty??l.qty)))throw new DomainError("Pesanan berubah. Minta toko meninjau ulang usulan.",409);if(sub.status!=="pending")throw new DomainError("Usulan sudah diputuskan.",409);sub.status=d.approve===true?"approved":"rejected";sub.decidedBy=actor.id;sub.decidedDate=date;
      if(sub.status==="approved"){if(!get(s.products,sub.productId).active)throw new DomainError("Barang pengganti sudah dinonaktifkan. Tolak usulan ini dan minta pilihan baru dari toko.",409);for(const r of s.reservations.filter(r=>r.orderLineId===l.id))r.qty=0;const qty=lineProgress(s,l).remaining;l.cancelled+=qty;const replacementId=uid();s.orderLines.push({id:replacementId,orderId:l.orderId,productId:sub.productId,requestedProductId:l.requestedProductId,qty,cancelled:0,createdDate:date,price:sub.price,note:`Pengganti sisa barang: ${sub.reason}`});sub.replacementLineId=replacementId;}target=l.orderId;message="Keputusan penggantian tersimpan.";break;
    }
    case "shipment.create": {
      permit("staf");const o=ensureOrder(d.orderId);chronology(date,orderReviewedDate(s,o),"persetujuan pesanan");if(o.status!=="approved")throw new DomainError("Pesanan belum siap diproses.");const courier=get(s.users,d.courierId);if(courier.role!=="kurir"||!courier.active)throw new DomainError("Pilih kurir aktif.");const id=uid();target=id;
      s.shipments.push({id,number:numbered(s,"SJ",date,s.shipments.length),orderId:o.id,courierId:courier.id,vehicle:string(d.vehicle||"B 1234 DEMO"),date,status:"ready",receiver:"",receivedAt:"",evidence:"",note:string(d.note||"","Catatan",false)});
      const selected=rows(d.lines);if(new Set(selected.map(x=>x.orderLineId)).size!==selected.length)throw new DomainError("Baris Surat Jalan duplikat.");
      for(const item of selected){const l=get(s.orderLines,item.orderLineId);if(l.orderId!==o.id)throw new DomainError("Barang berasal dari pesanan lain.");chronology(date,lineActivityDate(s,l),"persetujuan atau pemenuhan barang pesanan");for(const r of s.reservations.filter(r=>r.orderLineId===l.id&&r.qty>0))chronology(date,r.date||auditDate(s,o.id,"stock.reserve")||lineSourceDate(s,l),"pencadangan stok");if(s.substitutions.some(sub=>sub.orderLineId===l.id&&sub.status==="pending"))throw new DomainError("Tunggu keputusan PIC untuk barang pengganti.");let qty=integer(item.qty);const progress=lineProgress(s,l);if(qty>progress.reserved-progress.staged)throw new DomainError("Cadangkan stok sebelum membuat Surat Jalan.");
        const batchIds=[...new Set(s.reservations.filter(r=>r.orderLineId===l.id&&r.qty>0).map(r=>r.batchId))];
        for(const batchId of batchIds){const b=get(s.batches,batchId);const reserved=sum(s.reservations.filter(r=>r.orderLineId===l.id&&r.batchId===batchId).map(r=>r.qty));const staged=sum(s.shipmentLines.filter(x=>x.orderLineId===l.id&&x.batchId===batchId&&get(s.shipments,x.shipmentId).status==="ready").map(x=>x.qty));const n=Math.min(qty,Math.max(0,reserved-staged));if(n){s.shipmentLines.push({id:uid(),shipmentId:id,orderLineId:l.id,batchId,qty:n,accepted:0,returned:0,returnGood:0,finalized:false,price:l.price,cost:b.cost});qty-=n;}if(!qty)break;}
        if(qty)throw new DomainError("Reservasi tidak mencukupi baris pengiriman.");
      }
      message="Surat Jalan dibuat. Barang siap dikirim.";break;
    }
    case "shipment.dispatch": {
      permit("staf","kurir");const sh=get(s.shipments,d.id);chronology(date,sh.date,"pembuatan Surat Jalan");if(actor.role==="kurir"&&sh.courierId!==actor.id)throw new DomainError("Ini bukan tugas Anda.",403);if(sh.status!=="ready")throw new DomainError("Pengiriman sudah berjalan.",409);
      for(const l of s.shipmentLines.filter(x=>x.shipmentId===sh.id)){const b=get(s.batches,l.batchId);if(b.expiry&&b.expiry<=date)throw new DomainError("Batch sudah kedaluwarsa.");let qty=l.qty;const rs=s.reservations.filter(r=>r.orderLineId===l.orderLineId&&r.batchId===b.id);if(sum(rs.map(r=>r.qty))<qty||b.qty-b.held<qty)throw new DomainError("Reservasi atau stok berubah. Periksa pengiriman.",409);for(const r of rs){const n=Math.min(qty,r.qty);r.qty-=n;qty-=n;}b.qty-=l.qty;move(b.id,-l.qty,"dispatch",sh.id);}
      sh.status="dispatched";sh.date=date;message="Barang dikirim. Stok toko telah diperbarui.";break;
    }
    case "shipment.proof": {
      permit("kurir","staf");const sh=get(s.shipments,d.id);if(actor.role==="kurir"&&sh.courierId!==actor.id)throw new DomainError("Ini bukan tugas Anda.",403);if(sh.status!=="dispatched")throw new DomainError("Bukti dicatat untuk pengiriman berjalan.");chronology(date,latestDate(sh.date,sh.proofDate),"pengiriman atau bukti sebelumnya");sh.proofDate=date;sh.evidence=string(d.evidence,"Bukti serah terima");sh.receiver=string(d.receiver,"Nama penerima");sh.note=string(d.note||"","Catatan",false);message="Bukti pengantaran disimpan. Menunggu konfirmasi penerimaan.";break;
    }
    case "shipment.receive": {
      permit("pic","staf","customer");const sh=get(s.shipments,d.id),o=ensureOrder(sh.orderId);chronology(date,latestDate(sh.date,sh.proofDate),"pengiriman atau bukti pengantaran");if(sh.status!=="dispatched")throw new DomainError("Pengiriman belum dapat diterima.",409);sh.receiver=string(d.receiver||actor.name,"Nama penerima");
      // Only the protected upload service can publish attachment metadata through
      // HTTP. A delivery note alone cannot stand in for the buyer's signed proof.
      const receipt=s.attachments.find(a=>a.scope==="receipt"&&a.targetId===sh.id&&a.shipmentId===sh.id&&sameBuyer(a,o));
      if(actor.role==="staf"&&!receipt)throw new DomainError("Unggah foto atau PDF Surat Jalan bertanda tangan pada bagian Bukti penerimaan sebelum mengonfirmasi atas nama pembeli. Catatan saja belum cukup.");
      const incoming=rows(d.lines);const lines=s.shipmentLines.filter(x=>x.shipmentId===sh.id);if(incoming.length!==lines.length||new Set(incoming.map(x=>x.id)).size!==lines.length)throw new DomainError("Konfirmasikan seluruh baris pengiriman.");
      for(const l of lines){const input=incoming.find(x=>x.id===l.id);if(!input)throw new DomainError("Baris penerimaan tidak lengkap.");l.accepted=integer(input.accepted,"Jumlah diterima",0,l.qty);if(l.accepted<l.qty)s.complaints.push({id:uid(),shipmentId:sh.id,shipmentLineId:l.id,...buyerFields(o),qty:l.qty-l.accepted,reason:string(input.reason,"Alasan selisih"),status:"open",resolution:"",createdAt:new Date().toISOString(),createdDate:date});}
      sh.evidence=typeof d.evidence==="string"&&d.evidence?d.evidence:sh.evidence||`Dikonfirmasi akun ${actor.name}`;sh.status="received";sh.receivedAt=new Date().toISOString();sh.receivedDate=date;message="Penerimaan barang dicatat sesuai jumlah aktual.";break;
    }
    case "shipment.fail": {
      permit("kurir","staf");const sh=get(s.shipments,d.id);if(actor.role==="kurir"&&sh.courierId!==actor.id)throw new DomainError("Ini bukan tugas Anda.",403);if(sh.status!=="dispatched")throw new DomainError("Pengiriman tidak sedang berjalan.");chronology(date,latestDate(sh.date,sh.proofDate),"pengiriman atau bukti pengantaran");sh.failedDate=date;sh.status="failed";sh.note=string(d.reason,"Alasan gagal kirim");message="Pengiriman gagal dicatat. Catat barang kembali sebelum mengirim ulang.";break;
    }
    case "shipment.return": {
      permit("staf");const l=get(s.shipmentLines,d.shipmentLineId),sh=get(s.shipments,l.shipmentId);if(!["received","failed"].includes(sh.status))throw new DomainError("Barang belum dapat dikembalikan.");chronology(date,latestDate(sh.date,sh.receivedDate,sh.failedDate||auditDate(s,sh.id,"shipment.fail"),...s.complaints.filter(c=>c.shipmentLineId===l.id).map(c=>c.createdDate||auditDate(s,c.id,"complaint.create"))),"penerimaan, gagal kirim, atau komplain");const qty=integer(d.qty);if(qty>l.qty-l.accepted-l.returned)throw new DomainError("Jumlah kembali melebihi barang yang belum diterima.");const b=get(s.batches,l.batchId);b.qty+=qty;if(d.good!==true)b.held+=qty;else l.returnGood+=qty;l.returned+=qty;move(b.id,qty,"customer_return",sh.id,string(d.reason,"Alasan"));message="Barang kembali tercatat sekali pada stok.";break;
    }
    case "complaint.create": {
      permit("pic","customer");const l=get(s.shipmentLines,d.shipmentLineId),sh=get(s.shipments,l.shipmentId),o=ensureOrder(sh.orderId);chronology(date,sh.receivedDate||sh.date,"penerimaan");if(sh.status!=="received")throw new DomainError("Konfirmasikan penerimaan lebih dahulu.");if(l.finalized)throw new DomainError("Transaksi sudah final. Ajukan koreksi melalui Bagian Penagihan.");const qty=integer(d.qty,"Jumlah komplain",1,l.accepted);l.accepted-=qty;const id=uid();s.complaints.push({id,shipmentId:sh.id,shipmentLineId:l.id,...buyerFields(o),qty,reason:string(d.reason,"Alasan"),status:"open",resolution:"",createdAt:new Date().toISOString(),createdDate:date});target=id;message="Barang yang dikomplain dipisahkan dari kuantitas siap tagih.";break;
    }
    case "complaint.resolve": {
      permit("staf");const c=get(s.complaints,d.id);if(c.status!=="open")throw new DomainError("Komplain sudah selesai.",409);const l=get(s.shipmentLines,c.shipmentLineId),sh=get(s.shipments,l.shipmentId);chronology(date,latestDate(sh.receivedDate||sh.date,c.createdDate||auditDate(s,c.id,"complaint.create"),...s.movements.filter(m=>m.sourceId===sh.id&&m.type==="customer_return").map(m=>m.date)),"komplain atau barang kembali");if(d.outcome==="accepted"){if(c.qty>l.qty-l.accepted-l.returned)throw new DomainError("Barang telah kembali atau sudah diselesaikan.");l.accepted+=c.qty;}else if(l.qty-l.accepted-l.returned>0)throw new DomainError("Catat barang yang ditolak kembali ke toko lebih dahulu.");c.resolution=string(d.resolution,"Penyelesaian");c.status="resolved";c.resolvedDate=date;message="Penyelesaian komplain tercatat.";break;
    }
    case "sale.finalize": {
      permit("staf");const sh=get(s.shipments,d.id);chronology(date,latestDate(sh.receivedDate||sh.date,...s.complaints.filter(c=>c.shipmentId===sh.id).map(c=>c.resolvedDate||auditDate(s,c.id,"complaint.resolve"))),"penerimaan atau penyelesaian komplain");if(sh.status!=="received"||!sh.evidence||!sh.receiver)throw new DomainError("Bukti dan konfirmasi penerimaan belum lengkap.");if(s.complaints.some(c=>c.shipmentId===sh.id&&c.status==="open"))throw new DomainError("Selesaikan komplain sebelum finalisasi.");const lines=s.shipmentLines.filter(l=>l.shipmentId===sh.id&&!l.finalized);if(!lines.length)throw new DomainError("Pengiriman sudah difinalisasi.",409);const revenue=sum(lines.map(l=>l.accepted*l.price)),cost=sum(lines.map(l=>l.accepted*l.cost));for(const l of lines){l.finalized=true;l.finalizedDate=date;}if(revenue||cost)journal(s,date,sh.id,`Finalisasi ${sh.number}`,[["unbilled",revenue,0],["sales",0,revenue],["cogs",cost,0],["inventory",0,cost]]);message="Transaksi final dan siap masuk tagihan.";break;
    }
    case "invoice.issue": {
      permit("penagihan");const buyer=selectedBuyer(s,actor,d);const id=uid();target=id;const taxBps=integer(d.taxBps??0,"Pajak simulasi (basis poin)",0,10000);const selected=rows(d.lines);if(new Set(selected.map(x=>x.shipmentLineId)).size!==selected.length)throw new DomainError("Baris tagihan duplikat.");
      for(const item of selected){const l=get(s.shipmentLines,item.shipmentLineId),sh=get(s.shipments,l.shipmentId),o=get(s.orders,sh.orderId);chronology(date,l.finalizedDate||sh.receivedDate||sh.date,"finalisasi penjualan");if(!sameBuyer(o,buyer)||!l.finalized||l.accepted<=0)throw new DomainError("Transaksi belum siap ditagih untuk pembeli ini.");if(s.invoiceLines.some(x=>x.shipmentLineId===l.id))throw new DomainError("Transaksi sudah ditagih.",409);const subtotal=l.accepted*l.price;s.invoiceLines.push({id:uid(),invoiceId:id,shipmentLineId:l.id,qty:l.accepted,price:l.price,subtotal,tax:Math.round(subtotal*taxBps/10000)});}
      const dueDate=dateValue(d.dueDate);if(dueDate<date)throw new DomainError("Jatuh tempo tidak boleh sebelum tanggal invoice.");s.invoices.push({id,number:numbered(s,"INV",date,s.invoices.length),...buyer,date,dueDate,status:"issued",taxBps,taxNote:taxBps?"Perhitungan pajak simulasi; bukan faktur pajak resmi.":"Pajak demo nonaktif."});const sub=sum(s.invoiceLines.filter(x=>x.invoiceId===id).map(l=>l.subtotal)),tax=sum(s.invoiceLines.filter(x=>x.invoiceId===id).map(l=>l.tax));journal(s,date,id,"Penerbitan invoice",[["receivable",sub+tax,0],["unbilled",0,sub],["tax",0,tax]]);message="Invoice diterbitkan dari jumlah penerimaan final.";break;
    }
    case "payment.record": {
      permit("pic","penagihan","customer");const buyer=selectedBuyer(s,actor,d,true);let invoiceId:string|undefined;
      if(actor.role==="customer"||d.invoiceId){const invoice=get(s.invoices,string(d.invoiceId,"Invoice yang sudah terbit"));assertBuyerAccess(actor,invoice);if(!sameBuyer(invoice,buyer))throw new DomainError("Invoice harus milik pembayar yang sama.",403);if(invoice.status!=="issued")throw new DomainError("Tagihan belum diterbitkan.");chronology(date,invoice.date,"penerbitan invoice");if(actor.role==="customer"&&invoiceBalance(s,invoice.id)<=0)throw new DomainError("Tagihan ini sudah lunas.",409);invoiceId=invoice.id;}
      const id=uid();target=id;s.payments.push({id,...buyer,...(invoiceId?{invoiceId}:{}),amount:integer(d.amount,"Nilai transfer"),date,reference:string(d.reference||"","Referensi",false),payer:string(d.payer||actor.name,"Pembayar"),note:string(d.note||"","Catatan",false),status:"recorded",verifiedBy:null,evidence:string(d.evidence||"","Bukti",false),createdBy:actor.id});message="Transfer simulasi dicatat. Belum mengurangi piutang.";break;
    }
    case "payment.verify": {
      permit("penagihan");const p=get(s.payments,d.id);chronology(date,p.date,"pencatatan dana");if(p.status!=="recorded")throw new DomainError("Dana telah diverifikasi atau ditolak.",409);if(d.divisionId||d.customerId){const buyer=selectedBuyer(s,actor,d);if(buyerKey(p)&&!sameBuyer(p,buyer))throw new DomainError("Pembeli pembayar tidak dapat diganti saat verifikasi.");Object.assign(p,buyer);for(const a of s.attachments.filter(a=>a.scope==="payment"&&a.targetId===p.id))Object.assign(a,buyer);}p.status="verified";p.verifiedBy=actor.id;p.verifiedDate=date;journal(s,date,p.id,"Dana masuk terverifikasi",[["cash",p.amount,0],["unallocated",0,p.amount]]);message=buyerKey(p)?"Dana terverifikasi dan dapat dialokasikan.":"Dana terverifikasi, identitas masih perlu ditelusuri.";break;
    }
    case "payment.reject": {
      permit("penagihan");const p=get(s.payments,d.id);chronology(date,p.date,"pencatatan dana");if(p.status!=="recorded"||p.kind==="credit")throw new DomainError("Hanya pembayaran yang menunggu verifikasi dapat ditolak.",409);
      p.rejectionReason=string(d.reason,"Alasan penolakan");p.rejectedBy=actor.id;p.rejectedDate=date;p.status="rejected";message=`Pembayaran ditolak: ${p.rejectionReason}. Pembayar dapat membuat catatan baru yang benar.`;break;
    }
    case "payment.identify": {permit("penagihan");const p=get(s.payments,d.id);if(p.status==="rejected")throw new DomainError("Pembayaran ditolak; buat catatan pembayaran baru.",409);if(buyerKey(p)||p.kind==="credit")throw new DomainError("Pembeli dana sudah ditetapkan; identitas tidak dapat dipindahkan.");if(s.allocations.some(a=>a.paymentId===p.id))throw new DomainError("Identitas dana teralokasi tidak dapat diganti.");const buyer=selectedBuyer(s,actor,d);Object.assign(p,buyer);for(const a of s.attachments.filter(a=>a.scope==="payment"&&a.targetId===p.id))Object.assign(a,buyer);p.note+=`\nIdentifikasi: ${string(d.reason,"Hasil penelusuran")}`;message="Pembeli pembayar berhasil diidentifikasi.";break;}
    case "payment.allocate": {
      permit("penagihan");const p=get(s.payments,d.paymentId);chronology(date,p.verifiedDate||p.date,"verifikasi dana");if(p.status!=="verified"||!buyerKey(p))throw new DomainError("Verifikasi dan identifikasi dana lebih dahulu.");const id=uid();target=p.id;const lines=rows(d.lines);let total=0;
      for(const item of lines){const invoice=get(s.invoices,item.invoiceId);chronology(date,invoice.date,"penerbitan invoice");for(const old of s.credits.filter(c=>c.invoiceId===invoice.id&&c.status==="approved"))chronology(date,old.date,"nota kredit invoice");for(const old of s.allocations.filter(a=>a.invoiceId===invoice.id||a.paymentId===p.id))chronology(date,old.date,"alokasi sebelumnya");for(const old of s.refunds.filter(r=>r.paymentId===p.id))chronology(date,old.date,"pengembalian dana");if(!sameBuyer(invoice,p))throw new DomainError("Invoice harus milik divisi atau pelanggan pembayar yang sama.");const amount=integer(item.amount,"Nilai alokasi");if(amount>invoiceBalance(s,invoice.id))throw new DomainError("Alokasi melebihi sisa invoice.",409);if(amount>paymentAvailable(s,p.id))throw new DomainError("Dana yang tersedia tidak cukup.",409);s.allocations.push({id:uid(),paymentId:p.id,invoiceId:invoice.id,amount,date});total+=amount;}
      journal(s,date,id,"Alokasi pembayaran divisi",[["unallocated",total,0],["receivable",0,total]]);message="Pembayaran dialokasikan. Piutang diperbarui.";break;
    }
    case "credit.request": {permit("penagihan");const i=get(s.invoices,d.invoiceId);chronology(date,i.date,"invoice");const amount=integer(d.amount,"Nilai koreksi");if(amount>invoiceTotal(s,i.id)-sum(s.credits.filter(c=>c.invoiceId===i.id&&c.status==="approved").map(c=>c.amount)))throw new DomainError("Koreksi melebihi nilai invoice yang belum dikreditkan.");const id=uid();target=id;s.credits.push({id,invoiceId:i.id,amount,date,reason:string(d.reason,"Alasan koreksi"),status:"requested",requestedBy:actor.id,approvedBy:null});message="Nota kredit diajukan kepada Pimpinan Unit.";break;}
    case "credit.reject": {
      permit("pimpinan");const c=get(s.credits,d.id);independent(c.requestedBy);chronology(date,c.date,"pengajuan koreksi");if(c.status!=="requested")throw new DomainError("Koreksi sudah diproses.",409);
      c.rejectionReason=string(d.reason,"Alasan penolakan");c.rejectedBy=actor.id;c.rejectedDate=date;c.status="rejected";message=`Nota kredit ditolak: ${c.rejectionReason}. Nilai invoice tetap.`;break;
    }
    case "credit.approve": {
      permit("pimpinan");const c=get(s.credits,d.id);independent(c.requestedBy);chronology(date,c.date,"pengajuan koreksi");if(c.status!=="requested")throw new DomainError("Koreksi sudah diproses.",409);const i=get(s.invoices,c.invoiceId);for(const a of s.allocations.filter(a=>a.invoiceId===i.id))chronology(date,a.date,"alokasi pembayaran invoice");for(const old of s.credits.filter(x=>x.invoiceId===i.id&&x.status==="approved"))chronology(date,old.date,"nota kredit sebelumnya");const previous=s.credits.filter(x=>x.invoiceId===i.id&&x.status==="approved"),lines=s.invoiceLines.filter(l=>l.invoiceId===i.id),total=invoiceTotal(s,i.id),remaining=total-sum(previous.map(x=>x.amount));if(c.amount>remaining)throw new DomainError("Nilai invoice telah dikoreksi. Tinjau kembali.");const taxRemaining=sum(lines.map(l=>l.tax))-sum(previous.map(x=>x.tax||0)),netRemaining=sum(lines.map(l=>l.subtotal))-sum(previous.map(x=>x.net??x.amount));let tax=c.amount===remaining?taxRemaining:Math.min(taxRemaining,Math.round(c.amount*sum(lines.map(l=>l.tax))/total));if(c.amount-tax>netRemaining)tax=c.amount-netRemaining;c.tax=tax;c.net=c.amount-tax;const reduction=Math.min(c.amount,invoiceBalance(s,i.id)),excess=c.amount-reduction;c.status="approved";c.approvedBy=actor.id;c.date=date;
      if(excess)s.payments.push({id:uid(),...buyerFields(i),amount:excess,date,verifiedDate:date,kind:"credit",sourceCreditId:c.id,reference:`KREDIT-${i.number}`,payer:"Saldo nota kredit",note:c.reason,status:"verified",verifiedBy:actor.id,evidence:"",createdBy:actor.id});
      journal(s,date,c.id,"Nota kredit penjualan",[["sales",c.net,0],["tax",c.tax,0],["receivable",0,reduction],["unallocated",0,excess]]);message="Nota kredit disetujui; piutang dan saldo divisi diperbarui.";break;
    }
    case "refund.request": {permit("penagihan");const p=get(s.payments,d.paymentId);chronology(date,p.verifiedDate||p.date,"verifikasi dana");if(!buyerKey(p))throw new DomainError("Identifikasi pembeli sebelum pengembalian dana.");for(const old of s.allocations.filter(a=>a.paymentId===p.id))chronology(date,old.date,"alokasi dana");const amount=integer(d.amount,"Jumlah pengembalian");if(amount>paymentAvailable(s,p.id))throw new DomainError("Saldo dana belum dialokasikan tidak cukup.");const id=uid();s.refunds.push({id,paymentId:p.id,amount,date,reason:string(d.reason,"Konfirmasi pembeli dan alasan"),status:"requested",requestedBy:actor.id,approvedBy:null});target=id;message="Pengembalian diajukan untuk persetujuan.";break;}
    case "refund.reject": {
      permit("pimpinan");const r=get(s.refunds,d.id);independent(r.requestedBy);chronology(date,r.date,"pengajuan pengembalian");if(r.status!=="requested")throw new DomainError("Pengembalian sudah diputuskan.",409);
      r.rejectionReason=string(d.reason,"Alasan penolakan");r.rejectedBy=actor.id;r.rejectedDate=date;r.status="rejected";message=`Pengembalian saldo ditolak: ${r.rejectionReason}. Saldo tidak dicadangkan.`;break;
    }
    case "refund.approve": {permit("pimpinan");const r=get(s.refunds,d.id);independent(r.requestedBy);chronology(date,r.date,"pengajuan pengembalian");if(r.status!=="requested")throw new DomainError("Pengembalian sudah diputuskan.");if(r.amount>paymentAvailable(s,r.paymentId))throw new DomainError("Dana telah dipakai. Tinjau permintaan kembali.");r.status="approved";r.date=date;r.approvedBy=actor.id;message="Pengembalian disetujui.";break;}
    case "refund.pay": {permit("penagihan");const r=get(s.refunds,d.id);chronology(date,r.date,"pengajuan pengembalian");if(r.status!=="approved")throw new DomainError("Pengembalian belum disetujui atau sudah dibayar.");r.status="paid";r.date=date;journal(s,date,r.id,"Pengembalian dana simulasi",[["unallocated",r.amount,0],["cash",0,r.amount]]);message="Pengembalian dana simulasi dicatat.";break;}
    case "shipment.cancel": {
      permit("staf");const sh=get(s.shipments,d.id);if(sh.status!=="ready")throw new DomainError("Hanya Surat Jalan yang belum dikirim dapat dibatalkan.");chronology(date,sh.date,"pembuatan Surat Jalan");sh.cancelledDate=date;sh.status="cancelled";sh.note+=`\nDibatalkan: ${string(d.reason,"Alasan")}`;message="Surat Jalan dibatalkan. Reservasi tetap tersedia untuk penyiapan ulang.";break;
    }
    case "purchase.create": {
      permit("kepala","staf");const kind=string(d.kind);if(!["OMI reguler","DDO","Smart","Pasar Kering"].includes(kind))throw new DomainError("Jenis pengadaan tidak valid.");if(actor.role==="staf"&&!["OMI reguler","DDO"].includes(kind))throw new DomainError("Pengadaan Smart dan Pasar Kering diputuskan Kepala Toko.",403);const supplier=get(s.suppliers,d.supplierId),orderId=d.orderId?get(s.orders,d.orderId).id:null,id=uid();if(orderId)chronology(date,orderCreatedDate(s,get(s.orders,orderId)),"pesanan divisi sumber");target=id;
      s.purchases.push({id,number:numbered(s,"PB",date,s.purchases.length),supplierId:supplier.id,kind:kind as "DDO",orderId,date,status:"draft",note:string(d.note||"","Catatan",false),fundingStatus:"none",fundingAmount:0});
      const inputs=rows(d.lines);if(new Set(inputs.map(x=>x.productId)).size!==inputs.length)throw new DomainError("Gabungkan produk yang sama dalam satu baris pengadaan.");for(const item of inputs){const p=get(s.products,item.productId);s.purchaseLines.push({id:uid(),purchaseId:id,productId:p.id,qty:integer(item.qty),received:0,cost:integer(item.cost,"Harga beli")});}message="Pengadaan dibuat dan ditautkan dengan kebutuhan stok.";break;
    }
    case "purchase.confirm": {
      const p=get(s.purchases,d.id);permit(...(["OMI reguler","DDO"].includes(p.kind)?["staf","kepala"] as Role[]:["kepala"] as Role[]));chronology(date,p.date,"pengajuan pembelian");if(p.status!=="draft")throw new DomainError("Pengadaan sudah dikonfirmasi.");if(p.kind==="Pasar Kering"&&p.fundingStatus!=="disbursed")throw new DomainError("Dana Pasar Kering harus disetujui dan dicairkan secara simulasi lebih dahulu.");if(p.kind==="Pasar Kering")chronology(date,purchaseStageDate(s,p,"fundingDisbursedDate","funding.disburse"),"ketersediaan dana");p.confirmedDate=date;p.status="ordered";message="Pesanan pemasok dikonfirmasi. Menunggu penerimaan barang.";break;
    }
    case "purchase.close": {
      permit("kepala");const p=get(s.purchases,d.id);
      if(!["draft","ordered"].includes(p.status))throw new DomainError("Pengadaan sudah lengkap atau ditutup.",409);
      chronology(date,latestDate(p.date,p.confirmedDate||auditDate(s,p.id,"purchase.confirm"),p.fundingRequestedDate||auditDate(s,p.id,"funding.request"),p.fundingApprovedDate||auditDate(s,p.id,"funding.approve"),p.fundingDisbursedDate||auditDate(s,p.id,"funding.disburse"),...s.supplierPayments.filter(pay=>pay.purchaseId===p.id).map(pay=>pay.date),...s.movements.filter(m=>m.sourceId===p.id&&m.type==="receipt").map(m=>m.date)),"aktivitas pengadaan terakhir");
      const reason=string(d.reason,"Alasan penutupan"),lines=s.purchaseLines.filter(l=>l.purchaseId===p.id),viewed=rows(d.lines);
      if(viewed.length!==lines.length||new Set(viewed.map(l=>l.purchaseLineId)).size!==lines.length)throw new DomainError("Tinjau seluruh baris pengadaan sebelum menutup.",409);
      for(const line of lines){const prior=viewed.find(l=>l.purchaseLineId===line.id);if(!prior||integer(prior.received,"Jumlah diterima yang ditinjau",0)!==line.received||integer(prior.cancelled,"Jumlah dibatalkan yang ditinjau",0)!==(line.cancelled??0))throw new DomainError("Penerimaan pengadaan telah berubah. Muat ulang sebelum menutup.",409);}
      if(!lines.some(l=>l.qty-l.received-(l.cancelled??0)>0))throw new DomainError("Tidak ada sisa pengadaan untuk ditutup.",409);
      const payments=s.supplierPayments.filter(pay=>pay.purchaseId===p.id),receivedValue=sum(lines.map(l=>l.received*l.cost));
      if(sum(payments.map(pay=>pay.amount))>receivedValue||payments.some(pay=>pay.amount>pay.applied))throw new DomainError("Masih ada uang muka pemasok yang belum dipakai. Selesaikan penerimaan atau rekonsiliasi uang muka sebelum menutup; pengembalian uang muka belum tersedia.",409);
      for(const line of lines)line.cancelled=line.qty-line.received;
      p.status="closed";p.closedDate=date;p.closedBy=actor.id;p.closureReason=reason;
      message=`Sisa pengadaan ditutup: ${reason}. Penerimaan, pembayaran, dan alokasi dana internal tetap tercatat.`;break;
    }
    case "funding.request": {permit("kepala");const p=get(s.purchases,d.id);if(p.kind!=="Pasar Kering"||p.status!=="draft"||p.fundingStatus!=="none")throw new DomainError("Pengadaan tidak dapat diajukan dananya.");chronology(date,p.date,"pengajuan pembelian");p.fundingAmount=integer(d.amount,"Kebutuhan dana");if(p.fundingAmount<sum(s.purchaseLines.filter(l=>l.purchaseId===p.id).map(l=>(l.qty-(l.cancelled??0))*l.cost)))throw new DomainError("Dana kurang dari nilai pembelian.");p.fundingRequestedBy=actor.id;p.fundingRequestedDate=date;p.fundingStatus="requested";message="Dana Pasar Kering diajukan kepada Koperasi.";break;}
    case "funding.approve": {permit("pimpinan");const p=get(s.purchases,d.id);independent(p.fundingRequestedBy);if(p.status!=="draft"||p.fundingStatus!=="requested")throw new DomainError("Pengajuan dana belum tersedia atau pengadaan telah ditutup.");chronology(date,purchaseStageDate(s,p,"fundingRequestedDate","funding.request"),"pengajuan dana");p.fundingApprovedDate=date;p.fundingStatus="approved";message="Pengajuan dana pembelian disetujui.";break;}
    case "funding.disburse": {permit("penagihan");const p=get(s.purchases,d.id);if(p.status!=="draft"||p.fundingStatus!=="approved")throw new DomainError("Dana belum disetujui, sudah dicairkan, atau pengadaan telah ditutup.");chronology(date,purchaseStageDate(s,p,"fundingApprovedDate","funding.approve"),"persetujuan dana");p.fundingDisbursedDate=date;p.fundingStatus="disbursed";p.note+="\nAlokasi dana internal simulasi telah tersedia.";message="Ketersediaan dana internal dicatat. Pembelian dapat dilanjutkan.";break;}
    case "purchase.receive": {
      permit("staf");const p=get(s.purchases,d.id);chronology(date,purchaseStageDate(s,p,"confirmedDate","purchase.confirm"),"konfirmasi pembelian");for(const m of s.movements.filter(m=>m.sourceId===p.id&&m.type==="receipt"))chronology(date,m.date,"penerimaan pemasok sebelumnya");if(p.status!=="ordered")throw new DomainError("Pengadaan belum dipesan atau telah lengkap.");for(const pay of s.supplierPayments.filter(x=>x.purchaseId===p.id))chronology(date,pay.date,"pembayaran pemasok sebelumnya");const inputs=rows(d.lines);if(new Set(inputs.map(x=>x.purchaseLineId)).size!==inputs.length)throw new DomainError("Baris penerimaan duplikat.");let value=0;
      for(const item of inputs){const l=get(s.purchaseLines,item.purchaseLineId);if(l.purchaseId!==p.id)throw new DomainError("Baris berasal dari pengadaan lain.");const qty=integer(item.qty);if(qty>l.qty-l.received-(l.cancelled??0))throw new DomainError("Penerimaan melebihi sisa jumlah dipesan.");const expiry=item.expiry?dateValue(item.expiry):"";if(expiry&&expiry<=date)throw new DomainError("Barang kedaluwarsa tidak diterima sebagai stok layak jual.");l.received+=qty;const batchId=uid();s.batches.push({id:batchId,productId:l.productId,supplierId:p.supplierId,purchaseLineId:l.id,code:string(item.code||`LOT-${s.batches.length+1}`),qty,held:0,cost:l.cost,expiry,location:string(item.location||"Gudang")});move(batchId,qty,"receipt",p.id);value+=qty*l.cost;}
      journal(s,date,p.id,`Penerimaan ${p.number}`,[["inventory",value,0],["payable",0,value]]);
      let offset=value;for(const pay of s.supplierPayments.filter(x=>x.purchaseId===p.id&&x.amount>x.applied)){const amount=Math.min(offset,pay.amount-pay.applied);if(amount){pay.applied+=amount;offset-=amount;journal(s,date,pay.id,"Pemakaian uang muka pemasok",[["payable",amount,0],["vendorAdvance",0,amount]]);}if(!offset)break;}
      if(s.purchaseLines.filter(l=>l.purchaseId===p.id).every(l=>l.received+(l.cancelled??0)===l.qty))p.status="complete";message="Penerimaan aktual menambah stok. Sisa pesanan pemasok tetap tercatat.";break;
    }
    case "supplier.pay": {
      const p=get(s.purchases,d.purchaseId);if(p.closedDate)chronology(date,p.closedDate,"penutupan pengadaan");permit(...(["OMI reguler","DDO"].includes(p.kind)?["staf"] as Role[]:["kepala"] as Role[]));chronology(date,purchaseStageDate(s,p,"confirmedDate","purchase.confirm"),"konfirmasi pembelian");for(const pay of s.supplierPayments.filter(x=>x.purchaseId===p.id))chronology(date,pay.date,"pembayaran pemasok sebelumnya");if(p.status==="draft")throw new DomainError("Konfirmasikan pembelian lebih dahulu.");for(const m of s.movements.filter(m=>m.sourceId===p.id&&m.type==="receipt"))chronology(date,m.date,"penerimaan pemasok sebelumnya");const lines=s.purchaseLines.filter(l=>l.purchaseId===p.id),previous=s.supplierPayments.filter(x=>x.purchaseId===p.id),amount=integer(d.amount,"Pembayaran pemasok"),total=sum(lines.map(l=>(l.qty-(l.cancelled??0))*l.cost));if(amount>total-sum(previous.map(x=>x.amount)))throw new DomainError("Pembayaran melebihi nilai pembelian tersisa.");const due=sum(lines.map(l=>l.received*l.cost))-sum(previous.map(x=>x.applied)),applied=Math.min(amount,Math.max(0,due)),id=uid();s.supplierPayments.push({id,purchaseId:p.id,amount,date,reference:string(d.reference,"Referensi invoice / SPH"),applied});target=id;journal(s,date,id,"Pembayaran pemasok simulasi",[["payable",applied,0],["vendorAdvance",amount-applied,0],["cash",0,amount]]);message="Pembayaran/uang muka pemasok simulasi dicatat.";break;
    }
    case "stock.returnCreate": {
      permit("staf");const b=get(s.batches,d.batchId),qty=integer(d.qty);batchDate(b.id);const reserved=sum(s.reservations.filter(r=>r.batchId===b.id).map(r=>r.qty));if(d.fromHeld===true){const tracked=sum(s.stockReturns.filter(r=>r.batchId===b.id&&(r.status==="quarantined"||(r.status==="rejected"&&!r.released))).map(r=>r.qty));if(qty>b.held-tracked)throw new DomainError("Stok karantina tanpa proses retur tidak cukup.");}else{if(qty>b.qty-b.held-reserved)throw new DomainError("Barang telah dicadangkan atau ditahan.");b.held+=qty;}const id=uid();s.stockReturns.push({id,batchId:b.id,qty,date,reason:string(d.reason,"Alasan retur"),status:"quarantined",note:""});target=id;message="Barang dipisahkan dari stok layak jual dan masuk antrean retur.";break;
    }
    case "stock.returnResolve": {
      permit("staf");const r=get(s.stockReturns,d.id);chronology(date,r.date,"pengajuan retur");if(r.status!=="quarantined")throw new DomainError("Hasil retur sudah dicatat.");const b=get(s.batches,r.batchId);r.note=string(d.note,"Hasil pemeriksaan pemasok");r.status=d.accept===true?"accepted":"rejected";r.date=date;
      if(r.status==="accepted"){b.qty-=r.qty;b.held-=r.qty;move(b.id,-r.qty,"supplier_return",r.id);journal(s,date,r.id,"Retur diterima pemasok; klaim belum diselesaikan",[["supplierReceivable",r.qty*b.cost,0],["inventory",0,r.qty*b.cost]]);}message=r.status==="accepted"?"Retur diterima dan klaim pemasok dicatat.":"Retur ditolak. Barang tetap ditahan hingga ditinjau.";break;
    }
    case "stock.holdRelease": {
      permit("kepala");const r=get(s.stockReturns,d.id),b=get(s.batches,r.batchId);chronology(date,r.date,"keputusan retur");batchDate(b.id);if(r.status!=="rejected"||r.released)throw new DomainError("Barang retur tidak dapat dilepas lagi.");if(b.expiry&&b.expiry<=date)throw new DomainError("Barang kedaluwarsa tetap dilarang dijual.");r.note+=`\nLayak jual: ${string(d.reason,"Hasil penilaian kelayakan")}`;b.held-=r.qty;r.released=r.qty;message="Kelayakan ditinjau Kepala Toko; stok dapat digunakan kembali.";break;
    }
    case "stocktake.create": {
      permit("staf");const b=get(s.batches,d.batchId),id=uid();batchDate(b.id);
      const reason=string(d.reason,"Hasil pemeriksaan ulang"),counted=integer(d.counted,"Hasil hitung",0);
      const previous=d.replacesId?get(s.stocktakes,d.replacesId):undefined;
      if(previous){
        if(previous.batchId!==b.id)throw new DomainError("Penghitungan pengganti harus untuk batch yang sama.");
        if(!["requested","stale"].includes(stocktakeStatus(s,previous)))throw new DomainError("Opname sebelumnya telah diproses.",409);
        chronology(date,latestDate(previous.date,previous.invalidatedDate),"penghitungan sebelumnya atau perubahan stok");
        previous.status="superseded";previous.supersededBy=id;previous.resolutionReason=`Digantikan penghitungan ulang: ${reason}`;previous.resolvedBy=actor.id;
      }
      s.stocktakes.push({id,batchId:b.id,expected:b.qty,requestedBy:actor.id,counted,reason,date,status:"requested",approvedBy:null,movementCount:s.movements.filter(m=>m.batchId===b.id).length,...(previous?{replacesId:previous.id}:{})});
      target=id;message="Hasil stok opname diajukan untuk persetujuan.";break;
    }
    case "stocktake.cancel": {
      permit("staf","kepala");const st=get(s.stocktakes,d.id);
      if(actor.role==="staf"&&st.requestedBy!==actor.id)throw new DomainError("Hanya pemilik pengajuan atau Kepala Toko yang dapat membatalkan opname.",403);
      if(!["requested","stale"].includes(stocktakeStatus(s,st)))throw new DomainError("Opname telah diproses.",409);
      chronology(date,latestDate(st.date,st.invalidatedDate),"penghitungan atau perubahan stok");
      st.status="cancelled";st.resolutionReason=string(d.reason,"Alasan pembatalan");st.resolvedBy=actor.id;message="Pengajuan opname dibatalkan; riwayat tetap tersimpan.";break;
    }
    case "stocktake.approve": {
      permit("kepala");const st=get(s.stocktakes,d.id),b=get(s.batches,st.batchId);independent(st.requestedBy);chronology(date,st.date,"penghitungan stok");if(stocktakeStatus(s,st)!=="requested")throw new DomainError("Stok telah berubah atau opname sudah diproses. Hitung ulang sebelum menyesuaikan.",409);const delta=st.counted-b.qty;b.qty=st.counted;st.status="approved";st.approvedBy=actor.id;move(b.id,delta,"stocktake",st.id,st.reason);const value=Math.abs(delta)*b.cost;if(value)journal(s,date,st.id,"Penyesuaian stok opname",delta>0?[["inventory",value,0],["adjustment",0,value]]:[["expense",value,0],["inventory",0,value]]);message="Penyesuaian stok disetujui dan jurnal dicatat.";break;
    }
    case "expense.create": {
      permit("staf","kurir","kepala");const shipmentId=d.shipmentId?get(s.shipments,d.shipmentId).id:null,purchaseId=d.purchaseId?get(s.purchases,d.purchaseId).id:null;if(actor.role==="kurir"&&(!shipmentId||get(s.shipments,shipmentId).courierId!==actor.id))throw new DomainError("Biaya harus terkait tugas pengiriman Anda.",403);if(!shipmentId&&!purchaseId)throw new DomainError("Tautkan biaya pada pengiriman atau pembelian.");const id=uid();s.expenses.push({id,shipmentId,purchaseId,amount:integer(d.amount,"Nilai biaya"),date,category:string(d.category,"Jenis biaya"),description:string(d.description,"Keterangan"),status:"requested",createdBy:actor.id,approvedBy:null,evidence:string(d.evidence||"","Bukti",false)});target=id;message="Biaya diajukan untuk reimburse simulasi.";break;
    }
    case "expense.approve": {permit("pimpinan");const e=get(s.expenses,d.id);independent(e.createdBy);chronology(date,e.date,"pengajuan biaya");if(e.status!=="requested")throw new DomainError("Biaya telah diproses.");e.status="approved";e.date=date;e.approvedBy=actor.id;message="Biaya disetujui untuk penggantian.";break;}
    case "expense.reject": {
      permit("pimpinan");const e=get(s.expenses,d.id);independent(e.createdBy);chronology(date,e.date,"pengajuan biaya");if(e.status!=="requested")throw new DomainError("Biaya telah diproses.",409);
      e.rejectionReason=string(d.reason,"Alasan penolakan");e.rejectedBy=actor.id;e.rejectedDate=date;e.status="rejected";message=`Pengajuan biaya ditolak: ${e.rejectionReason}. Pengaju dapat mengajukan rincian yang benar.`;break;
    }
    case "expense.pay": {permit("penagihan");const e=get(s.expenses,d.id);chronology(date,e.date,"biaya");if(e.status!=="approved")throw new DomainError("Biaya belum disetujui atau sudah dibayar.");e.status="paid";journal(s,date,e.id,"Reimburse simulasi",[["expense",e.amount,0],["cash",0,e.amount]]);message="Penggantian biaya simulasi dicatat.";break;}
    case "product.update": {permit("kepala");const p=get(s.products,d.id);p.price=integer(d.price,"Harga jual");p.minimum=integer(d.minimum,"Minimum stok",0);p.returnMonths=integer(d.returnMonths,"Batas retur (bulan)",0,36);if(d.active!==undefined){if(typeof d.active!=="boolean")throw new DomainError("Status penjualan barang tidak valid.");p.active=d.active;}message="Pengaturan produk disimpan. Pesanan dan harga transaksi sebelumnya tetap tersimpan.";break;}
    case "attachment.add": {
      const scope=string(d.scope),targetId=string(d.targetId),context=attachmentContext(s,actor,scope,targetId),id=string(d.attachmentId);if(s.attachments.some(a=>a.id===id))throw new DomainError("Lampiran sudah ada.",409);s.attachments.push({id,ownerId:actor.id,...context,targetId,scope,name:string(d.name,"Nama file",true,200),mime:string(d.mime),size:integer(d.size,"Ukuran berkas",1,5242880),createdAt:new Date().toISOString()});if(scope==="payment")get(s.payments,targetId).evidence=`Berkas bukti: ${string(d.name)}`;if(scope==="receipt")get(s.shipments,targetId).evidence=`Berkas bukti: ${string(d.name)}`;if(scope==="expense")get(s.expenses,targetId).evidence=`Berkas bukti: ${string(d.name)}`;target=id;message="Lampiran tersimpan.";break;
    }
    case "master.product": {
      permit("kepala");const id=d.id?get(s.products,d.id).id:uid(),sku=string(d.sku,"SKU",true,50),category=string(d.category);if(!["OMI","Smart"].includes(category))throw new DomainError("Kategori barang tidak valid.");if(s.products.some(p=>p.sku===sku&&p.id!==id))throw new DomainError("SKU sudah digunakan.");const record={id,sku,name:string(d.name,"Nama barang",true,150),category:category as "OMI"|"Smart",unit:string(d.unit,"Satuan",true,30),price:integer(d.price,"Harga jual"),minimum:integer(d.minimum??0,"Stok minimum",0),returnMonths:integer(d.returnMonths??1,"Batas retur",0,36),active:d.active!==false};const current=s.products.find(p=>p.id===id);if(current){if(s.orderLines.some(l=>l.productId===id)||s.batches.some(b=>b.productId===id))throw new DomainError("Identitas barang yang sudah digunakan harus dipertahankan. Gunakan pengaturan harga dan stok minimum.");Object.assign(current,record);}else s.products.push(record);target=id;message="Data barang tersimpan.";break;
    }
    case "admin.division": {permit("admin");const code=string(d.code,"Kode",true,20);if(s.divisions.some(x=>x.code===code&&x.id!==d.id))throw new DomainError("Kode divisi sudah digunakan.");const record={id:d.id?get(s.divisions,d.id).id:uid(),name:string(d.name,"Nama divisi",true,150),code,address:string(d.address,"Alamat")},current=s.divisions.find(x=>x.id===record.id);if(current)Object.assign(current,record);else s.divisions.push(record);target=record.id;message="Data divisi tersimpan.";break;}
    case "admin.supplier": {permit("admin");const record={id:d.id?get(s.suppliers,d.id).id:uid(),name:string(d.name,"Nama pemasok",true,150),category:string(d.category,"Kategori",true,50)},current=s.suppliers.find(x=>x.id===record.id);if(current)Object.assign(current,record);else s.suppliers.push(record);target=record.id;message="Data pemasok simulasi tersimpan.";break;}
    case "admin.user": {
      permit("admin");const u=get(s.users,d.id),role=string(d.role);if(!isRole(role))throw new DomainError("Peran tidak valid.");if((u.role==="customer")!==(role==="customer"))throw new DomainError("Akun pelanggan dan petugas harus tetap terpisah. Gunakan akun yang sesuai; jenis akun tidak dapat dipindahkan.",403);const divisionId=role==="pic"?get(s.divisions,d.divisionId).id:null;if(u.id===actor.id&&(role!=="admin"||d.active===false))throw new DomainError("Akun administrator yang sedang digunakan tidak dapat dinonaktifkan atau diganti perannya.");u.role=role;u.divisionId=divisionId;u.active=d.active!==false;message="Hak akses akun diperbarui.";break;
    }
    case "period.submit": {permit("penagihan","laporan");const first=dateValue(string(d.month)+"-01");open(s,first);const p=period(s,first);p.status="review";p.submittedBy=actor.id;p.approvedRevision=null;p.approvedBy=null;target=p.id;message="Laporan diajukan kepada Pimpinan Unit.";break;}
    case "period.return": {
      permit("pimpinan");const p=get(s.periods,d.id);independent(p.submittedBy);if(p.status!=="review"||integer(d.revision,"Versi laporan",0)!==p.revision)throw new DomainError("Versi laporan telah berubah. Muat ulang sebelum meninjau.",409);open(s,p.id+"-01");open(s,date);chronology(date,auditDate(s,p.id,"period.submit"),"pengajuan laporan");
      p.returnReason=string(d.reason,"Catatan perbaikan");p.returnedBy=actor.id;p.returnedDate=date;p.returnedRevision=p.revision;p.revision++;p.status="open";p.approvedRevision=null;p.approvedBy=null;message=`Laporan dikembalikan untuk perbaikan: ${p.returnReason}. Ajukan ulang setelah ditinjau.`;break;
    }
    case "period.approve": {permit("pimpinan");const p=get(s.periods,d.id);independent(p.submittedBy);if(p.status!=="review")throw new DomainError("Laporan belum diajukan atau berubah.");if(d.revision!==undefined&&integer(d.revision,"Versi laporan",0)!==p.revision)throw new DomainError("Versi laporan telah berubah. Muat ulang sebelum menyetujui.",409);open(s,p.id+"-01");p.status="approved";p.approvedRevision=p.revision;p.approvedBy=actor.id;message="Versi laporan ini disetujui.";break;}
    case "period.close": {permit("akuntansi");const p=get(s.periods,d.id);if(p.status!=="approved"||p.approvedRevision!==p.revision)throw new DomainError("Versi laporan belum disetujui atau telah berubah.",409);if(d.revision!==undefined&&integer(d.revision,"Versi laporan",0)!==p.revision)throw new DomainError("Versi laporan telah berubah. Muat ulang sebelum menutup.",409);open(s,p.id+"-01");const report=financialReport(s,p.id+"-31");if(report.debit!==report.credit)throw new DomainError("Jurnal tidak seimbang.");p.status="closed";p.closedBy=actor.id;p.closedAt=new Date().toISOString();p.snapshot=report;message="Periode diposting dan dikunci. Sisa piutang tetap dibawa.";break;}
    default: throw new DomainError("Tindakan belum tersedia.",404);
  }
  if(!command.type.startsWith("period.")&&!command.type.startsWith("admin.")&&!command.type.startsWith("profile."))touch(s,date);
  s.audits.push({id:uid(),actorId:actor.id,action:command.type,targetId:target,at:new Date().toISOString(),date,description:message});s.revision++;
  validateState(s);return {state:s,result:{id:target,message}};
}
export function validateState(s:State){
  const validBuyer=(ref:BuyerRef,optional=false)=>{
    if(!buyerKey(ref)){if(optional&&!ref.divisionId&&!ref.customerId)return;throw new DomainError("Identitas pembeli transaksi tidak valid.",409);}
    if(ref.customerId)get(s.users,ref.customerId);else get(s.divisions,ref.divisionId);
  };
  for(const ref of [...s.orders,...s.invoices,...s.complaints])validBuyer(ref);
  for(const ref of [...s.payments,...s.attachments])validBuyer(ref,true);
  for(const complaint of s.complaints){const sh=get(s.shipments,complaint.shipmentId);if(!sameBuyer(complaint,get(s.orders,sh.orderId)))throw new DomainError("Pemilik komplain berbeda dari pesanan.",409);}
  for(const line of s.invoiceLines){const sh=get(s.shipments,get(s.shipmentLines,line.shipmentLineId).shipmentId);if(!sameBuyer(get(s.invoices,line.invoiceId),get(s.orders,sh.orderId)))throw new DomainError("Invoice menggabungkan pembeli berbeda.",409);}
  for(const payment of s.payments){
    if(payment.invoiceId&&!sameBuyer(payment,get(s.invoices,payment.invoiceId)))throw new DomainError("Pemilik pembayaran berbeda dari invoice.",409);
    if(payment.sourceCreditId){const credit=get(s.credits,payment.sourceCreditId);if(!sameBuyer(payment,get(s.invoices,credit.invoiceId)))throw new DomainError("Pemilik saldo nota kredit tidak sesuai.",409);}
  }
  for(const allocation of s.allocations)if(!sameBuyer(get(s.payments,allocation.paymentId),get(s.invoices,allocation.invoiceId)))throw new DomainError("Alokasi lintas pembeli ditolak.",409);
  for(const b of s.batches){integer(b.qty,"Stok",0);integer(b.held,"Stok ditahan",0,b.qty);if(sum(s.reservations.filter(r=>r.batchId===b.id).map(r=>r.qty))>b.qty-b.held)throw new DomainError("Reservasi melampaui stok layak.",409);}
  for(const p of s.purchases){
    const lines=s.purchaseLines.filter(l=>l.purchaseId===p.id);
    for(const line of lines){integer(line.qty,"Jumlah pengadaan",1);integer(line.received,"Penerimaan pengadaan",0,line.qty);integer(line.cancelled??0,"Sisa dibatalkan",0,line.qty-line.received);}
    const retained=sum(lines.map(l=>(l.qty-(l.cancelled??0))*l.cost)),payments=s.supplierPayments.filter(pay=>pay.purchaseId===p.id);
    if(sum(payments.map(pay=>pay.amount))>retained)throw new DomainError("Pembayaran pemasok melebihi nilai pengadaan setelah pembatalan.",409);
    if(p.status==="closed"&&(lines.some(l=>l.received+(l.cancelled??0)!==l.qty)||payments.some(pay=>pay.amount>pay.applied)))throw new DomainError("Pengadaan tertutup masih memiliki sisa penerimaan atau uang muka.",409);
  }
  for(const l of s.shipmentLines)if(l.accepted+l.returned>l.qty)throw new DomainError("Penerimaan dan barang kembali melebihi kiriman.");
  for(const p of s.payments)if(p.status==="verified"&&paymentAvailable(s,p.id)<0)throw new DomainError("Saldo dana tidak boleh negatif.");
  for(const j of s.journals){const rows=s.journalLines.filter(l=>l.journalId===j.id);if(sum(rows.map(l=>l.debit))!==sum(rows.map(l=>l.credit)))throw new DomainError("Jurnal tidak seimbang.");}
}
