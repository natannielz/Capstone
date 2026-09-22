"use client";

import {useCallback, useEffect, useRef, useState, type FormEvent} from "react";
import Link from "next/link";
import {useRouter, useSearchParams} from "next/navigation";
import {ArrowLeft, ArrowRight, FileText, LogOut, MapPin, Package, RefreshCw, Search, Truck, Upload, UserRound, WalletCards} from "lucide-react";
import {ShopShell} from "./shop/shop-shell";
import {Profile} from "./profile";
import {Art} from "./art";
import {Button} from "./ui/button";
import {Badge} from "./ui/badge";
import {Input} from "./ui/input";
import {Textarea} from "./ui/textarea";
import {Alert, AlertDescription} from "./ui/alert";
import {Skeleton} from "./ui/skeleton";
import {Field, FieldDescription, FieldGroup, FieldLabel} from "./ui/field";
import {Empty, EmptyHeader, EmptyTitle, EmptyDescription, EmptyMedia} from "./ui/empty";
import {Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle} from "./ui/dialog";
import type {Actor} from "@/lib/domain/accounts";
import type {CommandResult, Invoice, Order, Payment, Shipment, State} from "@/lib/domain/model";
import {get, invoiceBalance, invoiceStatus, invoiceTotal, lineProgress, money, paymentAvailable, sum} from "@/lib/domain/selectors";
import {matchesOrderQueue, orderFulfillmentLabel, orderValue, relatedOrderInvoices} from "@/lib/domain/order-views";
import {customerPaymentLabel, customerProductHref, customerSubstitutionViews} from "@/lib/domain/customer-order-views";
import {productImage} from "@/lib/domain/catalog";
import {ApiError, logoutSession, requestJson, singleFlight} from "@/lib/client/requests";
import {clearCustomerCart} from "@/lib/client/customer-cart";
import {clearCartForAccount} from "@/lib/client/cart-storage";
import {ambiguousCustomerActionError, clearCustomerActionDraft, customerActionTarget, customerActionValues, findPendingCustomerAction, readCustomerActionDraft, saveCustomerActionDraft, type CustomerActionDraft, type CustomerActionField} from "@/lib/client/customer-action-draft";

type ActionField = CustomerActionField;
type CustomerAction = {title: string; description: string; type: string; data: Record<string, unknown>; fields: ActionField[]; map?: (values: Record<string, string>) => Record<string, unknown>; label?: string};
type Context = {s: State; actor: Actor; refresh: () => Promise<void>; ask: (action: CustomerAction) => void};
const queues = {all: "Semua", preparation: "Diproses", "in-transit": "Dikirim", "needs-pic": "Perlu tindakan", fulfilled: "Terpenuhi", closed: "Dibatalkan / ditolak"};
const shipLabels: Record<Shipment["status"], string> = {ready: "Disiapkan", dispatched: "Dalam pengiriman", received: "Diterima", failed: "Gagal dikirim", cancelled: "Dibatalkan"};
function dateLabel(value: string) { return new Date(value.length === 10 ? `${value}T12:00:00` : value).toLocaleDateString("id-ID", {day: "numeric", month: "short", year: "numeric"}); }

