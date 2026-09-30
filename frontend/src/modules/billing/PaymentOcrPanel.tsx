import type { PaymentOcrView } from './payment-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';

const labels = { amount: 'Jumlah transfer', currency: 'Mata uang', transfer_date: 'Tanggal transfer', transfer_time: 'Waktu transfer', recipient: 'Penerima', account: 'Rekening tujuan', bank: 'Bank tujuan', reference: 'Referensi bank' };
const flags: Record<string, string> = { unknown: 'Belum terbaca', ambiguous: 'Lebih dari satu kemungkinan', low_confidence: 'Perlu pemeriksaan ulang', unparsed: 'Format belum dikenali', masked: 'Nomor tersamarkan', timezone_unverified: 'Zona waktu belum dipastikan', no_text: 'Tidak ada teks yang terbaca', amount_unknown: 'Jumlah transfer belum diketahui', touches_image_edge: 'Periksa tepi gambar', ambiguous_fields: 'Ada data yang ambigu' };
const states = { queued: 'Menunggu pembacaan', running: 'Sedang membaca bukti', retry: 'Pembacaan tertunda; sistem akan mencoba kembali', dead: 'Pembacaan belum berhasil; hubungi petugas pemeriksaan', succeeded: 'Hasil pembacaan tersedia' };

export function PaymentOcrPanel({ view, locked, onRefresh }: { view: PaymentOcrView | null; locked: boolean; onRefresh: () => void }) {
  return <section className={common.accountSection} aria-label="Pembacaan bukti otomatis"><h2>Pembacaan bukti otomatis</h2>
    <p>Hasil OCR membantu pemeriksaan gambar. Petugas tetap harus memeriksa dana masuk secara terpisah. Hasil ini tidak mengubah pengajuan atau saldo tagihan.</p>
    <p role="status">{view ? states[view.state] : 'Muat hasil untuk memeriksa pembacaan bukti yang diajukan.'}</p>
    <button className={common.secondary} disabled={locked} onClick={onRefresh}>Muat hasil OCR</button>
    {view?.result && <><p>{view.result.qualityFlags.map(flag => flags[flag]).join(' · ') || 'Periksa kembali semua saran terhadap bukti asli.'}</p>
      <dl className={styles.facts}>{view.result.fields.map(field => <div key={field.field}><dt>{labels[field.field]}</dt><dd><span>{field.value ?? 'Belum diketahui'}</span>
        {field.confidence !== null && <p>Keyakinan pembacaan: {new Intl.NumberFormat('id-ID', { style: 'percent', maximumFractionDigits: 1 }).format(Number(field.confidence))}</p>}
        {field.flags.length > 0 && <p>{field.flags.map(flag => flags[flag]).join(' · ')}</p>}</dd></div>)}</dl>
      <details><summary>Riwayat pembacaan</summary><p>Hasil {view.result.id} · {new Intl.DateTimeFormat('id-ID', { timeZone: 'Asia/Jakarta', dateStyle: 'medium', timeStyle: 'short' }).format(new Date(view.result.extractedAt))} WIB</p>
        <p>Versi model: {view.result.provenance.modelVersion}</p></details></>}
  </section>;
}
