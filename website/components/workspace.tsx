"use client";
import {buyerKey, buyerLabel} from "@/lib/domain/buyers";
import {buyerOptions} from "@/lib/domain/buyer-views";
import {Art} from "./art";
import {BrandMark} from "./brand";
import { Profile } from "./profile";
import { Dashboard } from "./dashboard";
import { Catalog } from "./catalog";
import { InventoryWorkspace } from "./inventory-workspace";
import { InvoiceComposer } from "./invoice-composer";
import { ApiError, logoutSession, requestJson, singleFlight } from "@/lib/client/requests";
import { clearCartForAccount } from "@/lib/client/cart-storage";
import { orderFulfillmentLabel } from "@/lib/domain/order-views";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useCallback,useEffect,useRef,useState } from "react";
import { Orders, OrderDetail } from "./order-experience";
import { PaymentAllocation } from "./payment-allocation";
import { MobileNavigation } from "./mobile-navigation";
import { useWorkspaceNavigation, type QueryPatch } from "./use-workspace-navigation";
import { PAGE_ROLES } from "@/lib/domain/navigation";
import { AlertDialog, AlertDialogContent, AlertDialogHeader, AlertDialogTitle, AlertDialogDescription, AlertDialogFooter, AlertDialogCancel, AlertDialogAction } from "@/components/ui/alert-dialog";
import { Store, LayoutDashboard, ClipboardList, Package, Truck, ReceiptText, WalletCards, BarChart3, ShoppingBag, LogOut, Plus, ChevronRight, ArrowLeft, RefreshCw, Menu, CircleHelp, AlertCircle, Users, CalendarCheck, Boxes } from "lucide-react";
import { Toaster,toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Field,FieldGroup,FieldLabel,FieldSet,FieldLegend } from "@/components/ui/field";
import { Dialog,DialogContent,DialogHeader,DialogTitle,DialogDescription,DialogFooter } from "@/components/ui/dialog";
import type { Actor,Role } from "@/lib/domain/accounts";
import { Procurement,ExpenseCenter,SettlementControls,ShipmentExtras,Reports,Periods,AccountSettings,DocumentLink,Attachments } from "./operations";
import { ROLE_LABELS } from "@/lib/domain/accounts";
import type { State,Order,CommandResult } from "@/lib/domain/model";
import { sum,money,today,get,invoiceTotal,invoiceBalance,invoiceStatus,paymentAvailable } from "@/lib/domain/selectors";

export type Page="dashboard"|"catalog"|"orders"|"deliveries"|"stock"|"billing"|"payments"|"procurement"|"reports"|"periods"|"admin"|"profile";
export type FormField={key:string;label:string;type?:"text"|"number"|"date"|"textarea"|"select";value?:string|number;min?:number;max?:number;options?:{value:string;label:string}[];optional?:boolean;group?:string};
export type ActionSpec={title:string;description:string;type:string;fields:FormField[];data?:Record<string,unknown>;map?:(values:Record<string,string>)=>Record<string,unknown>;label?:string;after?:(r:CommandResult)=>void};
export type WorkspaceContext={refresh:()=>Promise<void>;s:State;actor:Actor;go:(page:Page,id?:string,query?:QueryPatch)=>void;ask:(spec:ActionSpec)=>void};
const NAV:{id:Page;label:string;icon:typeof Store;roles:Role[]}[]=[
 {id:"dashboard",label:"Ringkasan",icon:LayoutDashboard,roles:["pic","kepala","staf","kurir","laporan","penagihan","pimpinan","akuntansi","admin"]},
 {id:"catalog",label:"Katalog barang",icon:ShoppingBag,roles:["pic","kepala"]},
 {id:"orders",label:"Pesanan",icon:ClipboardList,roles:["pic","kepala","staf","laporan"]},
 {id:"deliveries",label:"Pengiriman",icon:Truck,roles:["pic","kepala","staf","kurir"]},
 {id:"stock",label:"Persediaan",icon:Boxes,roles:["kepala","staf","laporan"]},
 {id:"billing",label:"Invoice & piutang",icon:ReceiptText,roles:["pic","kepala","penagihan","pimpinan","akuntansi","laporan"]},
 {id:"payments",label:"Pembayaran",icon:WalletCards,roles:["pic","penagihan","pimpinan","akuntansi"]},
 {id:"procurement",label:"Pengadaan",icon:Package,roles:["kepala","staf","penagihan","pimpinan","akuntansi"]},
 {id:"reports",label:"Laporan",icon:BarChart3,roles:["kepala","laporan","penagihan","pimpinan","akuntansi"]},
 {id:"periods",label:"Tutup periode",icon:CalendarCheck,roles:["laporan","penagihan","pimpinan","akuntansi"]},
 {id:"profile",label:"Profil saya",icon:Users,roles:["pic","kepala","staf","kurir","laporan","penagihan","pimpinan","akuntansi","admin"]},
 {id:"admin",label:"Pengaturan akun",icon:Users,roles:["admin"]}
];