export function CustomerAccount({initialActor, view, orderId}: {initialActor: Actor; view: "profile" | "orders"; orderId?: string}) {
  const params = useSearchParams();
  const [actor, setActor] = useState(initialActor), [s, setState] = useState<State | null>(null);
  const [error, setError] = useState(""), [message, setMessage] = useState(""), [loading, setLoading] = useState(true);
  const [action, setAction] = useState<CustomerAction | null>(null), [logoutBusy, setLogoutBusy] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [leaveTo, setLeaveTo] = useState<string | null>(null);
  const navigationAllowed = useRef(false);
  const [gate] = useState(() => singleFlight<void>());
  const logoutPending = useRef(false);
  const refresh = useCallback(() => gate(async () => {
    setLoading(true); setError("");
    try {
      const result = await requestJson<{actor: Actor; state: State}>("/api/state", {cache: "no-store"});
      if (result.actor.role !== "customer") { window.location.assign("/workspace"); return; }
      setActor(result.actor); setState(result.state);
      const draft = findPendingCustomerAction(result.actor.id, window.location.pathname);
      if (draft) setAction(current => current || restoreCustomerAction(draft));
    } catch (cause) {
      if (cause instanceof ApiError && [401, 403].includes(cause.status)) {
        window.location.assign(`/customer/login?next=${encodeURIComponent(window.location.pathname + window.location.search)}`); return;
      }
      setError(cause instanceof Error ? cause.message : "Data belum dapat dimuat. Coba kembali.");
    } finally { setLoading(false); }
  }), [gate]);
  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => { if (navigationAllowed.current) return; event.preventDefault(); event.returnValue = ""; };
    const navigate = (event: MouseEvent) => {
      if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>("a[href]") : null;
      if (!link || link.target === "_blank" || link.hasAttribute("download")) return;
      const target = new URL(link.href, window.location.href);
      if (target.pathname === window.location.pathname && target.search === window.location.search) return;
      event.preventDefault(); event.stopImmediatePropagation(); setLeaveTo(target.href);
    };
    window.addEventListener("beforeunload", warn);
    document.addEventListener("click", navigate, true);
    return () => {window.removeEventListener("beforeunload", warn); document.removeEventListener("click", navigate, true);};
  }, [dirty]);
  async function logout() {
    if (logoutPending.current) return;
    if (dirty && !window.confirm("Ada perubahan profil yang belum disimpan. Keluar tanpa menyimpannya?")) return;
    logoutPending.current = true; setLogoutBusy(true); setError("");
    try {
      await logoutSession();
      clearCartForAccount(actor.id); await clearCustomerCart(actor.id);
      navigationAllowed.current = true; setDirty(false); window.location.assign("/customer/login?next=%2Fshop");
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Belum berhasil keluar."); }
    finally { logoutPending.current = false; setLogoutBusy(false); }
  }
  const ctx: Context | null = s ? {s, actor, refresh, ask: setAction} : null;
  return <ShopShell actor={actor} active={view === "profile" ? "account" : "orders"}>
    <div className="customer-account">
      <aside className="customer-account-nav" aria-label="Navigasi akun">
        <div className="customer-identity"><Art src={actor.avatar || "/images/avatars/pic-b.png"} alt={`Foto ${actor.name}`} sizes="48px"/><div><strong>{actor.name}</strong><span>Pelanggan Unit Toko</span></div></div>
        <nav><Link href="/account/orders" aria-current={view === "orders" ? "page" : undefined}><Package aria-hidden="true"/>Pesanan saya</Link><Link href="/account" aria-current={view === "profile" ? "page" : undefined}><UserRound aria-hidden="true"/>Profil & alamat</Link></nav>
        <Button variant="ghost" onClick={() => void logout()} disabled={logoutBusy}><LogOut data-icon="inline-start"/>{logoutBusy ? "Keluar…" : "Keluar akun"}</Button>
      </aside>
      <section className="customer-account-main" id="customer-account-content" aria-label="Isi akun pelanggan">
        <header className="customer-page-heading"><div><p className="customer-eyebrow">AKUN SAYA</p><h1>{view === "profile" ? "Profil & alamat" : orderId ? "Detail pesanan" : "Pesanan saya"}</h1><p>{view === "profile" ? "Kelola informasi dan keamanan akun Anda." : "Pantau barang, pengiriman, dan pembayaran Anda."}</p></div><Button variant="outline" size="icon" aria-label="Perbarui data akun" disabled={loading} onClick={() => void refresh()}><RefreshCw/></Button></header>
        {error && <Alert variant="destructive"><AlertDescription>{error} <Button variant="link" onClick={() => void refresh()} disabled={loading}>Coba kembali</Button></AlertDescription></Alert>}
        {message && <p className="customer-notice" role="status">{message}</p>}
        {!message && orderId && params.get("created") === "1" && <p className="customer-notice" role="status">Pesanan berhasil dibuat. Toko akan meninjau barang dan jadwal pengiriman Anda.</p>}
        {!ctx && loading && <div className="customer-loading" aria-label="Memuat akun"><Skeleton className="h-24 w-full"/><Skeleton className="h-60 w-full"/></div>}
        {ctx && (view === "profile" ? <Profile s={ctx.s} actor={actor} refresh={refresh} onDirtyChange={setDirty} go={() => window.location.assign("/account/orders")} ask={() => {}}/> : orderId ? <CustomerOrderDetail {...ctx} id={orderId}/> : <CustomerOrders {...ctx}/>)}
      </section>
    </div>
    {action && <CustomerActionDialog key={actor.id + action.type + JSON.stringify(action.data)} actorId={actor.id} action={action} close={() => setAction(null)} complete={async result => {setAction(null); setMessage(result.message); await refresh();}}/>}
    <Dialog open={Boolean(leaveTo)} onOpenChange={open => {if (!open) setLeaveTo(null);}}><DialogContent><DialogHeader><DialogTitle>Perubahan belum disimpan</DialogTitle><DialogDescription>Data yang Anda ubah akan hilang jika meninggalkan halaman ini.</DialogDescription></DialogHeader><DialogFooter><Button variant="outline" onClick={() => setLeaveTo(null)}>Tetap di halaman</Button><Button onClick={() => {if (!leaveTo) return; navigationAllowed.current = true; setDirty(false); window.location.assign(leaveTo);}}>Tinggalkan halaman</Button></DialogFooter></DialogContent></Dialog>
  </ShopShell>;
}

