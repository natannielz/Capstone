import type { Actor } from "./accounts";
import type { State } from "./model";
import { canOpenPage, type WorkspacePage } from "./navigation";
import { invoiceBalance, money, sum } from "./selectors";
import { matchesOrderQueue } from "./order-views";

export type DashboardDestination = { page: WorkspacePage; id?: string; query?: Record<string, string | null> };
type Queue = DashboardDestination & { label: string; value: string | number; link: string; icon: "users"|"building"|"package"|"truck"|"check"|"orders"|"wallet"|"calendar"|"shopping" };
type Priority = DashboardDestination & { title: string; description: string; action: string; count: number; unit: string; waiting: boolean };
const orderQuery = (queue: string | null) => ({ queue, q: null, orderPage: null });

/** Presentation-only priorities; destinations retain the workspace role guard. */
export function dashboardView({ s, actor }: { s: State; actor: Actor }) {
  const role = actor.role;
  const pending = s.orders.filter(order => order.status === "submitted").length;
  const preparation = s.orders.filter(order => matchesOrderQueue(s, order, "preparation")).length;
  const picActions = s.orders.filter(order => matchesOrderQueue(s, order, "needs-pic")).length;
  const ready = s.shipments.filter(shipment => shipment.status === "ready");
  const transit = s.shipments.filter(shipment => shipment.status === "dispatched");
  const openInvoices = s.invoices.filter(invoice => invoiceBalance(s, invoice.id) > 0)
    .sort((a, b) => a.dueDate.localeCompare(b.dueDate) || a.number.localeCompare(b.number));
  const approvals = s.credits.filter(item => item.status === "requested").length + s.refunds.filter(item => item.status === "requested").length + s.expenses.filter(item => item.status === "requested").length;
  const funding = s.purchases.filter(item => item.status === "draft" && item.fundingStatus === "requested").length;
  const reviewPeriods = s.periods.filter(period => period.status === "review").length;
  const approvedPeriods = s.periods.filter(period => period.status === "approved").length;
  const openPeriods = s.periods.filter(period => period.status === "open").length;
  const recordedPayments = s.payments.filter(payment => payment.status === "recorded").length;
  const billable = s.shipmentLines.filter(line => line.finalized && line.accepted > 0 && !s.invoiceLines.some(invoiceLine => invoiceLine.shipmentLineId === line.id)).length;
  const finance = ["laporan", "penagihan", "pimpinan", "akuntansi"].includes(role);
  let priority: Priority;
  switch (role) {
    case "pic":
      priority = picActions ? { title: "Ada pesanan yang menunggu Anda", description: "Tinjau barang pengganti atau konfirmasikan barang yang sudah diterima divisi.", action: "Tinjau pesanan", count: picActions, unit: "pesanan perlu tindakan", waiting: true, page: "orders", query: orderQuery("needs-pic") }
        : { title: "Siapkan kebutuhan divisi Anda", description: "Pilih barang, tentukan jumlah, lalu ajukan pesanan untuk ditinjau toko.", action: "Buka katalog divisi", count: s.orders.length, unit: "pesanan divisi tercatat", waiting: false, page: "catalog" };
      break;
    case "kepala":
      priority = pending ? { title: "Pesanan baru siap ditinjau", description: "Periksa kebutuhan pembeli sebelum pesanan diteruskan ke persiapan barang.", action: "Tinjau pesanan masuk", count: pending, unit: "pesanan menunggu tinjauan", waiting: true, page: "orders", query: orderQuery("review") }
        : { title: "Pantau kesiapan persediaan", description: "Tinjau kondisi stok dan kebutuhan pembelian untuk menjaga pemenuhan pesanan.", action: "Periksa persediaan", count: preparation, unit: "pesanan dalam persiapan", waiting: false, page: "stock" };
      break;
    case "staf":
      priority = ready.length ? { title: "Pengiriman siap diberangkatkan", description: "Periksa surat jalan, kurir, dan barang sebelum mencatat keberangkatan.", action: "Buka pengiriman siap", count: ready.length, unit: "surat jalan siap", waiting: true, page: "deliveries", query: { status: "ready" } }
        : preparation ? { title: "Siapkan pesanan berikutnya", description: "Periksa stok dan reservasi barang sebelum membuat surat jalan.", action: "Buka antrean persiapan", count: preparation, unit: "pesanan perlu disiapkan", waiting: true, page: "orders", query: orderQuery("preparation") }
          : { title: "Persiapan pesanan sudah tertangani", description: "Lihat kondisi barang, batch, dan persediaan yang perlu diperiksa.", action: "Periksa persediaan", count: s.products.filter(product => product.active).length, unit: "barang aktif dalam katalog", waiting: false, page: "stock" };
      break;
    case "kurir": {
      const next = transit[0] || ready[0];
      priority = next ? { title: transit.length ? "Lanjutkan tugas pengantaran" : "Tugas pengiriman sudah siap", description: transit.length ? "Catat penerima dan bukti pengantaran pada surat jalan yang ditugaskan kepada Anda." : "Periksa alamat, kendaraan, dan rincian barang sebelum berangkat.", action: "Buka tugas berikutnya", count: transit.length || ready.length, unit: transit.length ? "pengiriman dalam perjalanan" : "pengiriman siap berangkat", waiting: true, page: "deliveries", id: next.id }
        : { title: "Belum ada tugas pengiriman aktif", description: "Tugas baru akan tampil setelah Staf Toko menugaskan surat jalan kepada Anda.", action: "Lihat riwayat pengiriman", count: s.shipments.filter(shipment => shipment.status === "received").length, unit: "pengiriman selesai", waiting: false, page: "deliveries", query: { status: "received" } };
      break;
    }
    case "penagihan":
      priority = recordedPayments ? { title: "Dana masuk menunggu pemeriksaan", description: "Cocokkan bukti dan identitas pembayar sebelum mengalokasikan dana ke invoice.", action: "Periksa pembayaran", count: recordedPayments, unit: "transfer menunggu verifikasi", waiting: true, page: "payments", query: { paymentStatus: "recorded", paymentSearch: null, paymentPage: null } }
        : billable ? { title: "Penerimaan barang siap ditagih", description: "Pilih penerimaan yang sudah final untuk digabungkan menjadi invoice pembeli.", action: "Siapkan invoice", count: billable, unit: "baris penerimaan siap ditagih", waiting: true, page: "billing" }
          : { title: "Tinjau saldo tagihan pembeli", description: "Periksa jatuh tempo dan sisa piutang setelah pembayaran terverifikasi.", action: "Buka invoice belum lunas", count: openInvoices.length, unit: "invoice belum lunas", waiting: openInvoices.length > 0, page: "billing", query: { balance: "open" } };
      break;
    case "pimpinan":
      priority = approvals ? { title: "Pengajuan menunggu keputusan", description: "Tinjau nota kredit, pengembalian saldo, dan penggantian biaya beserta alasannya.", action: "Tinjau pengajuan", count: approvals, unit: "pengajuan menunggu keputusan", waiting: true, page: "payments" }
        : funding ? { title: "Kebutuhan dana perlu ditinjau", description: "Periksa pengadaan Pasar Kering sebelum menyetujui alokasi dana pembelian.", action: "Tinjau kebutuhan dana", count: funding, unit: "pengajuan dana pembelian", waiting: true, page: "procurement" }
          : { title: reviewPeriods ? "Laporan siap mendapat keputusan" : "Pantau laporan Unit Toko", description: reviewPeriods ? "Periksa versi laporan sebelum memberi persetujuan atau catatan perbaikan." : "Lihat penjualan, posisi keuangan, dan riwayat aktivitas yang tercatat.", action: reviewPeriods ? "Tinjau laporan bulanan" : "Buka laporan", count: reviewPeriods, unit: "laporan menunggu tinjauan", waiting: reviewPeriods > 0, page: reviewPeriods ? "periods" : "reports" };
      break;
    case "akuntansi":
      priority = { title: approvedPeriods ? "Periode siap ditutup" : "Tinjau pembukuan Unit Toko", description: approvedPeriods ? "Laporan telah disetujui. Periksa versi dan snapshot sebelum melakukan posting akhir." : "Periksa keseimbangan neraca dan jurnal sebelum tahap penutupan periode.", action: approvedPeriods ? "Periksa periode disetujui" : "Buka laporan keuangan", count: approvedPeriods, unit: "periode telah disetujui", waiting: approvedPeriods > 0, page: approvedPeriods ? "periods" : "reports", query: approvedPeriods ? undefined : { reportTab: "finance" } };
      break;
    case "laporan":
      priority = { title: openPeriods ? "Siapkan laporan untuk ditinjau" : "Rekap transaksi yang sudah final", description: openPeriods ? "Tinjau transaksi dan catatan perbaikan sebelum mengajukan versi laporan bulanan." : "Telusuri penjualan berdasarkan tanggal finalisasi atau buka posisi keuangan pada tanggal pilihan.", action: openPeriods ? "Buka laporan bulanan" : "Buka rekap penjualan", count: openPeriods, unit: "periode masih terbuka", waiting: openPeriods > 0, page: openPeriods ? "periods" : "reports", query: openPeriods ? undefined : { reportTab: "sales" } };
      break;
    case "admin":
      priority = { title: "Kelola akses dan data organisasi", description: "Atur akun, divisi, dan pemasok. Hak akses operasional mengikuti peran masing-masing akun.", action: "Buka pengaturan akun", count: s.users.filter(user => user.active).length, unit: "akun mempunyai akses aktif", waiting: false, page: "admin", query: { adminSection: "users", accountQ: null, accountRole: null, accountStatus: null, accountPage: null } };
      break;
    default:
      return null;
  }

  const queues: Queue[] = role === "admin" ? [
    { label: "Pengguna aktif", value: s.users.filter(user => user.active).length, page: "admin", query: { adminSection: "users", accountQ: null, accountRole: null, accountStatus: null, accountPage: null }, icon: "users", link: "Kelola akun" },
    { label: "Divisi terdaftar", value: s.divisions.length, page: "admin", query: { adminSection: "divisions" }, icon: "building", link: "Lihat pengaturan divisi" },
    { label: "Pemasok terdaftar", value: s.suppliers.length, page: "admin", query: { adminSection: "suppliers" }, icon: "package", link: "Lihat pengaturan pemasok" },
  ] : role === "kurir" ? [
    { label: "Siap diberangkatkan", value: ready.length, page: "deliveries", icon: "package", query: { status: "ready" }, link: "Lihat surat jalan" },
    { label: "Dalam perjalanan", value: transit.length, page: "deliveries", icon: "truck", query: { status: "dispatched" }, link: "Lihat pengantaran" },
    { label: "Pengiriman selesai", value: s.shipments.filter(shipment => shipment.status === "received").length, page: "deliveries", icon: "check", query: { status: "received" }, link: "Lihat riwayat" },
  ] : finance ? [
    { label: "Invoice belum lunas", value: openInvoices.length, page: "billing", icon: "orders", query: { balance: "open" }, link: "Lihat invoice" },
    { label: "Sisa piutang pembeli", value: money(sum(openInvoices.map(invoice => invoiceBalance(s, invoice.id)))), page: "billing", icon: "wallet", query: { balance: "open" }, link: "Lihat rincian piutang" },
    { label: "Periode belum ditutup", value: s.periods.filter(period => period.status !== "closed").length, page: "periods", icon: "calendar", link: "Lihat periode" },
  ] : [
    { label: role === "pic" ? "Pesanan divisi" : role === "staf" ? "Perlu disiapkan" : "Menunggu tinjauan", value: role === "pic" ? s.orders.length : role === "staf" ? preparation : pending, page: "orders", icon: "shopping", query: orderQuery(role === "kepala" ? "review" : role === "staf" ? "preparation" : null), link: "Lihat pesanan" },
    { label: "Pengiriman aktif", value: ready.length + transit.length, page: "deliveries", icon: "truck", query: { status: "active" }, link: "Lihat pengiriman" },
    { label: role === "staf" ? "Barang aktif" : "Invoice belum lunas", value: role === "staf" ? s.products.filter(product => product.active).length : openInvoices.length, page: role === "staf" ? "stock" : "billing", icon: role === "staf" ? "package" : "wallet", query: role === "staf" ? undefined : { balance: "open" }, link: role === "staf" ? "Lihat persediaan" : "Lihat invoice" },
  ];
  return { priority, queues: queues.filter(queue => canOpenPage(role, queue.page)), finance, openInvoices, activeShipments: [...transit, ...ready] };
}
