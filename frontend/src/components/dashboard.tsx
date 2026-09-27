"use client";

import { ArrowUpRight, ShoppingBag, ClipboardList, Truck, WalletCards, Package, CalendarCheck, Users, Building2, ArrowRight, CheckCheck } from "lucide-react";
import { buyerLabel } from "@/lib/domain/buyers";
import { canOpenPage } from "@/lib/domain/navigation";
import { invoiceBalance, money, today } from "@/lib/domain/selectors";
import { dashboardView, type DashboardDestination } from "@/lib/domain/dashboard-views";
import { ROLE_LABELS } from "@/lib/domain/accounts";
import type { WorkspaceContext } from "./workspace";
import { OrderList } from "./order-experience";
import { Button } from "@/components/ui/button";

const queueIcons = { users: Users, building: Building2, package: Package, truck: Truck, check: CheckCheck, orders: ClipboardList, wallet: WalletCards, calendar: CalendarCheck, shopping: ShoppingBag };
const orderQuery = (queue: string | null) => ({ queue, q: null, orderPage: null });
const dateLabel = (date: string) => new Date(date + "T12:00:00+07:00").toLocaleDateString("id-ID", { day: "numeric", month: "short", year: "numeric", timeZone: "Asia/Jakarta" });

export function Dashboard(ctx: WorkspaceContext) {
  const { s, actor, go } = ctx;
  const view = dashboardView(ctx);
  if (!view || !canOpenPage(actor.role, view.priority.page)) return null;
  const { priority, queues, finance, openInvoices, activeShipments } = view;
  const open = (destination: DashboardDestination) => go(destination.page, destination.id, destination.query);
  return <div className="dashboard-v14">
    <section className="work-priority" aria-labelledby="work-priority-title" data-waiting={priority.waiting}>
      <div className="work-priority-copy">
        <p className="work-priority-label">{ROLE_LABELS[actor.role]} <span>/</span> Pekerjaan berikutnya</p>
        <h2 id="work-priority-title">{priority.title}</h2>
        <p>{priority.description}</p>
        <Button className="work-priority-action" onClick={() => open(priority)}>{priority.action}<ArrowRight data-icon="inline-end" aria-hidden="true"/></Button>
      </div>
      <div className="work-priority-total"><strong>{priority.count}</strong><span>{priority.unit}</span></div>
    </section>
    <section className="dashboard-overview" aria-labelledby="dashboard-overview-title">
      <div className="dashboard-section-heading"><h2 id="dashboard-overview-title">Sekilas aktivitas</h2><span>Data sesuai akses Anda</span></div>
      <div className="dashboard-queues">{queues.map(queue => { const Icon = queueIcons[queue.icon]; return <button className="queue-card" key={queue.label} onClick={() => open(queue)}>
        <div className="queue-label"><span>{queue.label}</span><Icon size={19} aria-hidden="true"/></div><strong>{queue.value}</strong><span className="queue-link">{queue.link}<ArrowUpRight size={15} aria-hidden="true"/></span>
      </button>;})}</div>
    </section>
    {!["admin", "kurir"].includes(actor.role) && !finance && <section className="panel">
      <div className="panel-title"><div><h2>Pesanan terbaru</h2><p>Lima pesanan terakhir, termasuk riwayat penyelesaiannya.</p></div><button className="text-link" onClick={() => go("orders", undefined, orderQuery(null))}>Semua pesanan<ArrowUpRight size={15} aria-hidden="true"/></button></div>
      <OrderList s={s} orders={[...s.orders].reverse().slice(0, 5)} onSelect={id => go("orders", id)}/>
    </section>}
    {finance && <section className="panel dashboard-invoices">
      <div className="panel-title"><div><h2>Tagihan yang perlu ditindaklanjuti</h2><p>Invoice belum lunas, diurutkan dari jatuh tempo paling awal.</p></div><button className="text-link" onClick={() => go("billing", undefined, { balance: "open" })}>Semua invoice<ArrowUpRight size={15} aria-hidden="true"/></button></div>
      {!!openInvoices.length && <div className="table-scroll"><table className="data-table"><caption className="sr-only">Lima invoice belum lunas dengan jatuh tempo paling awal</caption><thead><tr><th scope="col">Invoice / pembeli</th><th scope="col">Jatuh tempo</th><th scope="col" className="numeric">Sisa tagihan</th><th scope="col"><span className="sr-only">Tindakan</span></th></tr></thead><tbody>{openInvoices.slice(0, 5).map(invoice => <tr key={invoice.id}>
        <td><strong>{invoice.number}</strong><small>{buyerLabel(s, invoice)}</small></td><td><span className="dashboard-due-date">{dateLabel(invoice.dueDate)}</span>{invoice.dueDate < today() && <small className="dashboard-overdue">Lewat jatuh tempo</small>}</td><td className="numeric">{money(invoiceBalance(s, invoice.id))}</td><td><Button variant="outline" size="sm" aria-label={`Buka invoice ${invoice.number}`} onClick={() => go("billing", invoice.id)}>Buka invoice<ArrowRight data-icon="inline-end" aria-hidden="true"/></Button></td>
      </tr>)}</tbody></table></div>}
      {!openInvoices.length && <div className="dashboard-empty"><CheckCheck size={24} aria-hidden="true"/><div><h3>{s.invoices.length ? "Seluruh invoice sudah lunas" : "Belum ada invoice tercatat"}</h3><p>{s.invoices.length ? "Tagihan baru akan tampil setelah invoice diterbitkan." : "Invoice akan tampil setelah penerimaan barang difinalisasi dan ditagihkan."}</p></div></div>}
    </section>}
    {actor.role === "kurir" && <section className="panel dashboard-deliveries">
      <div className="panel-title"><div><h2>Penugasan pengiriman</h2><p>Pengiriman dalam perjalanan ditampilkan lebih dahulu.</p></div><button className="text-link" onClick={() => go("deliveries", undefined, { status: "active" })}>Semua tugas<ArrowUpRight size={15} aria-hidden="true"/></button></div>
      {activeShipments.length ? <ul className="dashboard-task-list">{activeShipments.map(shipment => <li key={shipment.id}><span className="dashboard-task-icon"><Truck size={21} aria-hidden="true"/></span><div><h3>{shipment.number}</h3><p>{s.orders.find(order => order.id === shipment.orderId)?.address || "Alamat tersedia pada surat jalan"}</p><span className="dashboard-task-status">{shipment.status === "dispatched" ? "Dalam perjalanan" : "Siap diberangkatkan"}</span></div><Button variant="outline" size="sm" aria-label={`Buka tugas ${shipment.number}`} onClick={() => go("deliveries", shipment.id)}>Buka tugas<ArrowRight data-icon="inline-end" aria-hidden="true"/></Button></li>)}</ul>
        : <div className="dashboard-empty"><CheckCheck size={24} aria-hidden="true"/><div><h3>Tidak ada pengiriman aktif</h3><p>Staf Toko akan menugaskan surat jalan baru. Pengiriman terdahulu tetap tersedia di riwayat.</p></div></div>}
    </section>}
  </div>;
}