function CustomerOrders({s}: Context) {
  const params = useSearchParams(), router = useRouter();
  const queue = Object.hasOwn(queues, params.get("status") || "") ? params.get("status")! : "all";
  const q = params.get("q") || "";
  const query = q.trim().toLocaleLowerCase("id-ID");
  const orders = [...s.orders].filter(o => (queue === "preparation" ? ["submitted", "approved"].includes(o.status) && ["Menunggu tinjauan", "Diproses", "Terpenuhi sebagian"].includes(orderFulfillmentLabel(s, o)) : matchesOrderQueue(s, o, queue)))
    .filter(o => `${o.number} ${s.orderLines.filter(l => l.orderId === o.id).map(l => get(s.products, l.productId).name).join(" ")}`.toLocaleLowerCase("id-ID").includes(query))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const page = Math.max(1, Math.min(Math.ceil(orders.length / 8) || 1, Math.floor(Number(params.get("page"))) || 1));
  function url(patch: Record<string, string>) { const next = new URLSearchParams(params); for (const [key, value] of Object.entries(patch)) { if (value) next.set(key, value); else next.delete(key); } return `/account/orders?${next}`; }
  const credit = sum(s.payments.map(p => paymentAvailable(s, p.id)));
  return <>
    <div className="customer-status-links" aria-label="Filter status pesanan">{Object.entries(queues).map(([key, label]) => <Link key={key} href={url({status: key, page: ""})} aria-current={key === queue ? "page" : undefined}>{label}</Link>)}</div>
    <form className="customer-order-search" onSubmit={event => {event.preventDefault(); const data = new FormData(event.currentTarget); router.push(url({q: String(data.get("q") || ""), page: ""}));}}>
      <Field><FieldLabel htmlFor="customer-order-search">Cari pesanan atau barang</FieldLabel><Input key={q} id="customer-order-search" name="q" type="search" placeholder="Nomor pesanan atau nama barang" defaultValue={q}/></Field><Button variant="outline" type="submit"><Search data-icon="inline-start"/>Cari</Button>
    </form>
    {credit > 0 && <p className="customer-wallet"><WalletCards aria-hidden="true"/>Saldo terverifikasi belum dialokasikan: <strong>{money(credit)}</strong></p>}
    {!orders.length && <Empty><EmptyHeader><EmptyMedia variant="icon"><Package/></EmptyMedia><EmptyTitle>{query || queue !== "all" ? "Tidak ada pesanan yang cocok" : "Belum ada pesanan"}</EmptyTitle><EmptyDescription>{query || queue !== "all" ? "Coba ubah kata pencarian atau filter status." : "Temukan kebutuhan pantry, rapat, dan merchandise di toko."}</EmptyDescription></EmptyHeader><Button asChild><Link href={query || queue !== "all" ? "/account/orders" : "/shop"}>{query || queue !== "all" ? "Tampilkan semua" : "Mulai belanja"}</Link></Button></Empty>}
    <div className="customer-order-list">{orders.slice((page - 1) * 8, page * 8).map(order => <article className="customer-order-card" key={order.id}>
      <header><div><Link href={`/account/orders/${order.id}`}><strong>{order.number}</strong></Link><span>{dateLabel(order.createdAt)}</span></div><Badge variant="secondary">{orderFulfillmentLabel(s, order)}</Badge></header>
      <OrderProducts s={s} order={order} preview/>
      <footer><div><span>Nilai pesanan</span><strong>{money(orderValue(s, order.id))}</strong></div><Button asChild variant="outline"><Link href={`/account/orders/${order.id}`}>Lihat pesanan<ArrowRight data-icon="inline-end"/></Link></Button></footer>
    </article>)}</div>
    {orders.length > 8 && <nav className="customer-pagination" aria-label="Halaman pesanan"><Button asChild variant="outline" disabled={page <= 1}><Link href={url({page: String(Math.max(1, page - 1))})} aria-disabled={page <= 1}>Sebelumnya</Link></Button><span>{page} / {Math.ceil(orders.length / 8)}</span><Button asChild variant="outline" disabled={page >= Math.ceil(orders.length / 8)}><Link href={url({page: String(page + 1)})} aria-disabled={page >= Math.ceil(orders.length / 8)}>Berikutnya</Link></Button></nav>}
  </>;
}

