import type { SourceReconciliationBaseline, SourceReconciliationObservation } from './stock-source-reconciliation-client';
import styles from '../administration/access.module.css';

export const reconciliationWhen = (value: string) => new Intl.DateTimeFormat('id-ID', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Jakarta' }).format(new Date(value)) + ' WIB';
const count = (value: string | number) => new Intl.NumberFormat('id-ID').format(BigInt(value));
const kinds = { loose: 'Satuan / lepas', sealed: 'Kemasan tertutup', other: 'Kemasan lain' };
export const reconciliationNotice = 'Rekonsiliasi hanya mencatat perbandingan jumlah fisik. Sumber belum diaktifkan; status tahan, biaya dan ketersediaan barang tidak berubah.';

export function ReconciliationBalanceFacts({ source }: { source: SourceReconciliationBaseline }) {
  return <section aria-label="Dasar jumlah lengkap"><h3>Dasar jumlah lengkap</h3><p>{source.balances.length} lot pada pembacaan ini. Jumlah fisik baru belum dicatat.</p>
    <ul className={styles.historyList}>{source.balances.map(row => <li key={row.balanceId}><strong>{row.skuCode} · {row.packageLabel} · Lot {row.lotReference}</strong><dl className={styles.facts}>
      <div><dt>Jumlah tercatat (kemasan)</dt><dd>{count(row.onHand)}</dd></div><div><dt>Ditahan</dt><dd>{count(row.held)}</dd></div><div><dt>Dipesan</dt><dd>{count(row.reserved)}</dd></div><div><dt>Terpisah sebagai kedaluwarsa</dt><dd>{count(row.expired)}</dd></div><div><dt>Isi per kemasan</dt><dd>{count(row.baseUnitsPerPackage)} {row.baseUnitCode}</dd></div><div><dt>Kedaluwarsa yang dicatat</dt><dd>{row.expiresOn ?? 'Belum diketahui'}</dd></div>
    </dl></li>)}</ul>
  </section>;
}

export function ReconciliationSourceFacts({ source }: { source: SourceReconciliationBaseline }) {
  return <>
    <p className={styles.scopeSummary}><strong>{source.poolLabel}</strong><br />{source.physicalKey}</p>
    <p className={styles.help}>{reconciliationNotice}</p>
    <details className={styles.recordedPermissions}>
      <summary>Dasar sumber dan kebijakan yang ditinjau</summary>
      <dl className={styles.facts}>
        <div><dt>Prosedur rekonsiliasi</dt><dd>{source.reconciliationProcedureReference}</dd></div>
        <div><dt>Data dibaca</dt><dd>{reconciliationWhen(source.capturedAt)}</dd></div>
        <div><dt>Perubahan sumber terakhir</dt><dd>{reconciliationWhen(source.latestSourceAt)}</dd></div>
        <div><dt>Batas usia pengamatan</dt><dd>{count(source.maximumAgeMilliseconds)} milidetik</dd></div>
        <div><dt>Revisi sumber / pengendali perubahan</dt><dd>{source.poolRevision} / {source.poolFence}</dd></div>
        <div><dt>Kebijakan / revisi</dt><dd>{source.policyId} / {source.policyRevision}</dd></div>
        <div><dt>Referensi konfigurasi</dt><dd>{source.configurationHash}</dd></div>
        <div><dt>Referensi jumlah lengkap</dt><dd>{source.baselineHash}</dd></div>
      </dl>
    </details>
  </>;
}

export function ReconciliationObservations({ source, observed, title }: { source: SourceReconciliationBaseline; observed: SourceReconciliationObservation; title: string }) {
  return <section aria-label={title}>
    <h3>{title}</h3>
    <dl className={styles.facts}>
      <div><dt>Waktu pengamatan</dt><dd>{reconciliationWhen(observed.observedAt)}</dd></div>
      <div><dt>Referensi pengamatan</dt><dd>{observed.sourceReference}</dd></div>
    </dl>
    {source.balances.length === 0 ? <p>Tidak ada saldo pada dasar lengkap ini. Pengamatan tetap harus memastikan lokasi diperiksa.</p> :
      <ul className={styles.historyList} aria-label={title + ' per lot'}>{source.balances.map(row => {
        const found = observed.entries.find(item => item.balanceId === row.balanceId), difference = found ? BigInt(found.packages) - BigInt(row.onHand) : null;
        return <li key={row.balanceId}>
          <strong>{row.skuCode} · {row.packageLabel} · Lot {row.lotReference}</strong>
          <dl className={styles.facts}>
            <div><dt>Jumlah tercatat (kemasan)</dt><dd>{count(row.onHand)}</dd></div>
            <div><dt>Jumlah fisik (kemasan)</dt><dd>{found ? count(found.packages) : 'Belum diisi'}</dd></div>
            <div><dt>Selisih fisik terhadap catatan</dt><dd>{difference === null ? 'Belum dapat dibandingkan' : difference === BigInt(0) ? 'Sesuai · 0' : `${difference > BigInt(0) ? '+' : ''}${difference.toString()} kemasan · perlu tindak lanjut`}</dd></div>
            <div><dt>Kedaluwarsa yang dicatat</dt><dd>{row.expiresOn ?? 'Belum diketahui; kelayakan belum ditentukan'}</dd></div>
          </dl>
        </li>;
      })}</ul>}
  </section>;
}

export function ReconciliationObservationFields({ source, checker, observedAt, sourceReference, quantities, disabled, onTime, onReference, onQuantity }: {
  source: SourceReconciliationBaseline; checker: boolean; observedAt: string; sourceReference: string; quantities: Record<string, string>; disabled: boolean;
  onTime: (value: string) => void; onReference: (value: string) => void; onQuantity: (balanceId: string, value: string) => void;
}) {
  const prefix = checker ? 'pemeriksa' : 'pengaju';
  return <fieldset className={styles.fields} disabled={disabled}>
    <legend>{checker ? 'Pengamatan pemeriksa sendiri' : 'Pengamatan fisik lengkap'}</legend>
    <p className={styles.help}>{checker ? 'Masukkan hasil pemeriksaan Anda sendiri. Isian tidak disalin dari pengaju.' : 'Periksa seluruh lot pada sumber ini. Isian kosong bukan jumlah nol.'} Jumlah menggunakan kemasan asli, bukan isi satuannya.</p>
    <div className={styles.twoFields}>
      <label htmlFor={`recon-${prefix}-time`}>Waktu pengamatan {prefix} (WIB)<input id={`recon-${prefix}-time`} type="datetime-local" step="1" required value={observedAt} onChange={event => onTime(event.target.value)} /></label>
      <label htmlFor={`recon-${prefix}-reference`}>Referensi pengamatan {prefix}<input id={`recon-${prefix}-reference`} required maxLength={160} value={sourceReference} onChange={event => onReference(event.target.value)} /></label>
    </div>
    <p className={styles.help}>Waktu harus setelah perubahan sumber terakhir ({reconciliationWhen(source.latestSourceAt)}), tidak di masa depan, dan masih dalam batas usia kebijakan.</p>
    {source.balances.length === 0 && <p>Dasar lengkap ini tidak memiliki saldo. Pastikan sumber fisik benar-benar diperiksa sebelum mengajukan pengamatan kosong.</p>}
    {source.balances.map(row => <section className={styles.review} key={row.balanceId} aria-label={`Lot ${row.lotReference}`}>
      <h3>{row.skuCode} · {row.packageLabel}</h3><p>Lot {row.lotReference} · {kinds[row.packageKind]}</p>
      <label htmlFor={`recon-${prefix}-${row.balanceId}`}>Jumlah fisik {prefix} · {row.skuCode} · {row.lotReference}<input id={`recon-${prefix}-${row.balanceId}`} type="text" inputMode="numeric" pattern="0|[1-9][0-9]{0,18}" maxLength={19} autoComplete="off" required value={quantities[row.balanceId] ?? ''} onChange={event => onQuantity(row.balanceId, event.target.value)} /></label>
      <p className={styles.help}>Isi per kemasan: {count(row.baseUnitsPerPackage)} {row.baseUnitCode}. Catat kemasan utuh menurut definisi ini.</p>
      <details className={styles.recordedPermissions}><summary>Jumlah tercatat dan asal lot</summary><dl className={styles.facts}>
        <div><dt>Jumlah tercatat (kemasan)</dt><dd>{count(row.onHand)}</dd></div>
        <div><dt>Ditahan</dt><dd>{count(row.held)}</dd></div>
        <div><dt>Dipesan</dt><dd>{count(row.reserved)}</dd></div>
        <div><dt>Terpisah sebagai kedaluwarsa</dt><dd>{count(row.expired)}</dd></div>
        <div><dt>Kedaluwarsa yang dicatat</dt><dd>{row.expiresOn ?? 'Belum diketahui'}</dd></div>
        <div><dt>Definisi kemasan / versi</dt><dd>{row.definitionId} / {row.definitionVersion}</dd></div>
        <div><dt>Penerimaan / lot</dt><dd>{row.receiptId} / {row.lotId}</dd></div>
      </dl></details>
    </section>)}
  </fieldset>;
}