const NAV_GROUPS:{label:string;pages:Page[]}[]=[
 {label:"Ruang kerja",pages:["dashboard"]},
 {label:"Operasional",pages:["catalog","orders","deliveries","stock","procurement"]},
 {label:"Keuangan",pages:["billing","payments","reports","periods"]},
 {label:"Akun",pages:["profile","admin"]}
];

const PAGE_DESCRIPTIONS:Record<Page,string>={
 dashboard:"Tinjau pekerjaan yang menunggu dan aktivitas terbaru.",
 catalog:"Pilih barang dan jumlah untuk pesanan divisi.",
 orders:"Tinjau pesanan, ketersediaan barang, dan progres pemenuhan.",
 deliveries:"Pantau surat jalan dan perkembangan pengiriman.",
 stock:"Periksa stok tersedia, batch, kedaluwarsa, dan retur.",
 billing:"Pantau tagihan, jatuh tempo, dan sisa piutang pembeli.",
 payments:"Tinjau pembayaran, saldo pembeli, dan pengembalian dana.",
 procurement:"Pantau pembelian, penerimaan barang, dan pembayaran pemasok.",
 reports:"Telusuri penjualan dan posisi keuangan berdasarkan periode.",
 periods:"Tinjau status laporan dan penutupan periode pembukuan.",
 admin:"Kelola akun, hak akses, divisi, dan pemasok.",
 profile:"Perbarui informasi pribadi dan pengaturan keamanan akun."
};

const ROLE_PAGE_DESCRIPTIONS:Partial<Record<Role,Partial<Record<Page,string>>>>={
 pic:{
  catalog:"Pilih barang dan jumlah yang dibutuhkan divisi Anda.",
  deliveries:"Pantau kiriman divisi dan konfirmasikan penerimaan barang.",
  payments:"Catat transfer divisi, unggah bukti, dan pantau verifikasi."
 },
 kepala:{procurement:"Kelola pengadaan dan pembayaran Smart atau Pasar Kering."},
 staf:{
  deliveries:"Siapkan surat jalan, catat pengiriman, dan selesaikan penerimaan.",
  procurement:"Buat pengadaan OMI atau DDO, catat penerimaan, dan bayar pemasok."
 },
 kurir:{deliveries:"Lihat tugas pengiriman dan catat bukti pengantaran."},
 laporan:{periods:"Ajukan laporan bulanan untuk persetujuan Pimpinan Unit."},
 penagihan:{
  payments:"Verifikasi dana masuk dan alokasikan pembayaran ke invoice.",
  procurement:"Pantau pengadaan dan siapkan dana pembelian yang disetujui.",
  periods:"Ajukan laporan bulanan untuk persetujuan Pimpinan Unit."
 },
 pimpinan:{
  payments:"Setujui pengajuan koreksi, pengembalian dana, dan biaya operasional.",
  procurement:"Tinjau pengadaan dan setujui kebutuhan dana pembelian.",
  periods:"Tinjau dan setujui laporan bulanan sebelum penutupan."
 },
 akuntansi:{periods:"Tutup periode setelah laporan bulanan disetujui."}
};

function shortDate(date:string){return new Date(date.length===10?date+"T12:00:00":date).toLocaleDateString("id-ID",{day:"numeric",month:"short"});}
export function orderStatus(s:State,o:Order){return orderFulfillmentLabel(s,o);}
export function Status({text}:{text:string}){return <Badge variant="secondary" data-status={text} className="status-badge">{text}</Badge>;}
function Empty({text}:{text:string}){return <div className="empty-state"><Package size={28}/><p>{text}</p></div>;}