function OrderProducts({s, order, preview = false}: {s: State; order: Order; preview?: boolean}) {
  const lines = s.orderLines.filter(l => l.orderId === order.id);
  return <div className="customer-order-products">{(preview ? lines.slice(0, 2) : lines).map(line => {const product = get(s.products, line.productId); const progress = lineProgress(s, line); return <div className="customer-product-row" key={line.id}>
    <Art src={productImage(product)} alt={product.name} sizes="80px"/><div><Link href={customerProductHref(product)}><strong>{product.name}</strong></Link><p>{product.sku} · {product.unit}</p><p>{line.qty} × {money(line.price)}{line.cancelled > 0 ? ` · ${line.cancelled} dibatalkan` : ""}</p>{!preview && <p>{progress.accepted} diterima · {progress.remaining} belum dikirim</p>}</div><strong>{money((line.qty - line.cancelled) * line.price)}</strong>
  </div>;})}{preview && lines.length > 2 && <p className="customer-more-products">+{lines.length - 2} barang lainnya</p>}</div>;
}

function CustomerOrderDetail(ctx: Context & {id: string}) {
  const {s, actor, ask, id} = ctx;
  const order = s.orders.find(o => o.id === id);
  if (!order) return <Empty><EmptyHeader><EmptyTitle>Pesanan tidak tersedia</EmptyTitle><EmptyDescription>Pesanan tidak ditemukan atau tidak dapat diakses akun ini.</EmptyDescription></EmptyHeader><Button asChild variant="outline"><Link href="/account/orders">Kembali ke pesanan</Link></Button></Empty>;
  const lines = s.orderLines.filter(l => l.orderId === id), shipments = s.shipments.filter(sh => sh.orderId === id);
  const invoices = relatedOrderInvoices(s, id);
  const substitutions = customerSubstitutionViews(s, id);
  const canCancel = ["submitted", "approved"].includes(order.status) && lines.some(l => lineProgress(s, l).remaining > 0) && !shipments.some(sh => sh.status === "ready");
  return <>
    <Link className="customer-back" href="/account/orders"><ArrowLeft aria-hidden="true"/>Semua pesanan</Link>
    <section className="customer-section"><header className="customer-section-heading"><div><h2>{order.number}</h2><p>Dibuat {dateLabel(order.createdAt)}</p></div><Badge variant="secondary">{orderFulfillmentLabel(s, order)}</Badge></header>
      <OrderProducts s={s} order={order}/><div className="customer-order-total"><span>Nilai pesanan setelah pembatalan</span><strong>{money(orderValue(s, id))}</strong></div>
      {canCancel && <Button variant="outline" onClick={() => ask({title: "Batalkan sisa pesanan", description: "Hanya barang yang belum dikirim dan masih dapat dibatalkan yang akan dihentikan.", type: "order.cancel", data: {id}, fields: [{key: "reason", label: "Alasan pembatalan", type: "textarea"}], label: "Batalkan sisa pesanan"})}>Batalkan sisa pesanan</Button>}
    </section>
    <section className="customer-section customer-destination"><MapPin aria-hidden="true"/><div><h2>Alamat pengiriman</h2><strong>{order.recipientName || actor.name}</strong><p>{order.recipientPhone || actor.phone}</p><p className="customer-preserve-lines">{order.address}</p>{order.note && <p className="customer-preserve-lines">Catatan: {order.note}</p>}</div></section>
    <section className="customer-section"><h2>Lampiran pesanan</h2><p>{order.status === "submitted" ? "Tambahkan desain merchandise atau spesifikasi barang sebelum toko meninjau pesanan. Gunakan berkas simulasi." : "Pesanan sudah ditinjau atau dibatalkan. Desain dan spesifikasi yang dilampirkan tetap tersedia sebagai acuan."}</p><CustomerAttachments {...ctx} scope="order" targetId={order.id} readOnly={order.status !== "submitted"}/>{order.status !== "submitted" && !s.attachments.some(attachment => attachment.scope === "order" && attachment.targetId === order.id) && <p>Tidak ada lampiran pesanan.</p>}</section>
    {substitutions.map(({substitution: sub, originalProduct, replacementProduct, quantity, originalTotal, total, needsDecision, status}) => <section className="customer-section" key={sub.id}>
      <header className="customer-section-heading"><div><h2>{needsDecision ? "Toko mengusulkan barang pengganti" : "Riwayat barang pengganti"}</h2><p>{originalProduct?.name || "Barang pesanan"} → <strong>{replacementProduct?.name || "Barang pengganti"}</strong></p></div><Badge variant="secondary">{status}</Badge></header>
      <dl className="customer-amounts"><div><dt>Jumlah pengganti</dt><dd>{quantity} {replacementProduct?.unit || "unit"}</dd></div><div><dt>Total pengganti</dt><dd>{money(total)}</dd></div></dl>
      <p>{money(sub.price)} per {replacementProduct?.unit || "unit"} · Nilai barang semula {money(originalTotal)}</p><p className="customer-preserve-lines">Alasan toko: {sub.reason}</p>
      {sub.decidedDate && <p>Keputusan dicatat {dateLabel(sub.decidedDate)}.</p>}{status === "Tidak berlaku" && <p>{sub.cancelledReason || "Pesanan atau sisa barang berubah sehingga usulan ini tidak dapat diproses."}</p>}
      {needsDecision && <div className="customer-actions">{[true, false].map(approve => <Button key={String(approve)} variant={approve ? "default" : "outline"} onClick={() => ask({title: approve ? "Setujui barang pengganti" : "Tolak barang pengganti", description: `${originalProduct?.name || "Barang pesanan"} diganti dengan ${quantity} ${replacementProduct?.unit || "unit"} ${replacementProduct?.name || "barang pengganti"}, ${money(sub.price)} per unit. Total ${money(total)} (nilai semula ${money(originalTotal)}).`, type: "substitution.decide", data: {id: sub.id, approve}, fields: [], label: approve ? "Setujui pengganti" : "Tolak pengganti"})}>{approve ? "Setujui" : "Tolak"}</Button>)}</div>}
    </section>)}
    <section className="customer-section"><h2>Perjalanan pesanan</h2><ol className="customer-timeline"><li><strong>Pesanan dibuat</strong><span>{dateLabel(order.createdAt)}</span></li>{order.reviewedDate && <li><strong>{order.status === "rejected" ? "Pesanan ditolak" : "Ditinjau toko"}</strong><span>{dateLabel(order.reviewedDate)}</span></li>}{shipments.map(sh => <li key={sh.id}><strong>{sh.number} · {shipLabels[sh.status]}</strong><span>{dateLabel(sh.receivedDate || sh.date)}</span></li>)}{invoices.map(({invoice}) => <li key={invoice.id}><strong>{invoice.number} diterbitkan</strong><span>{dateLabel(invoice.date)}</span></li>)}</ol></section>
    {shipments.map(sh => <CustomerShipment key={sh.id} {...ctx} shipment={sh}/>)}
    <section className="customer-section"><h2>Tagihan & pembayaran</h2>{!invoices.length ? <p>Tagihan diterbitkan setelah barang diterima dan penerimaan diselesaikan toko. Anda belum perlu membayar pesanan ini.</p> : invoices.map(({invoice, shared}) => <CustomerInvoice key={invoice.id} {...ctx} invoice={invoice} shared={shared}/>)}</section>
  </>;
}

