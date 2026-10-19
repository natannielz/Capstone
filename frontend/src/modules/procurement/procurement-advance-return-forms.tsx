'use client';

import type { AdvanceReturnPolicy, AdvanceReturnPreview } from './procurement-advance-return-client';
import styles from '../administration/access.module.css';
import { dateTimeFormatter, formatMoneyText } from '@/lib/format';

export const advanceReturnStates = { proposed: 'Menunggu pemeriksaan independen', returned: 'Pengembalian dibukukan', rejected: 'Ditolak tanpa pembukuan' };
export const advanceReturnWhen = (value: string) => dateTimeFormatter.format(new Date(value));
export const advanceReturnConsent = 'Saya telah memeriksa seluruh kredit bank, pemasok, pesanan pembelian, rincian uang muka dan kebijakan pembukuan.';
export const advanceReturnNotice = 'Satu kredit bank harus seluruhnya untuk satu pemasok dan satu pesanan pembelian. Beberapa uang muka terverifikasi dari pesanan yang sama dapat dipilih. Pengajuan belum mencadangkan nilai; persetujuan mencatat uang yang sudah diterima.';
export function advanceReturnCents(value: string): bigint | null {
  return /^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$/.test(value) ? BigInt(value.replace('.', '')) : null;
}
export function advanceReturnMoney(value: bigint) { return `${value / BigInt(100)}.${(value % BigInt(100)).toString().padStart(2, '0')}`; }

export function AdvanceReturnPolicyFacts({ value }: { value: AdvanceReturnPolicy }) {
  return <dl className={styles.facts}><div><dt>Persetujuan kebijakan</dt><dd className={styles.preserve}>{value.approvalReference}</dd></div><div><dt>Penanggung jawab</dt><dd className={styles.preserve}>{value.operatingOwnerReference}</dd></div><div><dt>Masa berlaku kebijakan</dt><dd>{advanceReturnWhen(value.approvedAt)} – {advanceReturnWhen(value.expiresAt)}</dd></div><div><dt>Akun penerimaan belum dialokasikan</dt><dd>{value.suspense.accountCode} · {value.suspense.mappingKey}</dd></div><div><dt>Akun uang muka pemasok</dt><dd>{value.supplierAdvance.accountCode} · {value.supplierAdvance.mappingKey}</dd></div></dl>;
}

export function AdvanceReturnFacts({ value, historical = false, rejected = false }: { value: Pick<AdvanceReturnPreview, 'input' | 'source' | 'capacity' | 'policy' | 'accounting' | 'amount'>; historical?: boolean; rejected?: boolean }) {
  const { input, source, accounting } = value;
  return <div aria-label="Fakta pengembalian yang ditinjau"><dl className={styles.facts}><div><dt>Pemasok</dt><dd>{source.supplierName}</dd></div><div><dt>Referensi pesanan pembelian</dt><dd className={styles.preserve}>{source.commercialReference}</dd></div><div><dt>Seluruh kredit bank</dt><dd>{formatMoneyText(source.receiptAmount)}</dd></div><div><dt>Tanggal kredit bank</dt><dd>{source.bookedDate}</dd></div><div><dt>Referensi bank</dt><dd className={styles.preserve}>{source.bankReference ?? 'Tidak tersedia'}</dd></div><div><dt>Tanggal pembukuan</dt><dd>{input.postingDate}</dd></div><div><dt>Alasan pengajuan</dt><dd className={styles.preserve}>{input.reason}</dd></div></dl>
    <section aria-label={historical ? 'Rincian pada pengajuan' : 'Rincian pada peninjauan'}><h3>{historical ? 'Rincian pada pengajuan' : 'Rincian pada peninjauan'}</h3>{historical && <p>Sisa di bawah ini adalah catatan saat pengajuan dibuat. Lihat sumber terbaru sebelum tindakan berikutnya.</p>}<ul className={styles.historyList}>{source.lines.map((line, at) => { const capacity = value.capacity.lines[at].advance; return <li key={line.settlementId}><h4>Uang muka terverifikasi {line.settlementId}</h4><dl className={styles.facts}><div><dt>Nilai uang muka</dt><dd>{formatMoneyText(line.settlementAmount)}</dd></div><div><dt>Sudah diterapkan ke utang</dt><dd>{formatMoneyText(capacity.appliedAmount)}</dd></div><div><dt>Sudah dikembalikan pemasok</dt><dd>{formatMoneyText('returnedAmount' in capacity ? capacity.returnedAmount : '0.00')}</dd></div><div><dt>Sisa yang tersedia saat ditinjau</dt><dd>{formatMoneyText(capacity.remainingAmount)}</dd></div><div><dt>Nilai pengembalian</dt><dd>{formatMoneyText(line.returnAmount)}</dd></div></dl></li>; })}</ul></section>
    <p>{rejected ? 'Penolakan ini tidak mencatat jurnal pengembalian. Kredit bank dan uang muka tetap memerlukan tindak lanjut.' : `Debit penerimaan belum dialokasikan dan kredit uang muka pemasok sebesar ${formatMoneyText(value.amount)}. Tindakan ini tidak menjalankan transfer bank.`}</p>
    <details><summary>Sumber dan kebijakan pembukuan</summary><AdvanceReturnPolicyFacts value={value.policy} /><dl className={styles.facts}><div><dt>Referensi penerimaan bank</dt><dd>{source.receiptId}</dd></div><div><dt>Buku / versi bagan akun</dt><dd>{accounting.bookId} / {accounting.chartVersionId}</dd></div><div><dt>Periode pembukuan</dt><dd>{accounting.periodMonth}</dd></div></dl></details>
  </div>;
}
