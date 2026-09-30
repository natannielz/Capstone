const labels: Record<string, string> = {
  "payment.review.correct": "Koreksi pemeriksaan pembayaran dengan verifikasi terbaru",
  "payment.review.read": "Lihat pengajuan dan bukti pembayaran dalam cakupan",
  "person.review.read": "Lihat peninjauan keterkaitan akun", "person.review.propose": "Ajukan penangguhan keterkaitan akun", "person.review.approve": "Putuskan penangguhan keterkaitan akun",
  "catalog.read": "Lihat katalog", "catalog.manage": "Kelola katalog", "pricing.manage": "Kelola harga",
  "order.read": "Lihat pesanan", "order.submit": "Ajukan pesanan", "order.approve": "Setujui pesanan", "order.substitute": "Tanggapi penggantian barang", "order.cancel": "Batalkan pesanan",
  "order.spend.approve": "Setujui belanja pembeli", "order.accept": "Terima pesanan di toko",
  "substitution.propose": "Ajukan penggantian barang", "substitution.accept": "Setujui penggantian barang",
  "stock.source.read": "Lihat sumber stok", "stock.source.propose": "Ajukan pengaturan sumber stok", "stock.source.approve": "Setujui pengaturan sumber stok", "stock.source.suspend": "Hentikan sementara sumber stok",
  "stock.source.policy.propose": "Ajukan kebijakan operasional stok", "stock.source.policy.approve": "Putuskan kebijakan operasional stok",
  "stock.convert": "Konversi kemasan stok", "reservation.release": "Lepaskan reservasi stok",
  "supplier.change.propose": "Ajukan perubahan pemasok", "supplier.change.approve": "Setujui perubahan pemasok",
  "history.read": "Lihat riwayat sesuai lingkup", "report.read": "Lihat laporan sesuai lingkup",
  "branch.pic": "PIC cabang atau divisi", "organization.wide": "Akses seluruh organisasi pembeli",
  "stock.read": "Lihat stok", "stock.receive": "Terima persediaan", "stock.count": "Hitung persediaan", "stock.adjust.propose": "Ajukan penyesuaian stok", "stock.adjust.approve": "Setujui penyesuaian stok",
  "shipment.read": "Lihat pengiriman", "shipment.prepare": "Siapkan pengiriman", "shipment.dispatch": "Kirim barang", "shipment.assign": "Tugaskan pengiriman",
  "delivery.attempt": "Catat perjalanan pengiriman", "delivery.evidence": "Unggah bukti pengiriman", "delivery.return": "Catat barang kembali", "receipt.accept": "Konfirmasi penerimaan",
  "case.read": "Lihat pengaduan", "case.request": "Ajukan pengaduan", "case.manage": "Tangani pengaduan",
  "procurement.read": "Lihat pengadaan", "procurement.prepare": "Siapkan pengadaan", "procurement.approve": "Setujui pengadaan", "supplier.claim": "Ajukan klaim pemasok",
  "source.prepare": "Siapkan data kasir", "source.reconcile": "Rekonsiliasi data kasir", "report.prepare": "Siapkan laporan",
  "period.submit": "Ajukan periode", "period.approve": "Setujui periode", "period.close": "Tutup periode", "period.reopen.request": "Ajukan pembukaan kembali periode", "period.reopen.approve": "Setujui pembukaan kembali periode",
  "invoice.read": "Lihat tagihan", "invoice.issue": "Terbitkan tagihan", "payment.evidence": "Kirim bukti pembayaran", "payment.record": "Catat pembayaran", "payment.allocate": "Alokasikan pembayaran", "payment.verify": "Verifikasi pembayaran secara independen",
  "credit.request": "Ajukan nota kredit", "credit.approve": "Setujui nota kredit", "refund.request": "Ajukan pengembalian dana", "refund.approve": "Setujui pengembalian dana",
  "disbursement.record": "Catat pencairan yang disetujui", "disbursement.verify": "Verifikasi pencairan", "funding.request": "Ajukan pendanaan", "funding.approve": "Setujui pendanaan", "expense.request": "Ajukan biaya", "expense.approve": "Setujui biaya",
  "reconciliation.verify": "Verifikasi rekonsiliasi", "journal.post": "Posting jurnal", "journal.reverse": "Balik jurnal",
  "invite.manage": "Kelola undangan", "membership.propose": "Ajukan keanggotaan", "membership.approve": "Setujui keanggotaan",
  "access.propose": "Ajukan izin akses", "access.approve": "Setujui izin akses", "access.revoke": "Cabut izin akses",
  "recovery.initiate": "Mulai pemulihan akun", "technical.configure": "Kelola pengaturan sistem", "access.audit.read": "Lihat riwayat akses",
};
export function permissionLabel(permission: string, fallback?: string) { return labels[permission] ?? fallback ?? permission; }

export const historicalGrantLabel = "Akses baca riwayat";
export function grantProfileLabel(templateId: string, roles: readonly { id: string; label: string }[]) {
  return templateId === "historical-read" ? historicalGrantLabel : roles.find(role => role.id === templateId)?.label ?? templateId;
}