function CustomerShipment(ctx: Context & {shipment: Shipment}) {
  const {s, actor, ask, shipment: sh} = ctx;
  const lines = s.shipmentLines.filter(l => l.shipmentId === sh.id), complaints = s.complaints.filter(c => c.shipmentId === sh.id);
  const productFor = (id: string) => get(s.products, get(s.orderLines, id).productId);
  function receive() { ask({title: "Konfirmasi barang diterima", description: "Isi jumlah yang benar-benar diterima untuk setiap barang. Beri alasan jika jumlahnya berbeda dari kiriman.", type: "shipment.receive", data: {id: sh.id}, fields: [{key: "receiver", label: "Nama penerima", value: actor.name}, ...lines.flatMap(l => [{key: `qty_${l.id}`, label: `${productFor(l.orderLineId).name} — diterima`, value: l.qty, type: "number" as const, min: 0, max: l.qty}, {key: `reason_${l.id}`, label: "Alasan selisih (jika ada)", optional: true}])], map: values => ({receiver: values.receiver, lines: lines.map(l => ({id: l.id, accepted: Number(values[`qty_${l.id}`]), reason: values[`reason_${l.id}`]}))}), label: "Simpan penerimaan"}); }
  return <section className="customer-section"><header className="customer-section-heading"><div><h2><Truck aria-hidden="true"/>{sh.number}</h2><p>{shipLabels[sh.status]}</p></div><a className="customer-document" href={`/api/documents/shipment/${sh.id}`} target="_blank" rel="noreferrer"><FileText/>Surat Jalan<span className="sr-only"> (tab baru)</span></a></header>
    {lines.map(l => <div className="customer-shipment-line" key={l.id}><div><strong>{productFor(l.orderLineId).name}</strong><p>{l.qty} dikirim · {l.accepted} diterima · {l.returned} kembali</p></div>{sh.status === "received" && l.accepted > 0 && !l.finalized && <Button variant="outline" size="sm" onClick={() => ask({title: "Ajukan komplain barang", description: "Jumlah yang dikomplain akan diperiksa toko sebelum ditagih.", type: "complaint.create", data: {shipmentLineId: l.id}, fields: [{key: "qty", label: "Jumlah barang bermasalah", type: "number", value: 1, min: 1, max: l.accepted}, {key: "reason", label: "Jelaskan masalah barang", type: "textarea"}], map: v => ({qty: Number(v.qty), reason: v.reason})})}>Ajukan komplain</Button>}</div>)}
    {complaints.map(c => <p className="customer-notice" key={c.id}>Komplain {c.qty} barang: {c.reason}. {c.status === "open" ? "Menunggu penanganan toko." : `Selesai: ${c.resolution}`}</p>)}
    {sh.status === "dispatched" && <Button onClick={receive}>Konfirmasi penerimaan</Button>}
    {sh.receiver && <p>Penerima: {sh.receiver}{sh.receivedDate ? ` · ${dateLabel(sh.receivedDate)}` : ""}</p>}
    {["dispatched", "received"].includes(sh.status) && <CustomerAttachments {...ctx} scope="receipt" targetId={sh.id}/>}
  </section>;
}

