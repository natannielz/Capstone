import type { SubmittedOrder } from './order-submit-client';

/** One short status per order, as marketplace order lists show it, plus a sentence on what happens next.
 *  Derived only from the verified progress the server returns; delivery and payment stay in the order timeline. */
export function buyerOrderStatus(order: Pick<SubmittedOrder, 'submission' | 'progress'>): { label: string; detail: string } {
  const { progress } = order, parts = order.submission.suborderIds.length;
  if (progress.cancelled) return { label: 'Dibatalkan', detail: 'Sisa pesanan dibatalkan dan cadangan barang yang belum dikirim dilepas.' };
  if (progress.spendingState === 'Rejected') return { label: 'Ditolak', detail: 'Penyetuju cabang menolak belanja ini. Cadangan barang telah dilepas.' };
  if (progress.spendingState === 'Pending') return { label: 'Menunggu persetujuan', detail: 'Menunggu persetujuan belanja dari penyetuju cabang.' };
  if (progress.rejectedSuborders.length === parts) return { label: 'Ditolak', detail: 'Toko tidak dapat memenuhi pesanan ini. Cadangan barang telah dilepas.' };
  if (progress.rejectedSuborders.length > 0) return { label: 'Sebagian ditolak', detail: 'Sebagian pesanan tidak dapat dipenuhi toko; bagian lainnya tetap diproses.' };
  if (progress.acceptedSuborders.length === parts) return { label: 'Diproses', detail: 'Toko menerima pesanan dan sedang menyiapkan barang.' };
  return { label: 'Menunggu toko', detail: 'Pesanan tercatat dan menunggu diterima toko.' };
}