export function Workspace(){
 const [state,setState]=useState<State|null>(null),[actor,setActor]=useState<Actor|null>(null),[error,setError]=useState(""),[loading,setLoading]=useState(true),[mobile,setMobile]=useState(false),[compact,setCompact]=useState(false),[action,setAction]=useState<ActionSpec|null>(null);
 const refreshGate=useRef(singleFlight<void>());
 const logoutPending=useRef(false);
 const [logoutBusy,setLogoutBusy]=useState(false),[logoutError,setLogoutError]=useState(""),[refreshNotice,setRefreshNotice]=useState("");
 const refresh=useCallback(()=>refreshGate.current(async()=>{
  setLoading(true);setError("");setRefreshNotice("Memperbarui data…");
  try {
   const data=await requestJson<{state:State;actor:Actor}>("/api/state",{cache:"no-store"});
   setState(data.state);setActor(data.actor);setRefreshNotice("Data sudah diperbarui.");
  } catch(err) {
   setRefreshNotice("");
   if(err instanceof ApiError&&err.status===401){window.location.assign(`/staff/login?next=${encodeURIComponent(window.location.pathname+window.location.search)}`);return;}
   setError(err instanceof Error?err.message:"Data belum dapat dimuat. Coba kembali.");
  } finally {setLoading(false);}
 }),[]);
 useEffect(()=>{const query=window.matchMedia("(max-width:760px)");const changed=()=>setCompact(query.matches);changed();query.addEventListener("change",changed);return()=>query.removeEventListener("change",changed);},[]);
 useEffect(()=>{const task=setTimeout(()=>void refresh(),0);return()=>clearTimeout(task);},[refresh]);
 const closeMobile=useCallback(()=>setMobile(false),[]);
 const {page,selected,query,go,updateQuery,onDirtyChange,requestLeave,pendingLeave,confirmLeave,cancelLeave}=useWorkspaceNavigation(actor,closeMobile);
 async function logout(){
  if(logoutPending.current)return;
  logoutPending.current=true;setLogoutBusy(true);setLogoutError("");
  try{await logoutSession();if(actor)await clearCartForAccount(actor.id).catch(()=>{});window.location.assign("/staff/login");}
  catch(err){setLogoutError(err instanceof Error?err.message:"Belum berhasil keluar. Coba kembali.");setLogoutBusy(false);}
  finally{logoutPending.current=false;}
 }
 if(!state||!actor)return <main className="loading-page" aria-busy={loading}><Store size={36}/><h1>{loading?"Menyiapkan ruang kerja…":"Data belum tersedia"}</h1><p role={error?"alert":"status"}>{error||"Mengambil pesanan dan aktivitas terakhir."}</p>{!loading&&<Button onClick={()=>void refresh()}>Coba kembali</Button>}</main>;
 const ctx:WorkspaceContext={s:state,actor,go,ask:setAction,refresh};const nav=NAV.filter(n=>(PAGE_ROLES[n.id] as Role[]).includes(actor.role));const active=nav.find(n=>n.id===page);const pending=state.orders.filter(o=>o.status==="submitted").length;
 const sidebarContent=<><a href="/workspace" className="brand" onClick={event=>{event.preventDefault();go("dashboard");}}><span className="brand-mark"><BrandMark/></span><span>Unit Toko<small>PORTAL DIVISI BNI</small></span></a><nav className="workspace-nav" aria-label="Navigasi utama">{NAV_GROUPS.map(group=>{const items=nav.filter(item=>group.pages.includes(item.id));return items.length>0&&<div className="workspace-nav-group" key={group.label}><p className="workspace-nav-label">{group.label}</p>{items.map(n=><button key={n.id} aria-current={page===n.id?"page":undefined} className={`nav-item ${page===n.id?"active":""}`} onClick={()=>go(n.id)}><n.icon size={18} aria-hidden="true"/><span>{n.label}</span>{n.id==="orders"&&pending>0&&<small><span aria-hidden="true">{pending}</span><span className="sr-only">{pending} pesanan menunggu tinjauan</span></small>}</button>)}</div>;})}</nav><div className="sidebar-bottom"><a className="sidebar-note" href="/panduan.html" target="_blank" rel="noreferrer"><CircleHelp size={17}/><p>Panduan demo<small>Alur presentasi capstone</small></p></a><button className="nav-item" disabled={logoutBusy} onClick={()=>requestLeave(()=>void logout())}><LogOut size={18}/><span>{logoutBusy?"Keluar akun…":"Keluar akun"}</span></button></div></>;
 return <div className="workspace"><Toaster richColors position="top-right"/>
  {compact?<MobileNavigation open={mobile} onOpenChange={setMobile}>{sidebarContent}</MobileNavigation>:<aside className="sidebar">{sidebarContent}</aside>}
  <div className="workspace-main"><header className="topbar"><button className="mobile-toggle" aria-label="Buka navigasi" aria-expanded={mobile} aria-controls="mobile-navigation" onClick={()=>setMobile(true)}><Menu size={22}/></button><div className="breadcrumb">Ruang kerja<ChevronRight size={14}/><strong>{active?.label||"Ringkasan"}</strong></div><div className="topbar-right"><span className="demo-chip">DEMO</span><button className="account-mini" onClick={()=>go("profile")} aria-label="Buka profil saya"><Art src={actor.avatar||`/images/avatars/${actor.role==='pic'?'pic-a':actor.role}.png`} alt={`Foto ${actor.name}`}/><div><strong>{actor.name}</strong><small>{ROLE_LABELS[actor.role]}</small></div></button></div></header>
   <main className="content"><div className="page-top"><div><div className="eyebrow">{actor.role==="pic"?state.divisions.find(d=>d.id===actor.divisionId)?.name:"UNIT TOKO · KOPERASI"}</div><h1 id="workspace-title" tabIndex={-1}>{page==="dashboard"?"Ruang kerja Anda":active?.label}</h1><p>{ROLE_PAGE_DESCRIPTIONS[actor.role]?.[page]||PAGE_DESCRIPTIONS[page]}</p></div><div className="page-tools"><span className="date-label">{new Date().toLocaleDateString("id-ID",{day:"numeric",month:"long",year:"numeric",timeZone:"Asia/Jakarta"})}</span><Button variant="outline" size="icon" aria-label={loading?"Memperbarui data":"Muat ulang data"} aria-busy={loading} disabled={loading} onClick={()=>void refresh()}><RefreshCw data-icon="inline-start"/></Button>{["pic","kepala"].includes(actor.role)&&page==="orders"&&<Button onClick={()=>go("catalog")}><Plus data-icon="inline-start"/>Buat pesanan</Button>}</div></div>
    {query.get("returnOrder")&&["deliveries","billing"].includes(page)&&<button className="back-link" onClick={()=>go("orders",query.get("returnOrder")!)}><ArrowLeft size={16}/>Kembali ke pesanan</button>}
    <p className="sr-only" role="status">{refreshNotice}</p>
    {error&&<Alert variant="destructive"><AlertCircle/><AlertTitle>Data belum diperbarui</AlertTitle><AlertDescription><p>{error} Data yang terakhir berhasil dimuat tetap ditampilkan.</p><Button variant="outline" disabled={loading} onClick={()=>void refresh()}>{loading?"Mencoba kembali…":"Coba kembali"}</Button></AlertDescription></Alert>}
    {logoutError&&<Alert variant="destructive"><AlertCircle/><AlertTitle>Belum berhasil keluar</AlertTitle><AlertDescription><p>{logoutError}</p><Button variant="outline" disabled={logoutBusy} onClick={()=>void logout()}>{logoutBusy?"Memproses…":"Coba keluar kembali"}</Button></AlertDescription></Alert>}
    {!active&&<section className="panel module-note">Halaman ini tidak tersedia untuk peran Anda. <Button variant="outline" onClick={()=>go("dashboard")}>Kembali ke beranda</Button></section>}
    {page==="profile"&&<Profile key={actor.id} {...ctx} onDirtyChange={onDirtyChange}/>}
    {page==="dashboard"&&<Dashboard {...ctx}/>}
    {active&&page==="catalog"&&<Catalog {...ctx}/>}
    {active&&page==="orders"&&(selected?<OrderDetail {...ctx} id={selected}/>:<Orders {...ctx} query={query} onQueryChange={updateQuery}/>)}
    {active&&page==="deliveries"&&<Deliveries {...ctx} selected={selected} query={query} onQueryChange={updateQuery}/>}
    {active&&page==="stock"&&<InventoryWorkspace {...ctx} query={query} onQueryChange={updateQuery}/>}
    {active&&page==="billing"&&<><Billing {...ctx} selected={selected} query={query} onQueryChange={updateQuery}/><SettlementControls {...ctx}/></>}
    {active&&page==="payments"&&<><Payments {...ctx}/><SettlementControls {...ctx}/><ExpenseCenter {...ctx}/></>}
    {active&&page==="procurement"&&<Procurement {...ctx}/>}
    {active&&page==="reports"&&<Reports {...ctx} query={query} onQueryChange={updateQuery}/>}
    {active&&page==="periods"&&<Periods {...ctx}/>}
    {active&&page==="admin"&&<AccountSettings {...ctx}/>}
   </main><footer className="workspace-footer">Unit Toko <span>·</span> Demo capstone <span>·</span> Seluruh transaksi menggunakan data simulasi.</footer></div>
  {pendingLeave&&<LeaveChangesDialog keep={cancelLeave} discard={confirmLeave}/>}
  {action&&<ActionDialog key={action.title+JSON.stringify(action.data)} spec={action} defaultDate={suggestedDate(state)} close={()=>setAction(null)} onSuccess={async r=>{await refresh();action.after?.(r);setAction(null);toast.success(r.message);}}/>}
 </div>;
}
function suggestedDate(s:State){const closed=s.periods.filter(p=>p.status==="closed").map(p=>p.id).sort().at(-1);if(!closed||closed<today().slice(0,7))return today();const [y,m]=closed.split("-").map(Number);return new Date(Date.UTC(y,m,1)).toISOString().slice(0,10);}
function ActionDialog({spec,defaultDate,close,onSuccess}:{spec:ActionSpec;defaultDate:string;close:()=>void;onSuccess:(result:CommandResult)=>Promise<void>}){
 const errorRef=useRef<HTMLParagraphElement>(null);

 const [effectiveDate,setEffectiveDate]=useState(defaultDate);
 const [values,setValues]=useState<Record<string,string>>(Object.fromEntries(spec.fields.map(f=>[f.key,String(f.value??"")]))),[busy,setBusy]=useState(false),[error,setError]=useState(""),[commandId,setCommandId]=useState(()=>crypto.randomUUID());
 useEffect(()=>{if(error)errorRef.current?.focus();},[error]);
 async function submit(event:React.FormEvent){event.preventDefault();setBusy(true);setError("");try{const fields=Object.fromEntries(spec.fields.map(f=>[f.key,f.type==="number"?Number(values[f.key]):values[f.key]]));const res=await fetch("/api/commands",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({id:commandId,type:spec.type,date:effectiveDate,data:{...spec.data,...(spec.map?spec.map(values):fields)}})});const data=await res.json() as CommandResult&{error?:string};if(!res.ok)throw new Error(data.error||"Tindakan belum dapat disimpan.");await onSuccess(data);}catch(err){setError(err instanceof Error?err.message:"Koneksi bermasalah. Coba simpan kembali.");}finally{setBusy(false);}}
 const groups:{key:string;label?:string;fields:FormField[]}[]=[];
 for(const field of spec.fields){const last=groups.at(-1);if(field.group&&last?.label===field.group)last.fields.push(field);else groups.push({key:field.key,label:field.group,fields:[field]});}
 const renderField=(f:FormField)=><Field key={f.key}><FieldLabel htmlFor={`field-${f.key}`}>{f.label}{f.optional&&<span className="optional-label"> (opsional)</span>}</FieldLabel>{f.type==="select"?<select className="native-select" id={`field-${f.key}`} value={values[f.key]} required={!f.optional} disabled={busy} onChange={e=>{setValues({...values,[f.key]:e.target.value});setCommandId(crypto.randomUUID());}}><option value="">Pilih…</option>{f.options?.map(o=><option key={o.value} value={o.value}>{o.label}</option>)}</select>:f.type==="textarea"?<Textarea id={`field-${f.key}`} value={values[f.key]} required={!f.optional} disabled={busy} onChange={e=>{setValues({...values,[f.key]:e.target.value});setCommandId(crypto.randomUUID());}}/>:<Input id={`field-${f.key}`} type={f.type||"text"} value={values[f.key]} min={f.min??(f.type==="number"?1:undefined)} max={f.max} required={!f.optional} disabled={busy} onChange={e=>{setValues({...values,[f.key]:e.target.value});setCommandId(crypto.randomUUID());}}/>}</Field>;
 return <Dialog open onOpenChange={open=>!open&&!busy&&close()}><DialogContent className="action-dialog sm:max-w-xl"><DialogHeader><DialogTitle>{spec.title}</DialogTitle><DialogDescription>{spec.description}</DialogDescription></DialogHeader><form className="action-form" onSubmit={submit}><div className="action-form-body"><FieldGroup>{!spec.type.startsWith("period.")&&!spec.type.startsWith("admin.")&&<Field><FieldLabel htmlFor="effective-date">Tanggal pencatatan</FieldLabel><Input id="effective-date" type="date" value={effectiveDate} required disabled={busy} onChange={e=>{setEffectiveDate(e.target.value);setCommandId(crypto.randomUUID());}}/></Field>}{groups.map(g=>g.label?<FieldSet className="action-line-group" key={g.key}><FieldLegend>{g.label}</FieldLegend><FieldGroup>{g.fields.map(renderField)}</FieldGroup></FieldSet>:g.fields.map(renderField))}</FieldGroup>{error&&<p ref={errorRef} tabIndex={-1} className="error-message mt-4" role="alert">{error}</p>}</div><DialogFooter className="action-form-actions"><Button type="button" variant="outline" disabled={busy} onClick={close}>Batal</Button><Button type="submit" disabled={busy}>{busy?"Menyimpan…":spec.label||"Simpan"}</Button></DialogFooter></form></DialogContent></Dialog>;
}
function shipmentLabel(status:string){return ({ready:"Siap dikirim",dispatched:"Dalam perjalanan",received:"Diterima",failed:"Gagal dikirim",cancelled:"Dibatalkan"} as Record<string,string>)[status]||status;}
function Deliveries(ctx:WorkspaceContext&{selected?:string;query:URLSearchParams;onQueryChange:(patch:QueryPatch)=>void}){
 const {s,actor,selected,go,ask,query,onQueryChange}=ctx;
 const status=query.get("status")||"all";
 const shipments=selected?s.shipments.filter(sh=>sh.id===selected):[...s.shipments].reverse().filter(sh=>status==="all"||(status==="active"?["ready","dispatched"].includes(sh.status):sh.status===status));
 return <>{!selected&&<ListFilter id="delivery-filter" label="Status pengiriman" value={status} onChange={value=>onQueryChange({status:value==="all"?null:value})} options={[{value:"all",label:"Semua pengiriman"},{value:"active",label:"Pengiriman aktif"},{value:"ready",label:"Siap dikirim"},{value:"dispatched",label:"Dalam perjalanan"},{value:"received",label:"Diterima"},{value:"failed",label:"Gagal dikirim"},{value:"cancelled",label:"Dibatalkan"}]}/>}{selected&&<button className="back-link" onClick={()=>go("deliveries")}><ArrowLeft size={16}/>Semua pengiriman</button>}{shipments.map(sh=>{const o=get(s.orders,sh.orderId),ls=s.shipmentLines.filter(l=>l.shipmentId===sh.id),complaints=s.complaints.filter(c=>c.shipmentId===sh.id);return <section className="panel delivery-card" key={sh.id}><div className="panel-title"><div><h2>{sh.number}</h2><p>{buyerLabel(s,o)} · {o.address}</p></div><Status text={shipmentLabel(sh.status)}/></div><div className="delivery-meta"><span><Truck size={16}/>{sh.vehicle}</span><span>{s.users.find(u=>u.id===sh.courierId)?.name||"Kurir toko"}</span><span>{shortDate(sh.date)}</span><span>Pesanan {o.number}</span></div><div className="table-scroll"><table className="data-table"><thead><tr><th>Barang</th><th>Jumlah Surat Jalan</th><th>Diterima</th><th>Kembali</th><th>Final</th></tr></thead><tbody>{ls.map(l=><tr key={l.id}><td>{get(s.products,get(s.orderLines,l.orderLineId).productId).name}</td><td>{l.qty}</td><td>{l.accepted}</td><td>{l.returned}</td><td>{l.finalized?"Ya":"Belum"}</td></tr>)}</tbody></table></div>{sh.receiver&&<p className="delivery-proof">Penerima: <strong>{sh.receiver}</strong> · {sh.evidence}</p>}
 <ShipmentExtras ctx={ctx} shipment={sh}/><div className="delivery-actions">{["staf","kurir"].includes(actor.role)&&sh.status==="ready"&&<Button onClick={()=>ask({title:"Berangkatkan pengiriman",description:"Stok toko akan dipindahkan ke barang dalam pengiriman, sesuai Surat Jalan.",type:"shipment.dispatch",fields:[],data:{id:sh.id},label:"Kirim barang"})}>Kirim barang<Truck data-icon="inline-end"/></Button>}{["pic","staf"].includes(actor.role)&&sh.status==="dispatched"&&<Button onClick={()=>ask({title:"Konfirmasi penerimaan",description:"Catat jumlah barang yang benar-benar diterima dalam kondisi sesuai.",type:"shipment.receive",data:{id:sh.id},fields:[{key:"receiver",label:"Nama penerima",value:actor.role==="pic"?actor.name:""},{key:"evidence",label:"Catatan bukti / konfirmasi",value:actor.role==="pic"?"Dikonfirmasi melalui akun PIC":"",optional:actor.role==="pic"},...ls.flatMap(l=>[{key:l.id,group:get(s.products,get(s.orderLines,l.orderLineId).productId).name,label:"Jumlah diterima",type:"number" as const,value:l.qty,min:0,max:l.qty},{key:l.id+"-reason",group:get(s.products,get(s.orderLines,l.orderLineId).productId).name,label:"Alasan bila jumlah berbeda",optional:true}])],map:v=>({receiver:v.receiver,evidence:v.evidence,lines:ls.map(l=>({id:l.id,accepted:Number(v[l.id]),reason:v[l.id+"-reason"]}))}),label:"Konfirmasi penerimaan"})}>Konfirmasi penerimaan</Button>}{actor.role==="staf"&&sh.status==="received"&&ls.some(l=>!l.finalized)&&<Button onClick={()=>ask({title:"Finalisasi transaksi",description:"Transaksi final menjadi dasar invoice. Komplain terkait harus sudah diselesaikan.",type:"sale.finalize",fields:[],data:{id:sh.id},label:"Finalisasi"})}>Finalisasi transaksi</Button>}{ls.every(l=>l.finalized)&&<Status text="Transaksi final"/>}</div>
 {complaints.map(c=><div className="complaint-item" key={c.id}><AlertCircle size={18}/><div><strong>Komplain {c.qty} barang · {c.status==="open"?"Terbuka":"Selesai"}</strong><p>{c.reason}</p>{c.resolution&&<p>{c.resolution}</p>}</div>{actor.role==="staf"&&c.status==="open"&&<Button variant="outline" size="sm" onClick={()=>ask({title:"Selesaikan komplain",description:"Catat penanganan yang disepakati dengan divisi.",type:"complaint.resolve",data:{id:c.id},fields:[{key:"outcome",label:"Hasil penanganan",type:"select",value:"returned",options:[{value:"returned",label:"Barang kembali ke toko"},{value:"accepted",label:"Disepakati diterima divisi"}]},{key:"resolution",label:"Penyelesaian",type:"textarea"}]})}>Tangani</Button>}</div>)}
 {actor.role==="staf"&&ls.filter(l=>l.qty-l.accepted-l.returned>0&&["received","failed"].includes(sh.status)).map(l=><div className="delivery-actions" key={l.id}><Button variant="outline" onClick={()=>ask({title:"Catat barang kembali",description:"Barang yang ditolak dipindahkan kembali ke toko. Kondisi menentukan kelayakan stok.",type:"shipment.return",data:{shipmentLineId:l.id},fields:[{key:"qty",label:"Jumlah",type:"number",value:l.qty-l.accepted-l.returned,max:l.qty-l.accepted-l.returned},{key:"good",label:"Kondisi",type:"select",value:"false",options:[{value:"true",label:"Baik dan layak jual"},{value:"false",label:"Ditahan / rusak"}]},{key:"reason",label:"Alasan"}],map:v=>({qty:Number(v.qty),good:v.good==="true",reason:v.reason})})}>Terima kembali {get(s.products,get(s.orderLines,l.orderLineId).productId).name}</Button></div>)}
 </section>;})}{!shipments.length&&<Empty text="Belum ada tugas pengiriman."/>}<ExpenseCenter {...ctx}/></>;}
