'use client';

import type { SupplierPayableCapacity, SupplierPayablePolicy, SupplierPayablePreview } from './procurement-supplier-payable-client';
import styles from '../administration/access.module.css';
import { dateTimeFormatter, formatMoneyText } from '@/lib/format';

export const payableKinds = { recognize: 'Pengakuan utang', apply_advance: 'Penerapan uang muka' };
export const payableStates = { proposed: 'Menunggu pemeriksaan independen', approved: 'Disetujui dan dibukukan', rejected: 'Ditolak tanpa pembukuan' };
export const payableWhen = (value: string) => dateTimeFormatter.format(new Date(value));
export const payableNotice = 'Pengajuan belum mencadangkan nilai. Persetujuan hanya mencatat pembukuan yang ditinjau; tidak mengirim uang atau melepas barang.';

export function PayableCapacityFacts({ value, historical = false }: { value: SupplierPayableCapacity; historical?: boolean }) {
  return <section aria-label={historical ? 'Sisa pada pengajuan' : 'Sisa nilai pada pembacaan terakhir'}><h3>{historical ? 'Sisa pada pengajuan' : 'Sisa nilai pada pembacaan terakhir'}</h3>{'recognized' in value ? <dl className={styles.facts}><div><dt>Nilai penilaian disetujui</dt><dd>{formatMoneyText(value.amount)}</dd></div><div><dt>Pengakuan utang</dt><dd>{value.recognized ? 'Sudah diakui' : 'Belum diakui'}</dd></div></dl> : <dl className={styles.facts}><div><dt>Utang diakui</dt><dd>{formatMoneyText(value.payable.totalAmount)}</dd></div><div><dt>Uang muka diterapkan ke utang</dt><dd>{formatMoneyText(value.payable.appliedAmount)}</dd></div><div><dt>Sisa utang</dt><dd>{formatMoneyText(value.payable.remainingAmount)}</dd></div><div><dt>Uang muka terverifikasi</dt><dd>{formatMoneyText(value.advance.totalAmount)}</dd></div><div><dt>Uang muka sudah diterapkan</dt><dd>{formatMoneyText(value.advance.appliedAmount)}</dd></div>{'returnedAmount' in value.advance && <div><dt>Uang muka dikembalikan pemasok</dt><dd>{formatMoneyText(value.advance.returnedAmount)}</dd></div>}<div><dt>Sisa uang muka</dt><dd>{formatMoneyText(value.advance.remainingAmount)}</dd></div></dl>}</section>;
}

export function PayablePolicyFacts({ value }: { value: SupplierPayablePolicy }) {
  return <dl className={styles.facts}><div><dt>Persetujuan kebijakan</dt><dd className={styles.preserve}>{value.approvalReference}</dd></div><div><dt>Penanggung jawab</dt><dd className={styles.preserve}>{value.operatingOwnerReference}</dd></div><div><dt>Masa berlaku kebijakan</dt><dd>{payableWhen(value.approvedAt)} – {payableWhen(value.expiresAt)}</dd></div><div><dt>Akun akrual penerimaan</dt><dd>{value.receiptAccrual.accountCode} · {value.receiptAccrual.mappingKey}</dd></div><div><dt>Akun utang pemasok</dt><dd>{value.supplierPayable.accountCode} · {value.supplierPayable.mappingKey}</dd></div><div><dt>Akun uang muka pemasok</dt><dd>{value.supplierAdvance.accountCode} · {value.supplierAdvance.mappingKey}</dd></div></dl>;
}

export function PayableFacts({ value, historical = false, rejected = false }: { value: Pick<SupplierPayablePreview, 'input' | 'source' | 'capacity' | 'policy' | 'accounting' | 'amount'>; historical?: boolean; rejected?: boolean }) {
  const { source, input, policy, accounting } = value;
  return <div aria-label="Fakta pembukuan yang ditinjau"><dl className={styles.facts}>
    <div><dt>Tindakan</dt><dd>{payableKinds[input.kind]}</dd></div><div><dt>Pemasok</dt><dd>{source.supplierName}</dd></div><div><dt>Referensi pesanan pembelian</dt><dd className={styles.preserve}>{source.commercialReference}</dd></div><div><dt>Invoice pemasok</dt><dd className={styles.preserve}>{source.invoiceDocumentReference}</dd></div><div><dt>Nilai pembukuan</dt><dd>{formatMoneyText(value.amount)}</dd></div><div><dt>Tanggal pembukuan</dt><dd>{input.postingDate}</dd></div><div><dt>Alasan pengajuan</dt><dd className={styles.preserve}>{input.reason}</dd></div>
  </dl><PayableCapacityFacts value={value.capacity} historical={historical} />
    <p>{rejected ? 'Penolakan tidak mencatat jurnal atau menerapkan uang muka. Sumber dan sisa nilai tetap memerlukan tindak lanjut.' : source.kind === 'recognize' ? `Debit akrual penerimaan dan kredit utang pemasok sebesar ${formatMoneyText(value.amount)}. Utang tetap terutang sampai ada penerapan atau penyelesaian yang sah.` : `Debit utang pemasok dan kredit uang muka sebesar ${formatMoneyText(value.amount)}. Hanya bagian ini yang diterapkan; sisa utang dan uang muka tetap dicatat.`}</p>
    <details><summary>Sumber dan kebijakan pembukuan</summary><PayablePolicyFacts value={policy} /><dl className={styles.facts}>
      <div><dt>Buku / versi bagan akun</dt><dd>{accounting.bookId} / {accounting.chartVersionId}</dd></div><div><dt>Periode pembukuan</dt><dd>{accounting.periodMonth}</dd></div><div><dt>Alokasi penilaian sumber</dt><dd>{source.valuationAllocationId}</dd></div>
      {source.kind === 'recognize' ? <><div><dt>Kemasan pada alokasi</dt><dd>{source.packages}</dd></div><div><dt>Nilai neto / pajak / biaya</dt><dd>{formatMoneyText(source.netAmount)} / {formatMoneyText(source.taxAmount)} / {formatMoneyText(source.chargeAmount)}</dd></div><div><dt>Jurnal penilaian</dt><dd>{source.valuationJournalId}</dd></div><div><dt>Penerimaan / dokumen invoice</dt><dd>{source.receiptId} / {source.invoiceDocumentId}</dd></div><div><dt>Pencocokan invoice / baris</dt><dd>{source.invoiceMatchId} / {source.matchLineNumber}</dd></div></> : <><div><dt>Utang / verifikasi debit</dt><dd>{source.payableId} / {source.settlementId}</dd></div><div><dt>Catatan uang muka</dt><dd>{source.disbursementId}</dd></div><div><dt>Jurnal verifikasi debit</dt><dd>{source.settlementJournalId}</dd></div></>}
    </dl></details>
  </div>;
}
