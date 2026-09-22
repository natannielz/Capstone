"use client";
import {buyerLabel} from "@/lib/domain/buyers";

import {ArrowLeft, ArrowRight, Check, FileText, Package, Search, Truck, X} from "lucide-react";
import {Button} from "@/components/ui/button";
import {Badge} from "@/components/ui/badge";
import {Field, FieldGroup, FieldLabel} from "@/components/ui/field";
import {InputGroup, InputGroupAddon, InputGroupInput} from "@/components/ui/input-group";
import type {Order, OrderLine, Shipment, State} from "@/lib/domain/model";
import {get, invoiceStatus, lineProgress, money, productAvailable, sum} from "@/lib/domain/selectors";
import {matchesOrderQueue, ORDER_QUEUES, orderFulfillmentLabel, orderValue, readOrderQuery, relatedOrderInvoices, substitutionNeedsDecision} from "@/lib/domain/order-views";
import type {WorkspaceContext} from "./workspace";

type QueryProps = {
  query: URLSearchParams;
  onQueryChange: (patch: Record<string, string | null>) => void;
};

function dateLabel(value: string) {
  return new Date(value.length === 10 ? `${value}T12:00:00` : value)
    .toLocaleDateString("id-ID", {day: "numeric", month: "short", year: "numeric"});
}

function StateBadge({children}: {children: string}) {
  return <Badge variant="secondary" className="status-badge" data-status={children}>{children}</Badge>;
}

function Document({kind, id, children}: {kind: "shipment" | "invoice"; id: string; children: string}) {
  return <a className="order-document-link" href={`/api/documents/${kind}/${encodeURIComponent(id)}`} target="_blank" rel="noreferrer">
    <FileText size={16} aria-hidden="true"/>{children}<span className="sr-only"> (tab baru)</span>
  </a>;
}

function shipmentStatus(shipment: Shipment) {
  return {ready: "Siap dikirim", dispatched: "Dalam perjalanan", received: "Diterima", failed: "Gagal dikirim", cancelled: "Dibatalkan"}[shipment.status];
}

export function OrderList({s, orders, onSelect, actionLabel = () => "Lihat pesanan"}: {
  s: State;
  orders: Order[];
  onSelect: (id: string) => void;
  actionLabel?: (order: Order) => string;
}) {
  if (!orders.length) return <p className="order-v5-empty-note">Belum ada pesanan dalam daftar ini.</p>;
  return <>
    <div className="orders-v5-table"><table className="data-table"><thead><tr>
      <th>Pesanan / pembeli</th><th>Dibutuhkan</th><th className="numeric">Nilai pesanan</th><th>Status</th><th><span className="sr-only">Tindakan</span></th>
    </tr></thead><tbody>{orders.map(order => <tr key={order.id}>
      <td><button className="table-link" onClick={() => onSelect(order.id)}>{order.number}</button><small>{buyerLabel(s, order)}</small></td>
      <td>{dateLabel(order.neededAt)}</td><td className="numeric">{money(orderValue(s, order.id))}</td>
      <td><StateBadge>{orderFulfillmentLabel(s, order)}</StateBadge></td>
      <td><Button size="sm" variant="outline" onClick={() => onSelect(order.id)}>{actionLabel(order)}<ArrowRight data-icon="inline-end"/></Button></td>
    </tr>)}</tbody></table></div>
    <div className="orders-v5-mobile">{orders.map(order => <article className="order-list-card" key={order.id}>
      <div className="order-list-card-top"><h3>{order.number}</h3><StateBadge>{orderFulfillmentLabel(s, order)}</StateBadge></div>
      <p>{buyerLabel(s, order)}</p>
      <dl><div><dt>Dibutuhkan</dt><dd>{dateLabel(order.neededAt)}</dd></div><div><dt>Nilai pesanan</dt><dd>{money(orderValue(s, order.id))}</dd></div></dl>
      <Button variant="outline" onClick={() => onSelect(order.id)}>{actionLabel(order)}<ArrowRight data-icon="inline-end"/></Button>
    </article>)}</div>
  </>;
}

