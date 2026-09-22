"use client";

import { type ReactNode } from "react";
import { PackageSearch, Plus, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { InputGroup, InputGroupAddon, InputGroupInput } from "@/components/ui/input-group";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { get, money, stocktakeStatus, sum } from "@/lib/domain/selectors";
import { inventoryView, matchesInventoryBatch, matchesInventoryProduct, readInventoryQuery, type InventoryBatch, type InventoryProduct } from "@/lib/domain/operations-views";
import type { Batch, Product, StockReturn, Stocktake } from "@/lib/domain/model";
import type { QueryPatch } from "./use-workspace-navigation";
import { Status, type WorkspaceContext } from "./workspace";

export type OperationsQueryProps = { query: URLSearchParams; onQueryChange: (patch: QueryPatch) => void };
const pageSize = 12;
const tabLabels = { products: "Produk", batches: "Batch", returns: "Retur", stocktakes: "Opname" };
const stocktakeLabels = { requested: "Menunggu persetujuan", approved: "Disetujui", stale: "Perlu hitung ulang", superseded: "Digantikan", cancelled: "Dibatalkan" };

function EmptyResults({ filtered, reset, children }: { filtered: boolean; reset: () => void; children: ReactNode }) {
  return <Empty className="ops-empty"><EmptyHeader><PackageSearch aria-hidden="true"/><EmptyTitle>{filtered ? "Tidak ada hasil yang cocok" : "Belum ada catatan"}</EmptyTitle><EmptyDescription>{filtered ? "Coba kata kunci lain atau hapus filter untuk melihat semua data." : children}</EmptyDescription></EmptyHeader>{filtered && <Button variant="outline" onClick={reset}>Hapus filter</Button>}</Empty>;
}

function InventoryTable({ heads, children }: { heads: string[]; children: ReactNode }) {
  return <div className="inventory-desktop"><table className="data-table"><thead><tr>{heads.map(head => <th key={head}>{head}</th>)}</tr></thead><tbody>{children}</tbody></table></div>;
}

function ProductConditions({ row }: { row: InventoryProduct }) {
  return <div className="ops-status-list">{!row.product.active && <Badge variant="outline">Tidak dijual</Badge>}{row.product.active && row.available <= row.product.minimum && <Badge variant="outline">Perlu restok</Badge>}{row.expired > 0 && <Badge variant="outline">Ada kedaluwarsa</Badge>}{row.expiring > 0 && <Badge variant="outline">Menjelang kedaluwarsa</Badge>}{row.held > 0 && <Badge variant="outline">Ada barang ditahan</Badge>}{row.product.active && row.available > row.product.minimum && !row.expired && !row.expiring && !row.held && <span className="muted">Stok cukup</span>}</div>;
}

function BatchConditions({ row }: { row: InventoryBatch }) {
  return <div className="ops-status-list">{row.expired && row.batch.qty > 0 ? <Badge variant="outline">Kedaluwarsa</Badge> : row.expiring && row.batch.qty > 0 ? <Badge variant="outline">Tinjau retur</Badge> : null}{row.batch.held > 0 && <Badge variant="outline">Ditahan: {row.batch.held}</Badge>}{row.batch.qty === 0 && <span className="muted">Batch kosong</span>}{!row.expired && !row.expiring && row.batch.held === 0 && row.batch.qty > 0 && <span className="muted">Layak jual</span>}</div>;
}

function editProduct(ctx: WorkspaceContext, product: Product) {
  ctx.ask({ title: "Pengaturan barang", description: "Barang yang dinonaktifkan tidak menerima pesanan baru. Pesanan sebelumnya tetap dapat diselesaikan dengan harga yang sudah disepakati.", type: "product.update", data: { id: product.id }, fields: [
    { key: "price", label: "Harga jual (Rp)", type: "number", value: product.price },
    { key: "minimum", label: "Stok minimum", type: "number", min: 0, value: product.minimum },
    { key: "returnMonths", label: "Pengingat sebelum kedaluwarsa (bulan)", type: "number", min: 0, max: 36, value: product.returnMonths },
    { key: "active", label: "Status penjualan", type: "select", value: String(product.active), options: [{ value: "true", label: "Aktif — dapat dipesan" }, { value: "false", label: "Nonaktif — tidak dijual" }] },
  ], map: values => ({ price: Number(values.price), minimum: Number(values.minimum), returnMonths: Number(values.returnMonths), active: values.active === "true" }) });
}

function createStocktake(ctx: WorkspaceContext, batch: Batch, replacesId?: string) {
  ctx.ask({ title: replacesId ? "Hitung ulang stok" : "Catat stok opname", description: replacesId ? "Penghitungan baru menggantikan pengajuan lama. Kepala Toko meninjau selisih sebelum stok disesuaikan." : "Catat hasil pemeriksaan fisik. Kepala Toko meninjau selisih sebelum stok disesuaikan.", type: "stocktake.create", data: { batchId: batch.id, ...(replacesId ? { replacesId } : {}) }, fields: [
    { key: "counted", label: "Jumlah fisik setelah hitung ulang", type: "number", value: batch.qty, min: 0 },
    { key: "reason", label: replacesId ? "Alasan dan hasil penghitungan ulang" : "Penjelasan hasil pemeriksaan", type: "textarea" },
  ] });
}

function BatchActions({ ctx, row }: { ctx: WorkspaceContext; row: InventoryBatch }) {
  const { s, actor, ask } = ctx, { batch, product } = row;
  const heldInReturns = sum(s.stockReturns.filter(r => r.batchId === batch.id && (r.status === "quarantined" || (r.status === "rejected" && !r.released))).map(r => r.qty));
  const free = batch.qty - batch.held - row.reserved, held = batch.held - heldInReturns;
  return <div className="ops-row-actions">{actor.role === "staf" && <>
    <Button size="sm" variant="outline" disabled={free <= 0 && held <= 0} onClick={() => ask({ title: "Pisahkan barang retur", description: `${product.name} · ${batch.code}. Barang ditahan sampai keputusan pemasok tercatat.`, type: "stock.returnCreate", data: { batchId: batch.id }, fields: [
      { key: "fromHeld", label: "Sumber barang", type: "select", value: free > 0 ? "false" : "true", options: [...(free > 0 ? [{ value: "false", label: `Stok belum ditahan · ${free} ${product.unit}` }] : []), ...(held > 0 ? [{ value: "true", label: `Karantina dari divisi · ${held} ${product.unit}` }] : [])] },
      { key: "qty", label: `Jumlah retur (${product.unit})`, type: "number", min: 1, max: Math.max(free, held) },
      { key: "reason", label: "Alasan / hasil pemeriksaan", type: "textarea" },
    ], map: values => ({ qty: Number(values.qty), reason: values.reason, fromHeld: values.fromHeld === "true" }) })}>Retur</Button>
    <Button size="sm" variant="outline" onClick={() => createStocktake(ctx, batch)}>Opname</Button>
  </>}{actor.role === "kepala" && <Button size="sm" variant="outline" onClick={() => editProduct(ctx, product)}>Atur barang</Button>}{actor.role === "laporan" && <span className="muted">Akses lihat</span>}</div>;
}

function ReturnRecord({ ctx, record }: { ctx: WorkspaceContext; record: StockReturn }) {
  const { s, actor, ask } = ctx, batch = get(s.batches, record.batchId), product = get(s.products, batch.productId);
  const text = record.released ? "Dilepas layak jual" : { quarantined: "Menunggu pemasok", accepted: "Diterima pemasok", rejected: "Ditolak pemasok" }[record.status];
  return <article className="ops-record"><div className="ops-record-heading"><div><h3>{product.name}</h3><p>{product.sku} · {batch.code} · {record.date}</p></div><Status text={text}/></div><dl className="ops-facts"><div><dt>Jumlah retur</dt><dd>{record.qty} {product.unit}</dd></div><div><dt>Lokasi batch</dt><dd>{batch.location}</dd></div></dl><p className="ops-record-note">{record.reason}</p>{record.note && <p className="ops-record-note">{record.note}</p>}<div className="ops-row-actions">{record.status === "quarantined" && actor.role === "staf" && [true, false].map(accept => <Button key={String(accept)} variant="outline" onClick={() => ask({ title: accept ? "Pemasok menerima retur" : "Pemasok menolak retur", description: accept ? "Stok keluar dan klaim pemasok dicatat. Penyelesaian klaim mengikuti konfirmasi pemasok." : "Barang tetap ditahan sampai kelayakannya ditinjau.", type: "stock.returnResolve", data: { id: record.id, accept }, fields: [{ key: "note", label: "Hasil konfirmasi pemasok", type: "textarea" }] })}>{accept ? "Catat diterima" : "Catat ditolak"}</Button>)}{record.status === "rejected" && !record.released && actor.role === "kepala" && <Button variant="outline" onClick={() => ask({ title: "Tinjau kelayakan barang", description: "Barang kedaluwarsa tidak dapat dilepas untuk dijual.", type: "stock.holdRelease", data: { id: record.id }, fields: [{ key: "reason", label: "Hasil pemeriksaan kelayakan", type: "textarea" }] })}>Lepas layak jual</Button>}</div></article>;
}

function StocktakeRecord({ ctx, record }: { ctx: WorkspaceContext; record: Stocktake }) {
  const { s, actor, ask } = ctx, batch = get(s.batches, record.batchId), product = get(s.products, batch.productId), status = stocktakeStatus(s, record);
  const open = status === "requested" || status === "stale", canCancel = actor.role === "kepala" || (actor.role === "staf" && record.requestedBy === actor.id);
  return <article className="ops-record"><div className="ops-record-heading"><div><h3>{product.name}</h3><p>{product.sku} · {batch.code} · {record.date}</p></div><Status text={stocktakeLabels[status]}/></div><dl className="ops-facts"><div><dt>Sistem saat diajukan</dt><dd>{record.expected} {product.unit}</dd></div><div><dt>Hasil hitung</dt><dd>{record.counted} {product.unit}</dd></div><div><dt>Fisik sistem sekarang</dt><dd>{batch.qty} {product.unit}</dd></div></dl><p className="ops-record-note">{record.reason}</p>{status === "stale" && <p className="ops-record-note">Stok telah berubah. Ajukan penghitungan baru agar penyesuaian memakai keadaan terbaru.</p>}{record.resolutionReason && <p className="ops-record-note">{record.resolutionReason}</p>}{record.supersededBy && <p className="ops-record-note">Penghitungan pengganti tercatat{ s.stocktakes.find(x => x.id === record.supersededBy)?.date ? ` pada ${s.stocktakes.find(x => x.id === record.supersededBy)?.date}` : ""}.</p>}{record.replacesId && <p className="ops-record-note">Menggantikan pengajuan sebelumnya pada batch ini.</p>}<div className="ops-row-actions">{status === "requested" && actor.role === "kepala" && <Button onClick={() => ask({ title: "Setujui selisih opname", description: `Selisih ${record.counted - record.expected} ${product.unit} akan dicatat beserta jurnal penyesuaian.`, type: "stocktake.approve", data: { id: record.id }, fields: [] })}>Setujui penyesuaian</Button>}{open && actor.role === "staf" && <Button variant="outline" onClick={() => createStocktake(ctx, batch, record.id)}>Hitung ulang</Button>}{open && canCancel && <Button variant="outline" onClick={() => ask({ title: "Batalkan pengajuan opname", description: "Riwayat penghitungan dipertahankan. Pembatalan tidak mengubah jumlah stok.", type: "stocktake.cancel", data: { id: record.id }, fields: [{ key: "reason", label: "Alasan pembatalan", type: "textarea" }], label: "Batalkan opname" })}>Batalkan pengajuan</Button>}</div></article>;
}

export function InventoryWorkspace({ query, onQueryChange, ...ctx }: WorkspaceContext & OperationsQueryProps) {
  const { s, actor, ask, go } = ctx, filters = readInventoryQuery(query);
  const view = inventoryView(s);
  const products = view.products.filter(row => matchesInventoryProduct(row, filters));
  const batches = view.batches.filter(row => matchesInventoryBatch(row, filters, view.products));
  const matchingBatches = new Set(batches.map(row => row.batch.id));
  const returns = [...s.stockReturns].reverse().filter(row => matchingBatches.has(row.batchId));
  const stocktakes = [...s.stocktakes].reverse().filter(row => matchingBatches.has(row.batchId));
  const counts = { products: products.length, batches: batches.length, returns: returns.length, stocktakes: stocktakes.length };
  const count = counts[filters.tab], pages = Math.max(1, Math.ceil(count / pageSize)), page = Math.min(filters.page, pages), offset = (page - 1) * pageSize;
  const pending = s.stocktakes.filter(record => ["requested", "stale"].includes(stocktakeStatus(s, record))).length;
  const filtered = !!filters.search || filters.source !== "all" || filters.condition !== "all" || !!filters.productId;
  const clear = () => onQueryChange({ stockSearch: null, stockSource: null, stockCondition: null, stockProduct: null, stockPage: null });
  const showBatch = (product: Product) => go("stock", undefined, { stockTab: "batches", stockProduct: product.id, stockPage: null });
  const productActions = (row: InventoryProduct) => <div className="ops-row-actions"><Button size="sm" variant="outline" onClick={() => showBatch(row.product)}>Lihat batch</Button>{actor.role === "kepala" && <Button size="sm" variant="ghost" onClick={() => editProduct(ctx, row.product)}>Atur barang</Button>}</div>;
  return <Tabs value={filters.tab} onValueChange={value => onQueryChange({ stockTab: value, stockPage: null, stockProduct: null })} className="inventory-v6 ops-workspace">
    <section className="panel"><div className="panel-title"><div><h2>Persediaan barang</h2><p>Stok tersedia sudah dikurangi reservasi, barang ditahan, dan barang kedaluwarsa.</p></div>{actor.role === "kepala" && <Button onClick={() => ask({ title: "Tambah barang", description: "Gunakan satuan transaksi yang konsisten. Stok ditambahkan melalui penerimaan pengadaan.", type: "master.product", fields: [
      { key: "sku", label: "Kode SKU" }, { key: "name", label: "Nama barang" }, { key: "category", label: "Sumber barang", type: "select", value: "OMI", options: [{ value: "OMI", label: "OMI" }, { value: "Smart", label: "Smart" }] }, { key: "unit", label: "Satuan transaksi (pcs / dus / pak)", value: "pcs" }, { key: "price", label: "Harga jual (Rp)", type: "number" }, { key: "minimum", label: "Minimum stok", type: "number", min: 0, value: 5 }, { key: "returnMonths", label: "Pengingat sebelum kedaluwarsa (bulan)", type: "number", min: 0, max: 36, value: 1 },
    ] })}><Plus data-icon="inline-start"/>Tambah barang</Button>}</div>
    <TabsList className="ops-tabs" aria-label="Bagian persediaan">{Object.entries(tabLabels).map(([value, text]) => <TabsTrigger value={value} key={value}>{text}{value === "stocktakes" && pending > 0 && <span className="ops-tab-count" aria-label={`${pending} perlu ditindaklanjuti`}>{pending}</span>}</TabsTrigger>)}</TabsList>
    <FieldGroup className="ops-filters"><Field><FieldLabel htmlFor="inventory-search">{filters.tab === "products" ? "Cari barang" : "Cari barang atau batch"}</FieldLabel><InputGroup><InputGroupAddon><Search aria-hidden="true"/></InputGroupAddon><InputGroupInput id="inventory-search" placeholder={filters.tab === "products" ? "Nama atau SKU barang" : "Nama, SKU, atau kode batch"} value={filters.search} onChange={event => onQueryChange({ stockSearch: event.target.value || null, stockPage: null })}/></InputGroup></Field><Field><FieldLabel htmlFor="inventory-source">Sumber barang</FieldLabel><select id="inventory-source" className="native-select" value={filters.source} onChange={event => onQueryChange({ stockSource: event.target.value === "all" ? null : event.target.value, stockPage: null })}><option value="all">OMI & Smart</option><option value="OMI">OMI</option><option value="Smart">Smart</option></select></Field><Field><FieldLabel htmlFor="inventory-condition">Kondisi</FieldLabel><select id="inventory-condition" className="native-select" value={filters.condition} onChange={event => onQueryChange({ stockCondition: event.target.value === "all" ? null : event.target.value, stockPage: null })}><option value="all">Semua kondisi</option><option value="low">Perlu restok</option><option value="expired">Kedaluwarsa</option><option value="expiring">Menjelang kedaluwarsa</option><option value="held">Ada barang ditahan</option></select></Field></FieldGroup>
    <div className="ops-results-heading"><p role="status">{count} {tabLabels[filters.tab].toLowerCase()} ditemukan{filters.productId && ` untuk ${s.products.find(product => product.id === filters.productId)?.name || "barang terpilih"}`}</p>{filtered && <Button variant="ghost" size="sm" onClick={clear}>Hapus filter</Button>}</div>
    <TabsContent value="products"><InventoryTable heads={["Barang", "Fisik / reservasi", "Tersedia", "Kondisi", "Harga jual", "Tindakan"]}>{products.slice(offset, offset + pageSize).map(row => <tr key={row.product.id}><td><strong>{row.product.name}</strong><small>{row.product.sku} · {row.product.category} · {row.product.unit}</small></td><td>{row.physical} / {row.reserved}<small>Ditahan {row.held}</small></td><td><strong>{row.available} {row.product.unit}</strong><small>Minimum {row.product.minimum}</small></td><td><ProductConditions row={row}/></td><td className="numeric">{money(row.product.price)}</td><td>{productActions(row)}</td></tr>)}</InventoryTable><div className="inventory-mobile">{products.slice(offset, offset + pageSize).map(row => <article className="ops-record" key={row.product.id}><div className="ops-record-heading"><div><h3>{row.product.name}</h3><p>{row.product.sku} · {row.product.category}</p></div><div className="inventory-available"><span>Tersedia</span><strong>{row.available} <small>{row.product.unit}</small></strong></div></div><ProductConditions row={row}/><dl className="ops-facts"><div><dt>Fisik / reservasi</dt><dd>{row.physical} / {row.reserved}</dd></div><div><dt>Harga jual</dt><dd>{money(row.product.price)}</dd></div></dl>{productActions(row)}</article>)}</div></TabsContent>
    <TabsContent value="batches"><InventoryTable heads={["Barang / batch", "Kedaluwarsa", "Fisik / reservasi", "Tersedia", "Tindakan"]}>{batches.slice(offset, offset + pageSize).map(row => <tr key={row.batch.id}><td><strong>{row.product.name}</strong><small>{row.product.sku} · {row.batch.code}</small><small>{row.batch.location}</small></td><td>{row.batch.expiry || "Tidak ditetapkan"}<BatchConditions row={row}/></td><td>{row.batch.qty} / {row.reserved}<small>Ditahan {row.batch.held}</small></td><td><strong>{row.available} {row.product.unit}</strong></td><td><BatchActions ctx={ctx} row={row}/></td></tr>)}</InventoryTable><div className="inventory-mobile">{batches.slice(offset, offset + pageSize).map(row => <article className="ops-record" key={row.batch.id}><div className="ops-record-heading"><div><h3>{row.product.name}</h3><p>{row.product.sku} · {row.batch.code}</p></div><div className="inventory-available"><span>Tersedia</span><strong>{row.available} <small>{row.product.unit}</small></strong></div></div><BatchConditions row={row}/><dl className="ops-facts"><div><dt>Kedaluwarsa</dt><dd>{row.batch.expiry || "Tidak ditetapkan"}</dd></div><div><dt>Lokasi</dt><dd>{row.batch.location}</dd></div><div><dt>Fisik / reservasi</dt><dd>{row.batch.qty} / {row.reserved}</dd></div><div><dt>Ditahan</dt><dd>{row.batch.held} {row.product.unit}</dd></div></dl><BatchActions ctx={ctx} row={row}/></article>)}</div></TabsContent>
    <TabsContent value="returns"><div className="ops-record-list">{returns.slice(offset, offset + pageSize).map(record => <ReturnRecord key={record.id} ctx={ctx} record={record}/>)}</div></TabsContent>
    <TabsContent value="stocktakes"><div className="ops-record-list">{stocktakes.slice(offset, offset + pageSize).map(record => <StocktakeRecord key={record.id} ctx={ctx} record={record}/>)}</div></TabsContent>
    {!count && <EmptyResults filtered={filtered} reset={clear}>{filters.tab === "products" ? "Tambahkan barang untuk memulai persediaan." : filters.tab === "batches" ? "Penerimaan pengadaan akan menambahkan batch barang di sini." : filters.tab === "returns" ? "Pisahkan barang melalui tindakan Retur pada batch yang perlu diperiksa." : "Staf Toko dapat mencatat hasil pemeriksaan melalui tindakan Opname pada batch."}</EmptyResults>}
    {count > 0 && <div className="ops-pagination"><p>Menampilkan {offset + 1}–{Math.min(offset + pageSize, count)} dari {count}</p><div><Button variant="outline" disabled={page <= 1} onClick={() => onQueryChange({ stockPage: String(page - 1) })}>Sebelumnya</Button><span>Halaman {page} / {pages}</span><Button variant="outline" disabled={page >= pages} onClick={() => onQueryChange({ stockPage: String(page + 1) })}>Berikutnya</Button></div></div>}
    </section>
  </Tabs>;
}
