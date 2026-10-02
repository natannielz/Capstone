export function recoveryPage(operation: string) {
  if (/^organization\.address\.(propose|approve|reject)$(?![\s\S])/.test(operation)) return { href: '/administration/organizations/addresses', label: 'halaman alamat organisasi' };
  if (operation === 'billing.refund.payout.verify') return { href: '/fulfillment/cases', label: 'halaman verifikasi debit refund' };
  if (operation.startsWith('billing.refund.payout.') || operation === 'evidence.refund-payout-image.reserve') return { href: '/fulfillment/cases', label: 'halaman pencatatan bukti refund' };
  if (operation === 'billing.return.financial.approve') return { href: '/fulfillment/cases', label: 'halaman persetujuan Finance retur' };
  if (operation === 'billing.return.entitlement.request') return { href: '/fulfillment/cases', label: 'halaman pengajuan nilai finansial retur' };
  if (operation === "billing.payment.allocation.correct") return { href: "/billing/payment-reviews", label: "halaman koreksi alokasi pembayaran" };
  if (operation === "billing.payment.allocate") return { href: "/billing/payment-reviews", label: "halaman alokasi pembayaran" };
  if (operation === "billing.bank-receipt.confirm") return { href: "/billing/bank-receipts", label: "halaman penerimaan bank" };
  if (operation === "billing.bank-statement.stage") return { href: "/billing/bank-statements", label: "halaman mutasi bank" };
  if (operation.startsWith("billing.receiving-account.")) return { href: "/billing/receiving-accounts", label: "halaman rekening penerimaan" };
  if (operation === "billing.payment.verify") return { href: "/billing/payment-reviews", label: "halaman pemeriksaan pembayaran" };
  if (operation === "billing.payment.review.correct") return { href: "/billing/payment-reviews", label: "halaman pemeriksaan pembayaran" };
  if (operation === "billing.payment.review.manual") return { href: "/billing/payment-reviews", label: "halaman pengecualian OCR" };
  if (operation === "billing.payment.review.manage") return { href: "/billing/payment-reviews", label: "halaman tindak lanjut pemeriksaan" };
  if (operation === 'billing.payment.submit' || operation === 'billing.payment.draft.save' || operation === 'evidence.payment-image.reserve') return { href: '/billing/payments', label: 'halaman bukti pembayaran' };
  if (operation === 'billing.payment.replacement.create') return { href: '/billing/payments', label: 'halaman bukti pengganti' };
  if (operation === 'billing.invoice.issue') return { href: '/billing/invoices', label: 'halaman tagihan' };
  if (operation === 'fulfillment.case.request') return { href: '/fulfillment/cases', label: 'halaman keluhan barang' };
  if (operation === 'fulfillment.case.decide') return { href: '/fulfillment/cases', label: 'halaman pemeriksaan keluhan' };
  if (operation === 'fulfillment.case.return_receive') return { href: '/fulfillment/cases', label: 'halaman penerimaan retur' };
  if (operation === 'fulfillment.case.return_inspect') return { href: '/fulfillment/cases', label: 'halaman pemeriksaan barang retur' };
  if (operation === 'fulfillment.case.resolution_request') return { href: '/fulfillment/cases', label: 'halaman pengajuan penyelesaian retur' };
  if (operation === 'fulfillment.delivery-attempt.record') return { href: '/fulfillment/shipments', label: 'halaman upaya pengiriman' };
  if (operation === 'fulfillment.receipt-proof.issue') return { href: '/fulfillment/receive', label: 'halaman bukti penerimaan' };
  if (operation === 'fulfillment.receipt.challenge.issue') return { href: '/fulfillment/shipments', label: 'QR penerimaan di rincian kiriman' };
  if (operation === 'fulfillment.receipt.confirm') return { href: '/fulfillment/receive', label: 'halaman penerimaan kiriman' };
  if (operation === 'fulfillment.surat-jalan.issue') return { href: '/fulfillment/shipments', label: 'rincian kiriman dan Surat Jalan' };
  if (operation === 'fulfillment.shipment.dispatch') return { href: '/fulfillment/dispatches', label: 'halaman keberangkatan kiriman' };
  if (operation === 'fulfillment.preparation.save') return { href: '/fulfillment/preparations', label: 'halaman persiapan kiriman' };
  if (['ordering.order.spending.approve', 'ordering.order.spending.reject', 'ordering.order.store.accept', 'ordering.order.store.reject', 'ordering.order.cancel'].includes(operation)) return { href: '/ordering/reviews', label: 'halaman peninjauan pesanan' };
  if (operation === 'ordering.draft.save') return { href: '/ordering/drafts', label: 'halaman draf pesanan' };
  if (operation === 'ordering.order.submit') return { href: '/ordering/checkout', label: 'halaman pengajuan pesanan' };
  if (/^catalog\.price\.(propose|approve|reject|withdraw)$(?![\s\S])/.test(operation)) return { href: "/catalog/prices", label: "halaman harga kemasan" };
  if (/^stock\.source\.policy\.(propose|approve|reject|withdraw)$(?![\s\S])/.test(operation)) return { href: "/inventory/sources/policies", label: "halaman kebijakan operasional stok" };
  if (operation === "stock.source.suspend") return { href: "/inventory/sources/suspensions", label: "halaman penghentian sumber" };
  if (/^stock\.source\.alias\.(propose|approve|reject|withdraw)$/.test(operation)) return { href: "/inventory/sources/aliases", label: "halaman pemetaan kanal" };
  if (/^stock\.source\.revision\.(propose|approve|reject|withdraw)$/.test(operation)) return { href: "/inventory/sources/revisions", label: "halaman perubahan sumber stok" };
  if (/^stock\.source\.(propose|approve|reject|withdraw)$/.test(operation)) return { href: "/inventory/sources", label: "halaman sumber stok" };
  if (operation === "inventory.stock-count.record") return { href: "/inventory/counts", label: "halaman hitung barang" };
  return operation.startsWith("catalog.") ? { href: "/administration/catalog", label: "halaman katalog" }
    : { href: "/administration/person-reviews", label: "halaman peninjauan orang" };
}
export function operationLabel(operation: string) {
  if (operation === 'organization.address.propose') return 'Pengajuan alamat organisasi';
  if (operation === 'organization.address.approve') return 'Persetujuan alamat organisasi';
  if (operation === 'organization.address.reject') return 'Penolakan alamat organisasi';
  if (operation === 'billing.refund.payout.verify') return 'Verifikasi debit bank refund';
  if (operation === 'billing.refund.payout.draft') return 'Penyimpanan draf bukti refund';
  if (operation === 'billing.refund.payout.record') return 'Pencatatan refund belum diverifikasi';
  if (operation === 'evidence.refund-payout-image.reserve') return 'Persiapan unggah bukti refund';
  if (operation === 'billing.return.financial.approve') return 'Persetujuan Finance retur';
  if (operation === 'billing.return.entitlement.request') return 'Pengajuan nilai finansial retur';
  if (operation === "billing.payment.allocation.correct") return "Koreksi alokasi pembayaran";
  if (operation === 'fulfillment.case.resolution_request') return 'Pengajuan penyelesaian retur';
  if (operation === 'fulfillment.case.return_inspect') return 'Pemeriksaan barang retur';
  if (operation === 'fulfillment.case.decide') return 'Keputusan pengembalian barang';
  if (operation === "billing.payment.allocate") return "Alokasi pembayaran";
  if (operation === "billing.bank-receipt.confirm") return "Konfirmasi kredit bank";
  if (operation === "billing.bank-statement.stage") return "Impor mutasi bank";
  if (operation.startsWith("billing.receiving-account.")) return "Perubahan rekening penerimaan";
  if (operation === "billing.payment.verify") return "Verifikasi pembayaran";
  if (operation === "billing.payment.review.correct") return "Koreksi pemeriksaan pembayaran";
  if (operation === "billing.payment.review.manual") return "Pemeriksaan manual gangguan OCR";
  if (operation === "billing.payment.review.manage") return "Tindak lanjut pemeriksaan pembayaran";
  if (operation === 'billing.payment.submit') return 'Pengajuan bukti pembayaran';
  if (operation === 'billing.payment.replacement.create') return 'Pembuatan draf bukti pengganti';
  if (operation === 'billing.payment.draft.save') return 'Penyimpanan draf pembayaran';
  if (operation === 'evidence.payment-image.reserve') return 'Persiapan unggah bukti pembayaran';
  if (operation === 'billing.invoice.issue') return 'Penerbitan invoice';
  if (operation === 'fulfillment.case.request') return 'Pengajuan keluhan barang';
  if (operation === 'fulfillment.delivery-attempt.record') return 'Pencatatan upaya pengiriman';
  if (operation === 'fulfillment.receipt-proof.issue') return 'Penerbitan bukti penerimaan';
  if (operation === 'fulfillment.receipt.challenge.issue') return 'Pembuatan QR penerimaan';
  if (operation === 'fulfillment.receipt.confirm') return 'Konfirmasi penerimaan kiriman';
  if (operation === 'fulfillment.surat-jalan.issue') return 'Penerbitan Surat Jalan';
  if (operation === 'fulfillment.shipment.dispatch') return 'Keberangkatan kiriman';
  if (operation === 'fulfillment.preparation.save') return 'Penyimpanan persiapan kiriman';
  if (operation === 'ordering.order.spending.reject') return 'Penolakan belanja cabang';
  if (operation === 'ordering.order.store.reject') return 'Penolakan pesanan toko';
  if (operation === 'ordering.order.cancel') return 'Pembatalan sisa pesanan';
  if (operation === 'ordering.order.spending.approve') return 'Persetujuan belanja cabang';
  if (operation === 'ordering.order.store.accept') return 'Penerimaan pesanan toko';
  if (operation === 'ordering.draft.save') return 'Penyimpanan draf pesanan';
  if (operation === 'ordering.order.submit') return 'Pengajuan pesanan cabang';
  if (operation.startsWith("catalog.price.")) return "Pengajuan atau keputusan harga kemasan";
  const sources: Readonly<Record<string, string>> = {
    "stock.source.policy.propose": "Pengajuan kebijakan operasional stok", "stock.source.policy.approve": "Persetujuan kebijakan operasional stok",
    "stock.source.policy.reject": "Penolakan kebijakan operasional stok", "stock.source.policy.withdraw": "Penarikan kebijakan operasional stok",
    "stock.source.suspend": "Penghentian sumber stok",
    "stock.source.alias.propose": "Pengajuan pemetaan kanal", "stock.source.alias.approve": "Persetujuan pemetaan kanal",
    "stock.source.alias.reject": "Penolakan pemetaan kanal", "stock.source.alias.withdraw": "Penarikan pemetaan kanal",
    "stock.source.revision.propose": "Pengajuan perubahan sumber stok", "stock.source.revision.approve": "Persetujuan perubahan sumber stok",
    "stock.source.revision.reject": "Penolakan perubahan sumber stok", "stock.source.revision.withdraw": "Penarikan perubahan sumber stok", "stock.source.propose": "Pengajuan sumber stok", "stock.source.approve": "Persetujuan sumber stok",
    "stock.source.reject": "Penolakan sumber stok", "stock.source.withdraw": "Penarikan pengajuan sumber stok" };
  if (sources[operation]) return sources[operation];
  if (operation === "inventory.stock-count.record") return "Pengamatan hitungan barang";
  if (operation.startsWith("catalog.")) return "Perubahan katalog";
  if (operation.startsWith("person.review.")) return "Peninjauan keterkaitan akun";
  if (operation.startsWith("organization.")) return "Perubahan struktur organisasi";
  if (operation.includes("membership")) return "Perubahan keanggotaan";
  if (operation.includes("scope")) return "Perubahan status unit";
  if (operation.includes("identity")) return "Perubahan status akun";
  return "Perubahan akses";
}
