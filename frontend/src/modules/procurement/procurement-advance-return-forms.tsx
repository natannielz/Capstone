'use client';

import type { AdvanceReturnPolicy, AdvanceReturnPreview } from './procurement-advance-return-client';
import styles from '../administration/access.module.css';
import { dateTimeFormatter, formatMoneyText, formatDate, formatMonth } from '@/lib/format';
import { Facts } from '@/components/workspace-kit/facts';

export const advanceReturnStates = { proposed: 'Menunggu pemeriksaan independen', returned: 'Pengembalian dibukukan', rejected: 'Ditolak tanpa pembukuan' };
export const advanceReturnWhen = (value: string) => dateTimeFormatter.format(new Date(value));
export const advanceReturnConsent = 'Saya telah memeriksa seluruh kredit bank, pemasok, pesanan pembelian, rincian uang muka dan kebijakan pembukuan.';
export const advanceReturnNotice = 'Satu kredit bank harus seluruhnya untuk satu pemasok dan satu pesanan pembelian. Beberapa uang muka terverifikasi dari pesanan yang sama dapat dipilih. Pengajuan belum mencadangkan nilai; persetujuan mencatat uang yang sudah diterima.';
export function advanceReturnCents(value: string): bigint | null {
  return /^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$/.test(value) ? BigInt(value.replace('.', '')) : null;
}
export function advanceReturnMoney(value: bigint) { return `${value / BigInt(100)}.${(value % BigInt(100)).toString().padStart(2, '0')}`; }

export function AdvanceReturnPolicyFacts({ value }: { value: AdvanceReturnPolicy }) {
  return <Facts items={[{ term: 'Persetujuan kebijakan', value: <span className={styles.preserve}>{value.approvalReference}</span> }, { term: 'Penanggung jawab', value: <span className={styles.preserve}>{value.operatingOwnerReference}</span> }, { term: 'Masa berlaku kebijakan', value: <>{advanceReturnWhen(value.approvedAt)} – {advanceReturnWhen(value.expiresAt)}</> }, { term: 'Akun penerimaan belum dialokasikan', value: <>{value.suspense.accountCode} · {value.suspense.mappingKey}</> }, { term: 'Akun uang muka pemasok', value: <>{value.supplierAdvance.accountCode} · {value.supplierAdvance.mappingKey}</> }]} />;
}

export function AdvanceReturnFacts({ value, historical = false, rejected = false }: { value: Pick<AdvanceReturnPreview, 'input' | 'source' | 'capacity' | 'policy' | 'accounting' | 'amount'>; historical?: boolean; rejected?: boolean }) {
  const { input, source, accounting } = value;
  return <div aria-label="Fakta pengembalian yang ditinjau"><Facts items={[{ term: 'Pemasok', value: source.supplierName }, { term: 'Referensi pesanan pembelian', value: <span className={styles.preserve}>{source.commercialReference}</span> }, { term: 'Seluruh kredit bank', value: formatMoneyText(source.receiptAmount) }, { term: 'Tanggal kredit bank', value: formatDate(source.bookedDate) }, { term: 'Referensi bank', value: <span className={styles.preserve}>{source.bankReference ?? 'Tidak tersedia'}</span> }, { term: 'Tanggal pembukuan', value: formatDate(input.postingDate) }, { term: 'Alasan pengajuan', value: <span className={styles.preserve}>{input.reason}</span> }]} />
    <section aria-label={historical ? 'Rincian pada pengajuan' : 'Rincian pada peninjauan'}><h3>{historical ? 'Rincian pada pengajuan' : 'Rincian pada peninjauan'}</h3>{historical && <p>Sisa di bawah ini adalah catatan saat pengajuan dibuat. Lihat sumber terbaru sebelum tindakan berikutnya.</p>}<ul className={styles.historyList}>{source.lines.map((line, at) => { const capacity = value.capacity.lines[at].advance; return <li key={line.settlementId}><h4>Uang muka terverifikasi {line.settlementId}</h4><Facts items={[{ term: 'Nilai uang muka', value: formatMoneyText(line.settlementAmount) }, { term: 'Sudah diterapkan ke utang', value: formatMoneyText(capacity.appliedAmount) }, { term: 'Sudah dikembalikan pemasok', value: formatMoneyText('returnedAmount' in capacity ? capacity.returnedAmount : '0.00') }, { term: 'Sisa yang tersedia saat ditinjau', value: formatMoneyText(capacity.remainingAmount) }, { term: 'Nilai pengembalian', value: formatMoneyText(line.returnAmount) }]} /></li>; })}</ul></section>
    <p>{rejected ? 'Penolakan ini tidak mencatat jurnal pengembalian. Kredit bank dan uang muka tetap memerlukan tindak lanjut.' : `Debit penerimaan belum dialokasikan dan kredit uang muka pemasok sebesar ${formatMoneyText(value.amount)}. Tindakan ini tidak menjalankan transfer bank.`}</p>
    <details><summary>Sumber dan kebijakan pembukuan</summary><AdvanceReturnPolicyFacts value={value.policy} /><Facts items={[{ term: 'Referensi penerimaan bank', value: source.receiptId }, { term: 'Buku / versi bagan akun', value: <>{accounting.bookId} / {accounting.chartVersionId}</> }, { term: 'Periode pembukuan', value: formatMonth(accounting.periodMonth) }]} /></details>
  </div>;
}