function Billing(ctx:WorkspaceContext&{selected?:string;query:URLSearchParams;onQueryChange:(patch:QueryPatch)=>void}){
 const {s,actor,selected,go,query,onQueryChange}=ctx;
 const [composing,setComposing]=useState(false);
 const balance=query.get("balance")||"all";
 const invoices=[...s.invoices].reverse().filter(i=>selected?i.id===selected:balance==="all"||(balance==="open"?invoiceBalance(s,i.id)>0:invoiceBalance(s,i.id)===0));
 const billable=s.shipmentLines.filter(l=>l.finalized&&l.accepted>0&&!s.invoiceLines.some(i=>i.shipmentLineId===l.id));
 return <>
  {selected?<button className="back-link" onClick={()=>go("billing",undefined,{returnOrder:null})}><ArrowLeft size={16}/>Semua invoice</button>:<ListFilter id="invoice-filter" label="Status tagihan" value={balance} onChange={value=>onQueryChange({balance:value==="all"?null:value})} options={[{value:"all",label:"Semua invoice"},{value:"open",label:"Belum lunas"},{value:"paid",label:"Lunas"}]}/>}
  {actor.role==="penagihan"&&billable.length>0&&<section className="panel ready-billing"><div><h2>{billable.length} baris penerimaan siap ditagih</h2><p>Pilih penerimaan yang akan digabungkan. Maksimal 100 baris per invoice.</p></div><Button onClick={()=>setComposing(true)}><Plus data-icon="inline-start"/>Buat invoice</Button></section>}
  <section className="panel"><div className="panel-title"><h2>Invoice pembeli</h2><span className="muted">Sisa piutang {money(sum(invoices.map(i=>invoiceBalance(s,i.id))))}</span></div>
   <div className="table-scroll invoice-desktop"><table className="data-table"><thead><tr><th>Invoice / pembeli</th><th>Jatuh tempo</th><th>Total</th><th>Sisa piutang</th><th>Status / dokumen</th></tr></thead><tbody>{invoices.map(i=><tr key={i.id} id={`invoice-${i.id}`}><td><strong>{i.number}</strong><small>{buyerLabel(s,i)}</small></td><td>{shortDate(i.dueDate)}</td><td className="numeric">{money(invoiceTotal(s,i.id))}</td><td className="numeric">{money(invoiceBalance(s,i.id))}</td><td><Status text={invoiceStatus(s,i)}/><DocumentLink kind="invoice" id={i.id}/></td></tr>)}</tbody></table></div>
   <ul className="invoice-mobile">{invoices.map(i=><li key={i.id}><div><strong>{i.number}</strong><Status text={invoiceStatus(s,i)}/></div><p>{buyerLabel(s,i)}</p><dl><div><dt>Jatuh tempo</dt><dd>{shortDate(i.dueDate)}</dd></div><div><dt>Total invoice</dt><dd>{money(invoiceTotal(s,i.id))}</dd></div><div><dt>Sisa piutang</dt><dd>{money(invoiceBalance(s,i.id))}</dd></div></dl><DocumentLink kind="invoice" id={i.id}/></li>)}</ul>
   {!invoices.length&&<Empty text={s.invoices.length?"Tidak ada invoice yang sesuai dengan pilihan ini.":"Belum ada invoice. Bagian Penagihan menerbitkannya setelah penerimaan difinalisasi."}/>}</section>
  {composing&&<InvoiceComposer ctx={ctx} close={()=>setComposing(false)}/>}
 </>;
}
function Payments(ctx:WorkspaceContext){const {s,actor,ask}=ctx;const [allocationId,setAllocationId]=useState<string|null>(null);const buyers=buyerOptions(s);const buyerRef=(value:string)=>buyers.find(b=>b.value===value)?.ref||{divisionId:null};function record(){ask({title:"Catat transfer simulasi",description:"Pencatatan ini tidak mengirim uang. Piutang berkurang setelah verifikasi dan alokasi oleh Penagihan.",type:"payment.record",fields:[...(actor.role==="penagihan"?[{key:"buyer",label:"Pembeli (kosongkan jika belum diketahui)",type:"select" as const,optional:true,options:buyers}]:[]),{key:"amount",label:"Nilai transfer (Rp)",type:"number"},{key:"payer",label:"Nama pembayar",value:actor.name},{key:"reference",label:"Referensi transfer",optional:true},{key:"note",label:"Catatan",type:"textarea",optional:true}],map:v=>({...buyerRef(v.buyer),amount:Number(v.amount),payer:v.payer,reference:v.reference,note:v.note}),label:"Catat transfer"});}
 return <><section className="panel"><div className="panel-title"><div><h2>Penerimaan dana</h2><p>Bukti transfer perlu diverifikasi sebelum pelunasan.</p></div>{["pic","penagihan"].includes(actor.role)&&<Button onClick={record}><Plus data-icon="inline-start"/>Catat transfer</Button>}</div><div className="table-scroll"><table className="data-table"><thead><tr><th>Pembayar / referensi</th><th>Jumlah</th><th>Belum dialokasikan</th><th>Status</th><th/></tr></thead><tbody>{[...s.payments].reverse().map(p=><tr key={p.id}><td><strong>{p.payer}</strong><small>{p.reference||"Tanpa referensi"} · {shortDate(p.date)}</small><small>{buyerLabel(s,p)}</small><Attachments ctx={ctx} scope="payment" targetId={p.id}/></td><td className="numeric">{money(p.amount)}</td><td>{p.status==="verified"?money(paymentAvailable(s,p.id)):"—"}</td><td><Status text={p.status==="rejected"?"Ditolak":p.status==="recorded"?"Menunggu verifikasi":buyerKey(p)?"Terverifikasi":"Belum teridentifikasi"}/>{p.status==="rejected"&&<p className="module-note"><strong>Alasan penolakan</strong><br/>{p.rejectionReason}<br/>Diputuskan {shortDate(p.rejectedDate||p.date)}. Catat ulang pembayaran dengan data yang benar.</p>}</td><td>{actor.role==="penagihan"&&<div className="row-actions">{p.status==="recorded"&&<><Button variant="outline" size="sm" onClick={()=>ask({title:"Verifikasi dana masuk",description:"Pastikan transfer simulasi telah cocok dengan data penerimaan dana.",type:"payment.verify",data:{id:p.id},fields:[],label:"Verifikasi dana"})}>Verifikasi</Button><Button variant="outline" size="sm" onClick={()=>ask({title:"Tolak bukti pembayaran",description:"Pembayaran ini tidak masuk saldo atau pelunasan. Alasan dapat dilihat pembayar agar catatan yang benar dapat diajukan kembali.",type:"payment.reject",data:{id:p.id},fields:[{key:"reason",label:"Alasan penolakan",type:"textarea"}],label:"Tolak pembayaran"})}>Tolak</Button></>}{p.status!=="rejected"&&!buyerKey(p)&&<Button variant="outline" size="sm" onClick={()=>ask({title:"Identifikasi pembayar",description:"Catat hasil penelusuran dan pembeli pemilik dana.",type:"payment.identify",data:{id:p.id},fields:[{key:"buyer",label:"Pembeli",type:"select",options:buyers},{key:"reason",label:"Hasil penelusuran",type:"textarea"}],map:v=>({...buyerRef(v.buyer),reason:v.reason})})}>Identifikasi</Button>}{p.status==="verified"&&buyerKey(p)&&paymentAvailable(s,p.id)>0&&<Button size="sm" onClick={()=>setAllocationId(p.id)}>Alokasikan</Button>}</div>}</td></tr>)}</tbody></table></div>{!s.payments.length&&<Empty text="Belum ada catatan transfer."/>}</section>{allocationId&&<PaymentAllocation ctx={ctx} paymentId={allocationId} close={()=>setAllocationId(null)}/>}</>;
}

