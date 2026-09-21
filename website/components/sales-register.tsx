"use client";

import { FileDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Empty, EmptyHeader, EmptyTitle, EmptyDescription } from "@/components/ui/empty";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { get, money, sum, today } from "@/lib/domain/selectors";
import { salesCSV, salesRows, validReportDate } from "@/lib/domain/operations-views";
import type { WorkspaceContext } from "./workspace";
import type { OperationsQueryProps } from "./inventory-workspace";
import { DocumentLink } from "./operations";

export function SalesRegister({ s, query, onQueryChange }: WorkspaceContext & OperationsQueryProps) {
  const start = validReportDate(query.get("salesStart"), today().slice(0, 7) + "-01");
  const end = validReportDate(query.get("salesEnd"), today());
  const category = ["OMI", "Smart"].includes(query.get("salesSource") || "") ? query.get("salesSource")! : "all";
  const division = s.divisions.some(d => d.id === query.get("salesDivision")) ? query.get("salesDivision")! : "all";
  const filters = { start, end, category, division }, validRange = start <= end;
  const rows = validRange ? salesRows(s, filters) : [];
  const pages = Math.max(1, Math.ceil(rows.length / 12));
  const page = Math.min(Math.max(1, Math.floor(Number(query.get("salesPage")) || 1)), pages);
  const shown = rows.slice((page - 1) * 12, page * 12), total = sum(rows.map(({ line }) => line.accepted * line.price));
  const groups = s.divisions.filter(d => division === "all" || d.id === division).map(d => ({ name: d.name, value: sum(rows.filter(row => row.order.divisionId === d.id).map(({ line }) => line.accepted * line.price)) }));
  const max = Math.max(1, ...groups.map(group => group.value));
  const change = (key: string, value: string) => onQueryChange({ [key]: value === "all" ? null : value, salesPage: null });
  function download() {
    if (!validRange) return;
    const url = URL.createObjectURL(new Blob([salesCSV(s, filters)], { type: "text/csv;charset=utf-8" }));
    const anchor = document.createElement("a"); anchor.href = url; anchor.download = `penjualan-${start}-${end}.csv`; anchor.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  const invoiceLink = (shipmentLineId: string) => {
    const invoiceLine = s.invoiceLines.find(line => line.shipmentLineId === shipmentLineId);
    return invoiceLine ? <DocumentLink kind="invoice" id={invoiceLine.invoiceId}>{get(s.invoices, invoiceLine.invoiceId).number}</DocumentLink> : <span className="muted">Siap ditagih</span>;
  };
  return <section className="panel sales-register-v6"><div className="panel-title"><div><h2>Penjualan final</h2><p>Transaksi berdasarkan tanggal finalisasi penerimaan, sebelum nota kredit dan pajak invoice.</p></div><Button variant="outline" disabled={!validRange || !rows.length} onClick={download}><FileDown data-icon="inline-start"/>CSV sesuai filter</Button></div>
    <FieldGroup className="ops-filters ops-filters-four"><Field data-invalid={!validRange}><FieldLabel htmlFor="sales-start">Dari tanggal finalisasi</FieldLabel><Input id="sales-start" type="date" value={start} aria-invalid={!validRange} aria-describedby={!validRange ? "sales-range-error" : undefined} onChange={event => change("salesStart", event.target.value)}/></Field><Field data-invalid={!validRange}><FieldLabel htmlFor="sales-end">Sampai tanggal finalisasi</FieldLabel><Input id="sales-end" type="date" value={end} aria-invalid={!validRange} aria-describedby={!validRange ? "sales-range-error" : undefined} onChange={event => change("salesEnd", event.target.value)}/></Field><Field><FieldLabel htmlFor="sales-source">Sumber barang</FieldLabel><select id="sales-source" className="native-select" value={category} onChange={event => change("salesSource", event.target.value)}><option value="all">OMI & Smart</option><option>OMI</option><option>Smart</option></select></Field><Field><FieldLabel htmlFor="sales-division">Divisi</FieldLabel><select id="sales-division" className="native-select" value={division} onChange={event => change("salesDivision", event.target.value)}><option value="all">Semua divisi</option>{s.divisions.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field></FieldGroup>
    {!validRange && <p id="sales-range-error" role="alert" className="ops-inline-error">Tanggal akhir harus sama atau setelah tanggal awal.</p>}
    <div className="ops-results-heading"><p role="status">{rows.length} baris penjualan sesuai filter</p><strong>{money(total)}</strong></div>
    {!!rows.length && <div className="sales-bars" aria-label="Nilai penjualan per divisi">{groups.map(group => <div key={group.name}><span>{group.name}</span><div aria-hidden="true"><i style={{ width: `${group.value / max * 100}%` }}/></div><b>{money(group.value)}</b></div>)}</div>}
    {!!rows.length && <><div className="ops-desktop-table"><table className="data-table"><thead><tr><th>Tanggal / Surat Jalan</th><th>Divisi / barang</th><th>Jumlah final</th><th className="numeric">Nilai</th><th>Invoice</th></tr></thead><tbody>{shown.map(({ line, shipment, order, product, date }) => <tr key={line.id}><td>{date}<small>{shipment.number}</small></td><td><strong>{product.name}</strong><small>{product.sku} · {product.category}</small><small>{get(s.divisions, order.divisionId).name}</small></td><td>{line.accepted} {product.unit}</td><td className="numeric">{money(line.accepted * line.price)}</td><td>{invoiceLink(line.id)}</td></tr>)}</tbody></table></div><div className="ops-mobile-list">{shown.map(({ line, shipment, order, product, date }) => <article key={line.id} className="ops-record"><div className="ops-record-heading"><div><h3>{product.name}</h3><p>{product.sku} · {product.category}</p></div><strong>{money(line.accepted * line.price)}</strong></div><dl className="ops-facts"><div><dt>Finalisasi</dt><dd>{date}</dd></div><div><dt>Jumlah final</dt><dd>{line.accepted} {product.unit}</dd></div><div><dt>Divisi</dt><dd>{get(s.divisions, order.divisionId).name}</dd></div><div><dt>Surat Jalan</dt><dd>{shipment.number}</dd></div></dl>{invoiceLink(line.id)}</article>)}</div></>}
    {!rows.length && validRange && <Empty><EmptyHeader><EmptyTitle>Belum ada penjualan pada pilihan ini</EmptyTitle><EmptyDescription>Ubah rentang tanggal, sumber barang, atau divisi. Penerimaan yang belum difinalisasi belum masuk laporan penjualan.</EmptyDescription></EmptyHeader></Empty>}
    {!!rows.length && <div className="ops-pagination"><p>CSV memuat seluruh {rows.length} baris sesuai filter.</p><div><Button variant="outline" disabled={page === 1} onClick={() => onQueryChange({ salesPage: String(page - 1) })}>Sebelumnya</Button><span>Halaman {page} / {pages}</span><Button variant="outline" disabled={page === pages} onClick={() => onQueryChange({ salesPage: String(page + 1) })}>Berikutnya</Button></div></div>}
  </section>;
}
