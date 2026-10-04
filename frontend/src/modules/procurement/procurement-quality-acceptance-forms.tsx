'use client';

import styles from '../administration/access.module.css';
import type { QualityAcceptanceChoice, QualityAcceptanceObservation, QualityAcceptancePolicy, QualityAcceptancePreview } from './procurement-quality-acceptance-client';

export type QualityObservationFields = { acceptedPackages: string; observedAt: string; qualityReference: string; putawayReference: string; segregationReference: string; acceptedCondition: string; lotReference: string; expiresOn: string; nonExpiringChecked: boolean; nonExpiringReason: string };
export const emptyQualityObservation = (): QualityObservationFields => ({ acceptedPackages: '', observedAt: '', qualityReference: '', putawayReference: '', segregationReference: '', acceptedCondition: '', lotReference: '', expiresOn: '', nonExpiringChecked: false, nonExpiringReason: '' });
export const qualityAcceptanceStates = { proposed: 'Menunggu pemeriksaan independen', approved_held: 'Mutu disetujui · barang tetap ditahan', rejected: 'Penerimaan mutu ditolak' };
export const qualityAcceptanceConsent = 'Saya telah memeriksa jumlah, kondisi, lot, kedaluwarsa, pemisahan barang dan lokasi penyimpanan. Barang tetap ditahan setelah keputusan ini.';
export const qualityAcceptanceNotice = 'Jumlah rusak, terbuka dan kedaluwarsa dapat mencakup kemasan yang sama. Tentukan bagian yang diterima melalui pemeriksaan langsung; jangan mengurangi atau menjumlahkan temuan untuk menebak jumlah barang baik.';
export const qualityWhen = (value: string) => new Intl.DateTimeFormat('id-ID', { timeZone: 'Asia/Jakarta', dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) + ' WIB';
export const qualityDay = (value: string) => new Intl.DateTimeFormat('id-ID', { timeZone: 'Asia/Jakarta', dateStyle: 'long' }).format(new Date(value + 'T00:00:00+07:00'));
export const qualityCount = (value: number | string) => new Intl.NumberFormat('id-ID').format(typeof value === 'number' ? value : BigInt(value));
export function qualityReceiptLabel(value: QualityAcceptanceChoice['receipt']) { const line = value.source.order.lines.find(item => item.skuId === value.source.inspection.line.requestSkuId)!; return `${value.input.receiptReference} · ${line.familyName} · ${line.packageLabel} · lot ${value.input.lotReference}`; }

export function QualityReceiptFacts({ value, historical = false }: { value: QualityAcceptanceChoice; historical?: boolean }) {
  const { receipt, heldBalance, capacity } = value, line = receipt.source.inspection.line, sku = receipt.source.order.lines.find(item => item.skuId === line.requestSkuId)!;
  return <section aria-label="Penerimaan fisik yang diperiksa"><h3>{sku.familyName} · {sku.packageLabel}</h3><dl className={styles.facts}>
    <div><dt>Referensi penerimaan</dt><dd>{receipt.input.receiptReference}</dd></div><div><dt>Pemasok</dt><dd>{receipt.source.order.supplier.displayName}</dd></div><div><dt>Pesanan pembelian</dt><dd>{receipt.source.order.commercialReference.reference}</dd></div>
    <div><dt>Kemasan dan isi</dt><dd>{sku.packageLabel} · {qualityCount(sku.baseUnitsPerPackage)} {sku.baseUnitCode} per kemasan</dd></div><div><dt>Lot penerimaan</dt><dd>{receipt.input.lotReference}</dd></div><div><dt>Kedaluwarsa asli</dt><dd>{receipt.input.expiresOn === null ? 'Tidak tercatat pada lot asli' : qualityDay(receipt.input.expiresOn)}</dd></div>
    <div><dt>Jumlah pada penerimaan ini</dt><dd>{qualityCount(receipt.input.packages)} kemasan</dd></div><div><dt>Saldo fisik ditahan</dt><dd>{qualityCount(heldBalance.held)} kemasan</dd></div><div><dt>Sudah diterima mutunya</dt><dd>{qualityCount(capacity.acceptedPackages)} kemasan</dd></div><div><dt>Sisa yang dapat diajukan</dt><dd>{qualityCount(capacity.remainingPackages)} kemasan</dd></div>
    <div><dt>Temuan kemasan rusak</dt><dd>{qualityCount(line.damagedPackages)} kemasan</dd></div><div><dt>Temuan kemasan terbuka</dt><dd>{qualityCount(line.openedPackages)} kemasan</dd></div><div><dt>Temuan kedaluwarsa</dt><dd>{qualityCount(line.expiredPackages)} kemasan</dd></div>
  </dl><p>{qualityAcceptanceNotice}</p><p>{historical ? 'Saldo dan sisa ini dicatat saat pengajuan. Bukan pembacaan saldo terkini.' : 'Saldo dan sisa berasal dari pembacaan terakhir dan akan diperiksa ulang saat penyimpanan.'}</p></section>;
}

export function QualityPolicyFacts({ value }: { value: QualityAcceptancePolicy }) {
  return <dl className={styles.facts}><div><dt>Persetujuan kebijakan</dt><dd>{value.approvalReference}</dd></div><div><dt>Prosedur pemeriksaan mutu</dt><dd>{value.qualityProcedureReference}</dd></div><div><dt>Prosedur penempatan barang</dt><dd>{value.putawayProcedureReference}</dd></div><div><dt>Masa berlaku kebijakan</dt><dd>{qualityWhen(value.approvedAt)} – {qualityWhen(value.expiresAt)}</dd></div><div><dt>Batas usia pengamatan</dt><dd>{new Intl.NumberFormat('id-ID').format(Number(value.maximumObservationAgeMilliseconds) / 60000)} menit</dd></div><div><dt>Barang tanpa kedaluwarsa</dt><dd>{value.allowNonExpiring ? `Diizinkan melalui ${value.nonExpiringProcedureReference}` : 'Tidak diizinkan'}</dd></div></dl>;
}

export function QualityObservationFacts({ value, checker = false }: { value: QualityAcceptanceObservation; checker?: boolean }) {
  return <section aria-label={checker ? 'Pengamatan pemeriksa independen' : 'Pengamatan pengaju'}><h3>{checker ? 'Pengamatan pemeriksa independen' : 'Pengamatan pengaju'}</h3><dl className={styles.facts}><div><dt>Jumlah diterima mutunya</dt><dd>{qualityCount(value.acceptedPackages)} kemasan</dd></div><div><dt>Waktu pemeriksaan</dt><dd>{qualityWhen(value.observedAt)}</dd></div><div><dt>Referensi pemeriksaan mutu</dt><dd>{value.qualityReference}</dd></div><div><dt>Lokasi penyimpanan bagian diterima</dt><dd>{value.putawayReference}</dd></div><div><dt>Pemisahan bagian diterima</dt><dd>{value.segregationReference}</dd></div><div><dt>Kondisi yang diperiksa</dt><dd>{value.acceptedCondition === 'sealed_undamaged' ? 'Kemasan tertutup dan tidak rusak' : 'Utuh sesuai definisi kemasan yang disetujui'}</dd></div><div><dt>Lot yang diperiksa</dt><dd>{value.lotReference}</dd></div><div><dt>Kedaluwarsa yang diperiksa</dt><dd>{value.expiresOn === null ? 'Tidak memiliki tanggal kedaluwarsa' : qualityDay(value.expiresOn)}</dd></div>{value.nonExpiringReason !== null && <div><dt>Alasan tanpa kedaluwarsa</dt><dd>{value.nonExpiringReason}</dd></div>}</dl></section>;
}

export function QualityAcceptanceFacts({ value, historical = false }: { value: Pick<QualityAcceptancePreview, 'input' | 'receipt' | 'heldBalance' | 'capacity' | 'policy'>; historical?: boolean }) {
  return <div aria-label="Fakta penerimaan mutu yang ditinjau"><QualityReceiptFacts value={value} historical={historical} /><QualityObservationFacts value={value.input} /><p><strong>Alasan pengajuan:</strong> {value.input.reason}</p><p>Persetujuan mutu tidak melepas stok. Seluruh barang tetap ditahan dan belum tersedia untuk pemesanan.</p><details><summary>Kebijakan pemeriksaan mutu</summary><QualityPolicyFacts value={value.policy} /></details></div>;
}

export function qualityObservationValues(value: QualityObservationFields) {
  const valid = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$/.test(value.observedAt), instant = valid ? new Date(value.observedAt + (value.observedAt.length === 16 ? ':00' : '') + '+07:00') : null;
  return { acceptedPackages: /^[1-9][0-9]{0,6}$/.test(value.acceptedPackages) ? Number(value.acceptedPackages) : Number.NaN,
    observedAt: instant && Number.isFinite(instant.valueOf()) ? instant.toISOString() : '', qualityReference: value.qualityReference,
    putawayReference: value.putawayReference, segregationReference: value.segregationReference, acceptedCondition: value.acceptedCondition,
    lotReference: value.lotReference, expiresOn: value.nonExpiringChecked ? null : value.expiresOn || null,
    nonExpiringReason: value.nonExpiringChecked ? value.nonExpiringReason : null };
}

export function QualityObservationForm({ prefix, value, onChange, disabled, nonExpiringAllowed, sourceExpiry, packageKind, checker = false }: { prefix: string; value: QualityObservationFields; onChange: (next: QualityObservationFields) => void; disabled: boolean; nonExpiringAllowed: boolean; sourceExpiry: string | null; packageKind: 'loose' | 'sealed' | 'other'; checker?: boolean }) {
  const update = <K extends keyof QualityObservationFields>(field: K, next: QualityObservationFields[K]) => onChange({ ...value, [field]: next });
  return <fieldset disabled={disabled} className={styles.accountSection}><legend>{checker ? 'Hasil pemeriksaan independen Anda' : 'Hasil pemeriksaan bagian yang diterima'}</legend>
    {checker && <p>Isi berdasarkan pemeriksaan Anda sendiri. Isian pengaju tidak disalin ke formulir pemeriksa.</p>}
    <label htmlFor={prefix + '-quantity'}>Jumlah kemasan yang diterima mutunya</label><input id={prefix + '-quantity'} inputMode="numeric" pattern="[1-9][0-9]{0,6}" maxLength={7} required value={value.acceptedPackages} onChange={event => update('acceptedPackages', event.target.value)} />
    <label htmlFor={prefix + '-observed'}>Waktu pemeriksaan (WIB)</label><input id={prefix + '-observed'} type="datetime-local" step="0.001" required value={value.observedAt} onChange={event => update('observedAt', event.target.value)} />
    <label htmlFor={prefix + '-quality'}>Referensi pemeriksaan mutu</label><input id={prefix + '-quality'} required maxLength={256} value={value.qualityReference} onChange={event => update('qualityReference', event.target.value)} />
    <label htmlFor={prefix + '-putaway'}>Referensi lokasi penyimpanan barang diterima</label><input id={prefix + '-putaway'} required maxLength={256} value={value.putawayReference} onChange={event => update('putawayReference', event.target.value)} />
    <label htmlFor={prefix + '-segregation'}>Referensi pemisahan bagian barang yang diterima</label><input id={prefix + '-segregation'} required maxLength={256} value={value.segregationReference} onChange={event => update('segregationReference', event.target.value)} />
    <label htmlFor={prefix + '-condition'}>Kondisi bagian barang yang diterima</label><select id={prefix + '-condition'} required value={value.acceptedCondition} onChange={event => update('acceptedCondition', event.target.value)}><option value="">Pilih hasil pemeriksaan kondisi</option>{packageKind === 'sealed' ? <option value="sealed_undamaged">Kemasan tertutup dan tidak rusak</option> : <option value="intact_as_defined">Utuh sesuai definisi kemasan yang disetujui</option>}</select>
    <label htmlFor={prefix + '-lot'}>Lot yang diperiksa</label><input id={prefix + '-lot'} required maxLength={128} value={value.lotReference} onChange={event => update('lotReference', event.target.value)} />
    {sourceExpiry !== null ? <><label htmlFor={prefix + '-expiry'}>Tanggal kedaluwarsa yang diperiksa</label><input id={prefix + '-expiry'} type="date" required value={value.expiresOn} onChange={event => update('expiresOn', event.target.value)} /><p>Tanggal harus cocok dengan lot penerimaan asli dan belum terlewati.</p></> : nonExpiringAllowed ? <><label className={styles.checkbox}><input type="checkbox" required checked={value.nonExpiringChecked} onChange={event => onChange({ ...value, nonExpiringChecked: event.target.checked, expiresOn: '' })} />Saya memastikan barang ini benar-benar tidak memiliki tanggal kedaluwarsa sesuai prosedur yang disetujui.</label><label htmlFor={prefix + '-non-expiring'}>Alasan barang tidak memiliki tanggal kedaluwarsa</label><textarea id={prefix + '-non-expiring'} required maxLength={1000} value={value.nonExpiringReason} onChange={event => update('nonExpiringReason', event.target.value)} /><p>Alasan pengaju dan pemeriksa harus sama dan didukung pemeriksaan masing-masing.</p></> : <p role="alert" className={styles.alert}>Penerimaan asli tidak mencatat tanggal kedaluwarsa. Kebijakan yang dipilih belum mengizinkan penerimaan barang tanpa kedaluwarsa.</p>}
  </fieldset>;
}