function CustomerInvoice(ctx: Context & {invoice: Invoice; shared: boolean}) {
  const {s, actor, ask, invoice, shared} = ctx;
  const balance = invoiceBalance(s, invoice.id);
  const paymentIds = new Set(s.allocations.filter(a => a.invoiceId === invoice.id).map(a => a.paymentId));
  const payments = s.payments.filter(p => p.invoiceId === invoice.id || paymentIds.has(p.id) || (p.sourceCreditId && s.credits.some(c => c.id === p.sourceCreditId && c.invoiceId === invoice.id)));
  return <div className="customer-invoice"><header className="customer-section-heading"><div><h3>{invoice.number}</h3><p>Jatuh tempo {dateLabel(invoice.dueDate)}</p></div><Badge variant="secondary">{invoiceStatus(s, invoice)}</Badge></header><dl className="customer-amounts"><div><dt>Total tagihan</dt><dd>{money(invoiceTotal(s, invoice.id))}</dd></div><div><dt>Sisa pembayaran</dt><dd>{money(balance)}</dd></div></dl>{shared && <p>Invoice ini menggabungkan penerimaan dari beberapa pesanan Anda.</p>}
    <div className="customer-actions"><a className="customer-document" href={`/api/documents/invoice/${invoice.id}`} target="_blank" rel="noreferrer"><FileText/>Lihat invoice<span className="sr-only"> (tab baru)</span></a>{balance > 0 && <Button onClick={() => ask({title: "Catat pembayaran simulasi", description: "Tidak ada transfer uang sungguhan. Setelah dicatat, unggah bukti simulasi. Penagihan akan memverifikasi dan mengalokasikan dana ke tagihan.", type: "payment.record", data: {invoiceId: invoice.id, payer: actor.name}, fields: [{key: "amount", label: "Jumlah pembayaran (Rp)", type: "number", value: balance, min: 1, max: balance}, {key: "reference", label: "Referensi transfer simulasi"}, {key: "note", label: "Catatan (opsional)", type: "textarea", optional: true}], map: v => ({amount: Number(v.amount), reference: v.reference, note: v.note}), label: "Catat pembayaran"})}>Catat pembayaran</Button>}</div>
    {payments.some(p => p.status === "recorded") && <p className="customer-notice">Ada pembayaran menunggu verifikasi. Periksa catatan di bawah sebelum mencatat pembayaran lagi.</p>}
    {payments.map(p => <CustomerPayment key={p.id} {...ctx} payment={p}/>)}
    {s.credits.filter(c => c.invoiceId === invoice.id).map(c => <p className="customer-notice" key={c.id}>Koreksi tagihan {money(c.amount)} · {c.status === "approved" ? "Disetujui" : c.status === "rejected" ? "Ditolak" : "Menunggu persetujuan"}. {c.reason}{c.status === "rejected" && c.rejectionReason ? ` Alasan penolakan: ${c.rejectionReason}` : ""}</p>)}
  </div>;
}

