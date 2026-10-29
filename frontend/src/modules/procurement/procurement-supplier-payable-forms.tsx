'use client';

import type { SupplierPayableCapacity, SupplierPayablePolicy, SupplierPayablePreview } from './procurement-supplier-payable-client';
import styles from '../administration/access.module.css';
import { dateTimeFormatter, formatMoneyText, formatDate, formatMonth } from '@/lib/format';
import { Facts } from '@/components/workspace-kit/facts';

export const payableKinds = { recognize: 'Pengakuan utang', apply_advance: 'Penerapan uang muka' };
export const payableStates = { proposed: 'Menunggu pemeriksaan independen', approved: 'Disetujui dan dibukukan', rejected: 'Ditolak tanpa pembukuan' };
export const payableWhen = (value: string) => dateTimeFormatter.format(new Date(value));
export const payableNotice = 'Pengajuan belum mencadangkan nilai. Persetujuan hanya mencatat pembukuan yang ditinjau; tidak mengirim uang atau melepas barang.';

export function PayableCapacityFacts({ value, historical = false }: { value: SupplierPayableCapacity; historical?: boolean }) {
  return <section aria-label={historical ? 'Sisa pada pengajuan' : 'Sisa nilai pada pembacaan terakhir'}><h3>{historical ? 'Sisa pada pengajuan' : 'Sisa nilai pada pembacaan terakhir'}</h3>{'recognized' in value ? <Facts items={[{ term: 'Nilai penilaian disetujui', value: formatMoneyText(value.amount) }, { term: 'Pengakuan utang', value: value.recognized ? 'Sudah diakui' : 'Belum diakui' }]} /> : <Facts items={[{ term: 'Utang diakui', value: formatMoneyText(value.payable.totalAmount) }, { term: 'Uang muka diterapkan ke utang', value: formatMoneyText(value.payable.appliedAmount) }, { term: 'Sisa utang', value: formatMoneyText(value.payable.remainingAmount) }, { term: 'Uang muka terverifikasi', value: formatMoneyText(value.advance.totalAmount) }, { term: 'Uang muka sudah diterapkan', value: formatMoneyText(value.advance.appliedAmount) }, ...('returnedAmount' in value.advance ? [{ term: 'Uang muka dikembalikan pemasok', value: formatMoneyText(value.advance.returnedAmount) }] : []), { term: 'Sisa uang muka', value: formatMoneyText(value.advance.remainingAmount) }]} />}</section>;
}

export function PayablePolicyFacts({ value }: { value: SupplierPayablePolicy }) {
  return <Facts items={[{ term: 'Persetujuan kebijakan', value: <span className={styles.preserve}>{value.approvalReference}</span> }, { term: 'Penanggung jawab', value: <span className={styles.preserve}>{value.operatingOwnerReference}</span> }, { term: 'Masa berlaku kebijakan', value: <>{payableWhen(value.approvedAt)} – {payableWhen(value.expiresAt)}</> }, { term: 'Akun akrual penerimaan', value: <>{value.receiptAccrual.accountCode} · {value.receiptAccrual.mappingKey}</> }, { term: 'Akun utang pemasok', value: <>{value.supplierPayable.accountCode} · {value.supplierPayable.mappingKey}</> }, { term: 'Akun uang muka pemasok', value: <>{value.supplierAdvance.accountCode} · {value.supplierAdvance.mappingKey}</> }]} />;
}

export function PayableFacts({ value, historical = false, rejected = false }: { value: Pick<SupplierPayablePreview, 'input' | 'source' | 'capacity' | 'policy' | 'accounting' | 'amount'>; historical?: boolean; rejected?: boolean }) {
  const { source, input, policy, accounting } = value;
  return <div aria-label="Fakta pembukuan yang ditinjau"><Facts items={[{ term: 'Tindakan', value: payableKinds[input.kind] }, { term: 'Pemasok', value: source.supplierName }, { term: 'Referensi pesanan pembelian', value: <span className={styles.preserve}>{source.commercialReference}</span> }, { term: 'Invoice pemasok', value: <span className={styles.preserve}>{source.invoiceDocumentReference}</span> }, { term: 'Nilai pembukuan', value: formatMoneyText(value.amount) }, { term: 'Tanggal pembukuan', value: formatDate(input.postingDate) }, { term: 'Alasan pengajuan', value: <span className={styles.preserve}>{input.reason}</span> }]} /><PayableCapacityFacts value={value.capacity} historical={historical} />
    <p>{rejected ? 'Penolakan tidak mencatat jurnal atau menerapkan uang muka. Sumber dan sisa nilai tetap memerlukan tindak lanjut.' : source.kind === 'recognize' ? `Debit akrual penerimaan dan kredit utang pemasok sebesar ${formatMoneyText(value.amount)}. Utang tetap terutang sampai ada penerapan atau penyelesaian yang sah.` : `Debit utang pemasok dan kredit uang muka sebesar ${formatMoneyText(value.amount)}. Hanya bagian ini yang diterapkan; sisa utang dan uang muka tetap dicatat.`}</p>
    <details><summary>Sumber dan kebijakan pembukuan</summary><PayablePolicyFacts value={policy} /><dl className={styles.facts}>
      <div><dt>Buku / versi bagan akun</dt><dd>{accounting.bookId} / {accounting.chartVersionId}</dd></div><div><dt>Periode pembukuan</dt><dd>{formatMonth(accounting.periodMonth)}</dd></div><div><dt>Alokasi penilaian sumber</dt><dd>{source.valuationAllocationId}</dd></div>
      {source.kind === 'recognize' ? <><div><dt>Kemasan pada alokasi</dt><dd>{source.packages}</dd></div><div><dt>Nilai neto / pajak / biaya</dt><dd>{formatMoneyText(source.netAmount)} / {formatMoneyText(source.taxAmount)} / {formatMoneyText(source.chargeAmount)}</dd></div><div><dt>Jurnal penilaian</dt><dd>{source.valuationJournalId}</dd></div><div><dt>Penerimaan / dokumen invoice</dt><dd>{source.receiptId} / {source.invoiceDocumentId}</dd></div><div><dt>Pencocokan invoice / baris</dt><dd>{source.invoiceMatchId} / {source.matchLineNumber}</dd></div></> : <><div><dt>Utang / verifikasi debit</dt><dd>{source.payableId} / {source.settlementId}</dd></div><div><dt>Catatan uang muka</dt><dd>{source.disbursementId}</dd></div><div><dt>Jurnal verifikasi debit</dt><dd>{source.settlementJournalId}</dd></div></>}
    </dl></details>
  </div>;
}