export function Orders({s, actor, go, query, onQueryChange}: WorkspaceContext & QueryProps) {
  const filter = readOrderQuery(query);
  const filtered = s.orders.filter(order => matchesOrderQueue(s, order, filter.queue))
    .filter(order => `${order.number} ${buyerLabel(s, order)}`
      .toLocaleLowerCase("id-ID").includes(filter.q.trim().toLocaleLowerCase("id-ID")))
    .sort((a, b) => filter.sort === "needed"
      ? a.neededAt.localeCompare(b.neededAt) || b.createdAt.localeCompare(a.createdAt)
      : b.createdAt.localeCompare(a.createdAt) || b.number.localeCompare(a.number));
  const pages = Math.max(1, Math.ceil(filtered.length / 12));
  const page = Math.min(filter.page, pages);
  const visible = filtered.slice((page - 1) * 12, page * 12);
  const isFiltered = filter.queue !== "all" || Boolean(filter.q);
  const clear = () => onQueryChange({queue: null, q: null, orderPage: null});
  const actionLabel = (order: Order) => actor.role === "kepala" && order.status === "submitted"
    ? "Tinjau pesanan" : actor.role === "staf" && matchesOrderQueue(s, order, "preparation")
      ? "Siapkan pesanan" : actor.role === "pic" && matchesOrderQueue(s, order, "needs-pic")
        ? "Lihat tindakan" : "Lihat pesanan";

  return <section className="panel orders-v5">
    <div className="panel-title orders-v5-heading">
      <div><h2>{ORDER_QUEUES[filter.queue]}</h2><p aria-live="polite">{filtered.length} pesanan{isFiltered ? ` dari ${s.orders.length}` : ""}</p></div>
      {isFiltered && <Button variant="ghost" onClick={clear}><X data-icon="inline-start"/>Hapus filter</Button>}
    </div>
    <FieldGroup className="orders-v5-filters">
      <Field><FieldLabel htmlFor="orders-search">Cari pesanan</FieldLabel>
        <InputGroup><InputGroupAddon><Search aria-hidden="true"/></InputGroupAddon>
          <InputGroupInput id="orders-search" type="search" placeholder="Nomor pesanan atau pembeli" value={filter.q}
            onChange={event => onQueryChange({q: event.target.value || null, orderPage: null})}/>
        </InputGroup>
      </Field>
      <Field><FieldLabel htmlFor="orders-queue">Status pekerjaan</FieldLabel>
        <select id="orders-queue" className="native-select" value={filter.queue}
          onChange={event => onQueryChange({queue: event.target.value === "all" ? null : event.target.value, orderPage: null})}>
          {Object.entries(ORDER_QUEUES).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </select>
      </Field>
      <Field><FieldLabel htmlFor="orders-sort">Urutkan</FieldLabel>
        <select id="orders-sort" className="native-select" value={filter.sort}
          onChange={event => onQueryChange({sort: event.target.value === "newest" ? null : event.target.value, orderPage: null})}>
          <option value="newest">Pesanan terbaru</option><option value="needed">Tanggal kebutuhan</option>
        </select>
      </Field>
    </FieldGroup>
    {!!visible.length && <OrderList s={s} orders={visible} onSelect={id => go("orders", id)} actionLabel={actionLabel}/>}
    {!visible.length && <div className="orders-v5-empty"><Package aria-hidden="true"/><h3>{isFiltered ? "Tidak ada pesanan yang cocok" : "Belum ada pesanan"}</h3>
      <p>{isFiltered ? "Ubah pencarian atau status pekerjaan untuk melihat pesanan lainnya." : "Pesanan pembeli akan muncul di sini setelah diajukan."}</p>
      {isFiltered && <Button variant="outline" onClick={clear}>Tampilkan semua pesanan</Button>}
    </div>}
    {pages > 1 && <nav className="orders-v5-pagination" aria-label="Halaman daftar pesanan">
      <Button variant="outline" disabled={page === 1} onClick={() => onQueryChange({orderPage: String(page - 1)})}>Sebelumnya</Button>
      <span>Halaman {page} dari {pages}</span>
      <Button variant="outline" disabled={page === pages} onClick={() => onQueryChange({orderPage: String(page + 1)})}>Berikutnya</Button>
    </nav>}
  </section>;
}

export function OrderDetail(ctx: WorkspaceContext & {id: string}) {
  const {s, actor, id, go, ask} = ctx;
  const order = s.orders.find(item => item.id === id);
  if (!order) return <section className="panel orders-v5-empty"><Package aria-hidden="true"/><h2>Pesanan tidak tersedia</h2><p>Pesanan tidak ditemukan atau tidak dapat diakses oleh akun Anda.</p><Button variant="outline" onClick={() => go("orders")}>Kembali ke pesanan</Button></section>;
  const lines = s.orderLines.filter(line => line.orderId === id);
  const division = {name: buyerLabel(s, order)};
  const shipments = s.shipments.filter(shipment => shipment.orderId === id);
  const substitutions = s.substitutions.filter(sub => lines.some(line => line.id === sub.orderLineId));
  const pendingSubstitutions = substitutions.filter(sub => substitutionNeedsDecision(s, sub));
  const inTransit = shipments.filter(shipment => shipment.status === "dispatched");
  const reserved = lines.filter(line => {const progress = lineProgress(s, line); return progress.reserved > progress.staged;});
  const canSeeBilling = ["pic", "kepala", "laporan", "penagihan", "pimpinan", "akuntansi"].includes(actor.role);
  const invoices = canSeeBilling ? relatedOrderInvoices(s, id) : [];
  const balance = sum(invoices.map(item => item.balance));
  const paymentLabel = !invoices.length ? "Belum ada invoice" : balance > 0 ? "Masih ada piutang" : "Invoice terkait lunas";
  const canOpenDeliveries = ["pic", "kepala", "staf", "kurir"].includes(actor.role);

  function createShipment() {
    ask({title: "Buat Surat Jalan", description: "Masukkan jumlah yang disiapkan. Sisa pesanan dapat dikirim menyusul.", type: "shipment.create", data: {orderId: id},
      fields: [
        {key: "courierId", label: "Kurir", type: "select", value: s.users.find(user => user.role === "kurir" && user.active)?.id, options: s.users.filter(user => user.role === "kurir" && user.active).map(user => ({value: user.id, label: user.name}))},
        {key: "vehicle", label: "Kendaraan", value: "B 1234 DEMO"},
        ...reserved.map(line => ({key: line.id, label: `${get(s.products, line.productId).name} (${get(s.products, line.productId).unit})`, type: "number" as const, min: 0, value: lineProgress(s, line).reserved - lineProgress(s, line).staged, max: lineProgress(s, line).reserved - lineProgress(s, line).staged})),
      ],
      map: values => ({courierId: values.courierId, vehicle: values.vehicle, lines: reserved.filter(line => Number(values[line.id]) > 0).map(line => ({orderLineId: line.id, qty: Number(values[line.id])}))}),
      label: "Buat Surat Jalan", after: result => go("deliveries", result.id),
    });
  }

  function reserveStock(line: OrderLine) {
    const product = get(s.products, line.productId), progress = lineProgress(s, line);
    ask({title: "Cadangkan stok", description: `${product.name} · ${productAvailable(s, product.id)} ${product.unit} tersedia`, type: "stock.reserve", data: {orderLineId: line.id},
      fields: [{key: "qty", label: `Jumlah (${product.unit})`, type: "number", value: Math.min(progress.remaining - progress.reserved, productAvailable(s, product.id)), max: progress.remaining - progress.reserved}], label: "Cadangkan"});
  }

  function proposeSubstitution(line: OrderLine) {
    ask({title: "Usulkan barang pengganti", description: "PIC harus menyetujui produk dan harga pengganti sebelum diproses.", type: "substitution.propose", data: {orderLineId: line.id},
      fields: [{key: "productId", label: "Produk pengganti", type: "select", options: s.products.filter(product => product.id !== line.productId && product.active).map(product => ({value: product.id, label: `${product.name} · ${money(product.price)}`}))}, {key: "reason", label: "Alasan", type: "textarea"}], label: "Kirim usulan"});
  }

  return <div className="order-detail-v5">
    <button className="back-link" onClick={() => go("orders")}><ArrowLeft size={16} aria-hidden="true"/>Kembali ke daftar pesanan</button>
    <section className="panel order-v5-summary">
      <div className="order-v5-summary-main"><span className="eyebrow">{division.name}</span><h2>{order.number}</h2><p>{order.address}</p><p className="order-v5-note">{order.note || "Tidak ada catatan tambahan."}</p></div>
      <div className="order-v5-summary-side"><span>Tanggal kebutuhan</span><strong>{dateLabel(order.neededAt)}</strong><span>Nilai pesanan</span><strong>{money(orderValue(s, id))}</strong>
        {actor.role === "kepala" && order.status === "submitted" && <div className="order-v5-actions">
          <Button onClick={() => ask({title: "Teruskan pesanan ke staf", description: "Pesanan akan diproses untuk pengecekan dan penyiapan stok.", type: "order.review", fields: [], data: {id, approve: true}, label: "Teruskan pesanan"})}><Check data-icon="inline-start"/>Tinjau & setujui</Button>
          <Button variant="outline" onClick={() => ask({title: "Tolak pesanan", description: "Alasan dapat dilihat pada catatan pesanan.", type: "order.review", fields: [{key: "reason", label: "Alasan penolakan", type: "textarea"}], data: {id, approve: false}})}>Tolak pesanan</Button>
        </div>}
        {actor.role === "staf" && reserved.length > 0 && <Button onClick={createShipment}><Truck data-icon="inline-start"/>Buat Surat Jalan</Button>}
      </div>
    </section>
    <div className="order-v5-statuses">
      <section className="panel"><h3>Pemenuhan barang</h3><StateBadge>{orderFulfillmentLabel(s, order)}</StateBadge><p>{shipments.length} Surat Jalan · {lines.filter(line => line.qty > line.cancelled).length} jenis barang aktif</p></section>
      {canSeeBilling && <section className="panel"><h3>Penagihan</h3><StateBadge>{paymentLabel}</StateBadge><p>{invoices.length ? `${invoices.length} invoice terkait · sisa piutang invoice ${money(balance)}` : "Invoice diterbitkan setelah penerimaan difinalisasi."}</p></section>}
    </div>
    {actor.role === "pic" && (pendingSubstitutions.length > 0 || inTransit.length > 0) && <section className="panel order-v5-attention">
      <h3>Perlu tindakan Anda</h3>
      {pendingSubstitutions.map(sub => <div key={sub.id}><p>Tinjau pengganti: <strong>{get(s.products, sub.productId).name}</strong></p><Button variant="outline" onClick={() => document.getElementById(`substitution-${sub.id}`)?.scrollIntoView({behavior: "smooth", block: "center"})}>Tinjau pengganti<ArrowRight data-icon="inline-end"/></Button></div>)}
      {inTransit.map(shipment => <div key={shipment.id}><p>Konfirmasi barang pada <strong>{shipment.number}</strong></p><Button variant="outline" onClick={() => go("deliveries", shipment.id)}>Buka penerimaan<ArrowRight data-icon="inline-end"/></Button></div>)}
    </section>}
    <section className="panel"><div className="panel-title"><div><h2>Rincian barang</h2><p>Jumlah ditampilkan sesuai satuan setiap barang.</p></div></div>
      <div className="order-v5-lines">{lines.map(line => {
        const product = get(s.products, line.productId), progress = lineProgress(s, line);
        const pending = pendingSubstitutions.some(sub => sub.orderLineId === line.id);
        const canReserve = actor.role === "staf" && order.status === "approved" && progress.remaining > progress.reserved;
        const canSubstitute = ["kepala", "staf"].includes(actor.role) && order.status === "approved" && progress.remaining > 0 && progress.staged === 0;
        return <article className="order-v5-line" key={line.id}>
          <div className="order-v5-line-heading"><div><h3>{product.name}</h3><p>{money(line.price)} / {product.unit}{line.requestedProductId !== product.id ? " · Barang pengganti" : ""}</p>{line.cancelled > 0 && <small>{line.cancelled} {product.unit} dibatalkan atau diganti</small>}</div><span>{product.sku}</span></div>
          <dl className="order-v5-quantities">{[["Dipesan", line.qty - line.cancelled], ["Dicadangkan", progress.reserved], ["Dikirim", progress.shipped], ["Diterima", progress.accepted]].map(([label, value]) => <div key={String(label)}><dt>{label}</dt><dd>{value} <span>{product.unit}</span></dd></div>)}</dl>
          {(canReserve || canSubstitute) && <div className="order-v5-line-actions">
            {canReserve && <Button size="sm" variant="outline" disabled={pending || productAvailable(s, product.id) <= 0} onClick={() => reserveStock(line)}>Cadangkan stok</Button>}
            {canSubstitute && <Button size="sm" variant="ghost" disabled={pending} onClick={() => proposeSubstitution(line)}>Usulkan pengganti</Button>}
            <p>{pending ? "Menunggu keputusan PIC atas barang pengganti." : `${Math.max(0, productAvailable(s, product.id))} ${product.unit} tersedia di toko.`}</p>
          </div>}
        </article>;
      })}</div>
    </section>
    {substitutions.map(sub => {
      const product = get(s.products, sub.productId), original = get(s.orderLines, sub.orderLineId);
      const quantity = sub.qty ?? lineProgress(s, original).remaining;
      const needsDecision = substitutionNeedsDecision(s, sub);
      const cancelled = sub.status === "cancelled" || (sub.status === "pending" && !needsDecision);
      return <section className="panel order-v5-substitution" id={`substitution-${sub.id}`} key={sub.id}>
        <div><h3>Usulan barang pengganti</h3><p>{get(s.products, original.productId).name} → <strong>{product.name}</strong></p><p>{quantity} {product.unit} × {money(sub.price)} · total {money(quantity * sub.price)}</p><p>{sub.reason}</p>{cancelled && <p>{sub.cancelledReason || "Usulan tidak berlaku karena pesanan atau sisa barang telah dibatalkan."}</p>}</div>
        <StateBadge>{cancelled ? "Tidak berlaku" : needsDecision ? "Menunggu persetujuan" : sub.status === "approved" ? "Disetujui" : "Ditolak"}</StateBadge>
        {actor.role === "pic" && needsDecision && <div className="order-v5-actions">{[true, false].map(approve => <Button key={String(approve)} variant={approve ? "default" : "outline"} onClick={() => ask({title: approve ? "Setujui barang pengganti" : "Tolak barang pengganti", description: `${quantity} ${product.unit} ${product.name} dengan harga ${money(sub.price)} per ${product.unit}. Total ${money(quantity * sub.price)}.`, type: "substitution.decide", fields: [], data: {id: sub.id, approve}})}>{approve ? "Setujui pengganti" : "Tolak pengganti"}</Button>)}</div>}
      </section>;
    })}
    <section className="panel"><div className="panel-title"><h2>Pengiriman terkait</h2><span>{shipments.length} Surat Jalan</span></div>
      {shipments.length ? <div className="order-v5-related">{shipments.map(shipment => <article key={shipment.id}>
        <div><h3>{shipment.number}</h3><p>{shipment.vehicle} · {dateLabel(shipment.date)}</p><StateBadge>{shipmentStatus(shipment)}</StateBadge></div>
        <div className="order-v5-actions">{canOpenDeliveries && <Button variant="outline" size="sm" onClick={() => go("deliveries", shipment.id)}>{actor.role === "pic" && shipment.status === "dispatched" ? "Konfirmasi penerimaan" : "Buka pengiriman"}<ArrowRight data-icon="inline-end"/></Button>}<Document kind="shipment" id={shipment.id}>Surat Jalan</Document></div>
      </article>)}</div> : <p className="order-v5-empty-note">Surat Jalan tersedia setelah toko mencadangkan dan menyiapkan barang.</p>}
    </section>
    {canSeeBilling && <section className="panel"><div className="panel-title"><div><h2>Invoice terkait</h2><p>Nilai pesanan ini dipisahkan dari saldo seluruh invoice.</p></div></div>
      {invoices.length ? <div className="order-v5-related">{invoices.map(item => <article key={item.invoice.id} className="order-v5-invoice">
        <div className="order-v5-invoice-heading"><div><h3>{item.invoice.number}</h3><p>Jatuh tempo {dateLabel(item.invoice.dueDate)}</p></div><StateBadge>{invoiceStatus(s, item.invoice)}</StateBadge></div>
        <dl><div><dt>Nilai pesanan ini</dt><dd>{money(item.orderAmount)}</dd></div><div><dt>Total seluruh invoice</dt><dd>{money(item.total)}</dd></div><div><dt>Sudah dialokasikan</dt><dd>{money(item.allocated)}</dd></div>{item.credited > 0 && <div><dt>Nota kredit disetujui</dt><dd>{money(item.credited)}</dd></div>}<div><dt>Sisa seluruh invoice</dt><dd>{money(item.balance)}</dd></div></dl>
        {item.shared && <p className="order-v5-shared-note">Invoice ini juga memuat pesanan lain dari pembeli yang sama. Pembayaran dan nota kredit berlaku pada seluruh invoice.</p>}
        <div className="order-v5-actions"><Button variant="outline" size="sm" onClick={() => go("billing", item.invoice.id)}>Buka tagihan<ArrowRight data-icon="inline-end"/></Button><Document kind="invoice" id={item.invoice.id}>Dokumen invoice</Document></div>
      </article>)}</div> : <p className="order-v5-empty-note">Belum ada invoice untuk pesanan ini. Bagian Penagihan menerbitkannya setelah penerimaan difinalisasi.</p>}
    </section>}
    {["pic", "kepala"].includes(actor.role) && !["cancelled", "rejected"].includes(order.status) && lines.some(line => lineProgress(s, line).remaining > 0) && <div className="order-v5-cancel"><Button variant="outline" onClick={() => ask({title: "Batalkan sisa pesanan", description: "Hanya kuantitas yang belum dikirim yang dibatalkan. Riwayat tetap tersimpan.", type: "order.cancel", data: {id}, fields: [{key: "reason", label: "Alasan pembatalan", type: "textarea"}], label: "Batalkan sisa"})}>Batalkan sisa pesanan</Button></div>}
  </div>;
}