function CustomerPayment(ctx: Context & {payment: Payment}) {
  const {s, payment: p} = ctx;
  const allocated = sum(s.allocations.filter(a => a.paymentId === p.id).map(a => a.amount));
  return <article className="customer-payment"><header><strong>{money(p.amount)}</strong><Badge variant="outline">{customerPaymentLabel(s, p)}</Badge></header><p>{p.reference || (p.kind === "credit" ? "Saldo koreksi tagihan" : "Transfer tanpa referensi")} · {dateLabel(p.date)}</p>{p.status === "rejected" && <p className="customer-notice">Pembayaran ditolak: {p.rejectionReason || "Bukti belum sesuai."} Periksa jumlah dan referensi, lalu catat pembayaran yang benar melalui tagihan di atas.</p>}{p.status === "verified" && <p>{money(allocated)} telah dialokasikan · {money(paymentAvailable(s, p.id))} saldo tersedia</p>}{s.refunds.filter(r => r.paymentId === p.id).map(r => <p key={r.id}>Pengembalian {money(r.amount)} · {r.status === "paid" ? "Dibayar secara simulasi" : r.status === "approved" ? "Disetujui" : r.status === "rejected" ? "Ditolak" : "Diajukan"}. {r.reason}{r.status === "rejected" && r.rejectionReason ? ` Alasan penolakan: ${r.rejectionReason}` : ""}</p>)}{p.kind !== "credit" && <CustomerAttachments {...ctx} scope="payment" targetId={p.id} readOnly={p.status === "rejected"}/>}</article>;
}

function CustomerAttachments({s, refresh, scope, targetId, readOnly = false}: Context & {scope: "payment" | "receipt" | "order"; targetId: string; readOnly?: boolean}) {
  const [busy, setBusy] = useState(false), [error, setError] = useState("");
  const uploading = useRef(false);
  const attachments = s.attachments.filter(a => a.scope === scope && a.targetId === targetId);
  async function upload(file: File) {
    if (uploading.current) return;
    if (file.size > 4 * 1024 * 1024 || !["image/png", "image/jpeg", "application/pdf"].includes(file.type)) {setError("Pilih PNG, JPG, atau PDF maksimal 4 MB."); return;}
    uploading.current = true; setBusy(true); setError("");
    try { const data = new FormData(); data.set("file", file); data.set("scope", scope); data.set("targetId", targetId); await requestJson("/api/attachments", {method: "POST", body: data}); await refresh(); }
    catch (cause) {setError(cause instanceof Error ? cause.message : "Unggahan belum berhasil. Coba kembali.");}
    finally {uploading.current = false; setBusy(false);}
  }
  return <div className="customer-attachments">{attachments.map(a => <a className="customer-document" key={a.id} href={`/api/attachments/${a.id}`} target="_blank" rel="noreferrer"><FileText/>{a.name}<span className="sr-only"> (tab baru)</span></a>)}{!readOnly && <Field><FieldLabel htmlFor={`proof-${targetId}`}><Upload aria-hidden="true"/> {busy ? "Mengunggah lampiran…" : scope === "order" ? "Unggah desain atau spesifikasi" : scope === "payment" ? "Unggah bukti pembayaran" : "Unggah bukti penerimaan"}</FieldLabel><Input id={`proof-${targetId}`} type="file" accept="image/png,image/jpeg,application/pdf" disabled={busy} aria-invalid={Boolean(error)} onChange={event => {const file = event.target.files?.[0]; if (file) void upload(file); event.target.value = "";}}/><FieldDescription>PNG, JPG, atau PDF, maksimal 4 MB. Gunakan berkas simulasi.</FieldDescription></Field>}{error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}</div>;
}

function restoreCustomerAction(draft: CustomerActionDraft): CustomerAction {
  return {...draft.presentation, type: draft.command.type, data: draft.baseData, map: values => customerActionValues(draft.command.type, draft.baseData, values)};
}