function LeaveChangesDialog({keep,discard}:{keep:()=>void;discard:()=>void}){
 const origin=useRef<HTMLElement|null>(null);

 return <AlertDialog open onOpenChange={open=>!open&&keep()}><AlertDialogContent onOpenAutoFocus={()=>{origin.current=document.activeElement instanceof HTMLElement?document.activeElement:null;}} onCloseAutoFocus={event=>{event.preventDefault();requestAnimationFrame(()=>{if(origin.current?.isConnected)origin.current.focus();});}}><AlertDialogHeader><AlertDialogTitle>Perubahan belum disimpan</AlertDialogTitle><AlertDialogDescription>Anda memiliki perubahan pada profil. Tetap mengedit untuk menyimpannya, atau buang perubahan untuk melanjutkan.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel onClick={keep}>Tetap mengedit</AlertDialogCancel><AlertDialogAction onClick={discard}>Buang perubahan</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>;
}

function ListFilter({id,label,value,options,onChange}:{id:string;label:string;value:string;options:{value:string;label:string}[];onChange:(value:string)=>void}){
 return <Field className="workspace-list-filter"><FieldLabel htmlFor={id}>{label}</FieldLabel><select className="native-select" id={id} value={value} onChange={event=>onChange(event.target.value)}>{options.map(option=><option key={option.value} value={option.value}>{option.label}</option>)}</select></Field>;
}