function CustomerActionDialog({actorId, action, close, complete}: {actorId: string; action: CustomerAction; close: () => void; complete: (result: CommandResult) => Promise<void>}) {
  const target = customerActionTarget(action.type, action.data);
  const [draft, setDraft] = useState(() => readCustomerActionDraft(actorId, target));
  const [values, setValues] = useState<Record<string, string>>(() => draft?.values || Object.fromEntries(action.fields.map(f => [f.key, String(f.value ?? "")])));
  const [commandId, setCommandId] = useState(() => crypto.randomUUID()), [busy, setBusy] = useState(false), [error, setError] = useState("");
  const pending = useRef(false), errorRef = useRef<HTMLDivElement>(null);
  const presentation = draft?.presentation || action;
  const locked = busy || Boolean(draft);
  useEffect(() => {if (error) errorRef.current?.focus();}, [error]);
  async function submit(event: FormEvent) {
    event.preventDefault(); if (pending.current) return;
    pending.current = true; setBusy(true); setError("");
    let sent: CustomerActionDraft | undefined;
    try {
      sent = saveCustomerActionDraft(draft || {version: 1, actorId, target, path: window.location.pathname,
        command: {id: commandId, type: action.type, data: {...action.data, ...(action.map ? action.map(values) : values)}},
        baseData: action.data, values, presentation: {title: action.title, description: action.description, fields: action.fields, label: action.label}});
      setDraft(sent); setValues(sent.values);
      const result = await requestJson<CommandResult>("/api/commands", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(sent.command)});
      if (!result || typeof result.id !== "string" || typeof result.message !== "string") throw new ApiError("Hasil tindakan belum dapat dipastikan. Coba periksa kembali permintaan yang sama.", 200);
      clearCustomerActionDraft(actorId, target, sent.command.id); setDraft(undefined);
      await complete(result);
    } catch (cause) {
      if (sent && !ambiguousCustomerActionError(cause)) {
        clearCustomerActionDraft(actorId, target, sent.command.id); setDraft(undefined);
      }
      if (cause instanceof ApiError && cause.status === 401) {
        window.location.assign(`/customer/login?next=${encodeURIComponent(window.location.pathname + window.location.search)}`); return;
      }
      setError(cause instanceof Error ? cause.message : "Tindakan belum berhasil. Isian Anda tetap tersimpan.");
    }
    finally {pending.current = false; setBusy(false);}
  }
  function edit(key: string, value: string) {
    if (locked || pending.current) return;
    setValues(previous => ({...previous, [key]: value})); setCommandId(crypto.randomUUID()); setError("");
  }
  return <Dialog open onOpenChange={open => {if (!open && !pending.current) close();}}><DialogContent className="customer-action-dialog" onEscapeKeyDown={event => {if (pending.current) event.preventDefault();}}><DialogHeader><DialogTitle>{presentation.title}</DialogTitle><DialogDescription>{presentation.description}</DialogDescription></DialogHeader><form onSubmit={submit} aria-busy={busy}>
    {draft && !busy && <Alert><AlertDescription>Hasil permintaan sebelumnya belum dipastikan. Isian dikunci agar tidak tercatat dua kali. Periksa kembali untuk melanjutkan permintaan yang sama, termasuk setelah halaman dimuat ulang.</AlertDescription></Alert>}
    <FieldGroup>{presentation.fields.map(f => <Field key={f.key} data-disabled={locked}><FieldLabel htmlFor={`customer-action-${f.key}`}>{f.label}</FieldLabel>{f.type === "textarea" ? <Textarea id={`customer-action-${f.key}`} value={values[f.key] || ""} disabled={locked} required={!f.optional} maxLength={1000} onChange={event => edit(f.key, event.target.value)}/> : <Input id={`customer-action-${f.key}`} type={f.type || "text"} min={f.min} max={f.max} step={f.type === "number" ? 1 : undefined} value={values[f.key] || ""} disabled={locked} required={!f.optional} maxLength={250} onChange={event => edit(f.key, event.target.value)}/>} {f.help && <FieldDescription>{f.help}</FieldDescription>}</Field>)}</FieldGroup>
    {error && <Alert ref={errorRef} tabIndex={-1} variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}<DialogFooter><Button variant="outline" type="button" disabled={busy} onClick={close}>{draft ? "Tutup sementara" : "Kembali"}</Button><Button type="submit" disabled={busy}>{busy ? "Menyimpan…" : draft ? "Periksa kembali" : presentation.label || "Simpan"}</Button></DialogFooter></form></DialogContent></Dialog>;
}
