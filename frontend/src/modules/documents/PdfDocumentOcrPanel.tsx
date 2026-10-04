'use client';

import { useId, useState, type ReactNode } from 'react';
import common from '@/modules/administration/access.module.css';
import styles from './pdf-document-ocr.module.css';
import { parsePdfDocumentOcr, pdfDocumentOcrOwnerKey, selectPdfDocumentObservation,
  type PdfDocumentOcrCandidate, type PdfDocumentOcrOwner, type PdfDocumentOcrPage,
  type PdfDocumentOcrView, type PdfDocumentObservationSelection } from './pdf-document-ocr-client';

const labels: Record<PdfDocumentOcrCandidate['field'], string> = {amount: 'Jumlah transfer', currency: 'Mata uang', transfer_date: 'Tanggal transfer',
  transfer_time: 'Waktu transfer', recipient: 'Penerima', account: 'Rekening tujuan', bank: 'Bank tujuan', reference: 'Referensi bank',
  document_reference: 'Nomor dokumen pemasok', document_date: 'Tanggal dokumen', supplier_name: 'Nama pemasok', total_amount: 'Total tagihan',
  purchase_order_reference: 'Referensi pesanan pemasok', delivery_reference: 'Referensi surat jalan', source_sku: 'Kode barang pemasok'};
const reasons: Record<string, string> = {human_review_required: 'Wajib diperiksa manusia', unknown_on_page: 'Ada halaman yang belum terbaca',
  ambiguous_on_page: 'Ada beberapa kemungkinan pada satu halaman', contradictory_pages: 'Nilai antarhalaman berbeda; periksa semua observasi',
  repeated_across_pages: 'Nilai berulang pada beberapa halaman; bukan transaksi tambahan', unknown: 'Belum diketahui', ambiguous: 'Lebih dari satu kemungkinan',
  low_confidence: 'Keyakinan rendah; periksa ulang', unparsed: 'Format belum dikenali', masked: 'Nomor tersamarkan', timezone_unverified: 'Zona waktu belum dipastikan',
  no_visible_marks: 'Halaman tidak memiliki tanda yang terlihat', no_text: 'Tidak ada teks yang terbaca', orientation_unresolved: 'Arah halaman belum dipastikan',
  skew_out_of_range: 'Kemiringan halaman di luar batas pembacaan', low_text_confidence: 'Teks perlu diperiksa ulang', amount_unknown: 'Jumlah belum diketahui',
  touches_image_edge: 'Periksa teks di tepi halaman', ambiguous_fields: 'Ada isian yang ambigu', human_line_matching_required: 'Cocokkan sendiri dengan baris dokumen asli'};
const states = {queued: 'Menunggu pembacaan seluruh PDF', running: 'Sedang membaca seluruh PDF', retry: 'Pembacaan tertunda; hasil sebagian tidak ditampilkan',
  dead: 'Pembacaan seluruh PDF belum berhasil; hasil sebagian tidak ditampilkan', succeeded: 'Hasil pembacaan seluruh PDF tersedia untuk ditinjau'};
const describe = (values: string[]) => values.map(value => reasons[value] ?? `Perlu pemeriksaan (${value})`).join(' · ');
const decimal = (value: number) => String(Math.round(value * 1000) / 1000).replace('.', ',');
const percent = (value: string | null) => {
  if (value === null) return 'Belum diketahui';
  const [whole, fraction = ''] = value.split('.'), units = BigInt(whole) * BigInt(1000) + BigInt(fraction.padEnd(3, '0').slice(0, 3));
  const integer = units / BigInt(10), part = units % BigInt(10);
  return `${integer}${part ? `,${part}` : ''}%`;
};
const points = (value: {x: number; y: number}[]) => value.map(point => `(${decimal(point.x)}; ${decimal(point.y)})`).join(' → ');

export type PdfDocumentOcrPanelProps = {
  owner: PdfDocumentOcrOwner;
  currentActorId: string | null;
  view: PdfDocumentOcrView | null;
  locked: boolean;
  loading: boolean;
  errorMessage?: string | null;
  isCurrent: () => boolean;
  onRefresh: () => void;
  onOpenOriginal?: () => void;
  onSelectObservation?: (selection: PdfDocumentObservationSelection) => void;
  /** Only the evidence owner may supply an authorized preview; this component creates no private-media URL. */
  renderOriginalPage?: (request: {owner: PdfDocumentOcrOwner; pageIndex: number}) => ReactNode;
};

function Coordinates({candidate}: {candidate: PdfDocumentOcrCandidate}) {
  return candidate.boundingBox && candidate.pdfCanvasCorners ? <details className={styles.coordinates}><summary>Lokasi pada halaman asli</summary>
    <p>Kotak raster (x, y, lebar, tinggi; 0–1): {candidate.boundingBox.map(value => value.replace('.', ',')).join('; ')}.</p>
    <p>Empat sudut pada kanvas PDF: {points(candidate.pdfCanvasCorners)}.</p>
    <p>Koordinat telah dipetakan kembali ke kanvas PDF asli, termasuk rotasi halaman.</p></details> : <p className={common.help}>Lokasi belum diketahui; periksa PDF asli.</p>;
}

function Observation({candidate, pageIndex, view, props}: {candidate: PdfDocumentOcrCandidate; pageIndex: number; view: PdfDocumentOcrView; props: PdfDocumentOcrPanelProps}) {
  const [reviewed, setReviewed] = useState(false), id = useId();
  const enabled = !props.locked && !props.loading && props.currentActorId === props.owner.actorId && props.isCurrent();
  return <article className={styles.observation}><h4>{labels[candidate.field]}</h4><p className={styles.value}>{candidate.value ?? 'Belum diketahui pada halaman ini'}</p>
    <p>Keyakinan pembacaan: {percent(candidate.confidence)}.</p>
    {candidate.flags.length > 0 && <p className={styles.warning}>{describe(candidate.flags)}</p>}
    <Coordinates candidate={candidate}/>
    {props.onSelectObservation && <><label className={common.checkbox} htmlFor={id}><input id={id} type="checkbox" checked={reviewed} disabled={!enabled}
      onChange={event => setReviewed(event.target.checked)}/><span>Saya telah memeriksa isian ini pada PDF asli halaman {pageIndex + 1}.</span></label>
      <button type="button" className={common.secondary} disabled={!enabled || !reviewed} onClick={() => {
        if (!props.isCurrent() || props.currentActorId !== props.owner.actorId || props.locked || props.loading || !reviewed) return;
        props.onSelectObservation?.(selectPdfDocumentObservation(view, props.owner, pageIndex, candidate.field));
      }}>Pilih rujukan halaman {pageIndex + 1}: {labels[candidate.field]}</button>
      <p className={common.help}>Memilih rujukan tidak menyetujui nilai atau dokumen dan tidak mencatat pembayaran.</p></>}
  </article>;
}

function PageMap({page}: {page: PdfDocumentOcrPage}) {
  const {width, height} = page.raster;
  return <figure className={styles.map}><svg role="img" aria-label={`Peta lokasi teks halaman ${page.raster.pageIndex + 1}, bukan gambar bukti`}
    viewBox={`0 0 ${width} ${height}`} style={{aspectRatio: `${width} / ${height}`}}>
    <rect width={width} height={height} fill="var(--workspace-surface)" stroke="var(--workspace-border-interactive)"/>
    {page.lines.map((line, index) => <rect key={index} x={Number(line.boundingBox[0]) * width} y={Number(line.boundingBox[1]) * height}
      width={Number(line.boundingBox[2]) * width} height={Number(line.boundingBox[3]) * height} fill="none" stroke="var(--workspace-accent)"
      strokeWidth={Math.max(1, width / 400)}><title>{`${line.text} · ${percent(line.confidence)}`}</title></rect>)}
    {page.fields.filter(field => field.boundingBox !== null).map(field => <rect key={field.field} x={Number(field.boundingBox![0]) * width}
      y={Number(field.boundingBox![1]) * height} width={Number(field.boundingBox![2]) * width} height={Number(field.boundingBox![3]) * height}
      fill="none" stroke="var(--workspace-ink)" strokeDasharray="5 3" strokeWidth={Math.max(1, width / 350)}><title>{`${labels[field.field]}: ${field.value ?? 'Belum diketahui'}`}</title></rect>)}
  </svg><figcaption>Peta kotak teks dan isian OCR. Peta ini tidak menampilkan isi visual bukti; cocokkan dengan PDF asli.</figcaption></figure>;
}

function CompleteResult({view, props}: {view: PdfDocumentOcrView; props: PdfDocumentOcrPanelProps}) {
  const result = view.result!;
  return <><section className={styles.overview}><h3>Observasi lintas halaman</h3>
    <p>Seluruh halaman tetap terpisah. Tidak ada nilai pemenang atau jumlah gabungan yang dipilih otomatis.</p>
    <dl>{result.fields.map(field => <div key={field.field}><dt>{labels[field.field]}</dt><dd><p className={styles.warning}>{describe(field.reviewReasons)}</p>
      <ol>{field.observations.map(observation => <li key={observation.pageIndex}>Halaman {observation.pageIndex + 1}: {observation.candidate.value ?? 'Belum diketahui'}
        {' · '}{percent(observation.candidate.confidence)}{observation.candidate.flags.length > 0 && ` · ${describe(observation.candidate.flags)}`}</li>)}</ol></dd></div>)}</dl></section>
    {result.pages.map(page => <section className={styles.page} key={page.raster.pageIndex} data-pdf-ocr-page={page.raster.pageIndex}>
      <h3>Halaman {page.raster.pageIndex + 1} dari {result.pages.length}</h3><p className={styles.warning}>{describe(page.reviewReasons)}</p>
      <p className={common.help}>Halaman asli berotasi {page.raster.rotationDegrees}°. Raster {page.raster.width} × {page.raster.height} piksel.
        {' '}Pembacaan mengoreksi arah {page.preprocessing.orientationDegrees}° dan kemiringan {decimal(page.preprocessing.deskewDegrees)}°.</p>
      {props.renderOriginalPage && <div className={styles.original}>{props.renderOriginalPage({owner: props.owner, pageIndex: page.raster.pageIndex})}</div>}
      <PageMap page={page}/>
      <div className={styles.fields}>{page.fields.map(candidate => <Observation key={candidate.field} candidate={candidate} pageIndex={page.raster.pageIndex} view={view} props={props}/>)}</div>
      <details className={styles.lines}><summary>Semua baris teks halaman {page.raster.pageIndex + 1} ({page.lines.length})</summary>
        {page.lines.length === 0 ? <p>Tidak ada teks yang terbaca pada halaman ini. Halaman tetap wajib diperiksa pada PDF asli.</p> :
          <ol>{page.lines.map((line, index) => <li key={index}><p>{line.text}</p><p>Keyakinan: {percent(line.confidence)}.
            {Number(line.confidence) < .9 && ' Keyakinan rendah; periksa ulang.'}</p><p>Kotak raster: {line.boundingBox.map(value => value.replace('.', ',')).join('; ')}.
            {' '}Sudut pada kanvas PDF: {points(line.pdfCanvasCorners)}.</p></li>)}</ol>}
      </details></section>)}
    <details className={styles.lines}><summary>Identitas hasil dan pembacaan</summary><dl className={common.facts}>
      <div><dt>Hasil OCR</dt><dd>{result.id}</dd></div><div><dt>Hash hasil</dt><dd>{result.resultHash}</dd></div>
      <div><dt>Bukti asli dan versi</dt><dd>{view.evidence.id} · {view.evidence.version}</dd></div><div><dt>Hash bukti asli</dt><dd>{view.evidence.sha256}</dd></div>
      <div><dt>Waktu pembacaan (WIB)</dt><dd>{new Intl.DateTimeFormat('id-ID', {timeZone: 'Asia/Jakarta', dateStyle: 'medium', timeStyle: 'short'}).format(new Date(result.extractedAt))}</dd></div>
      <div><dt>Model pembacaan</dt><dd>{result.ocrProvenance.modelVersion}</dd></div><div><dt>Rasterisasi</dt><dd>{result.rasterProvenance.pipeline}</dd></div>
      <div><dt>Versi sumber</dt><dd>{view.sourceRevision}</dd></div></dl></details></>;
}

export function PdfDocumentOcrPanel(props: PdfDocumentOcrPanelProps) {
  const headingId = useId();
  if (props.currentActorId !== props.owner.actorId || !props.isCurrent()) return <section className={common.accountSection} aria-label="Pembacaan PDF otomatis">
    <p role="status">Sesi atau dokumen berubah. Muat ulang sumber yang masih diizinkan; hasil sebelumnya tidak ditampilkan.</p></section>;
  let shown: PdfDocumentOcrView | null = null, invalid = false;
  if (props.view) { try { shown = parsePdfDocumentOcr(props.view, props.owner); } catch { invalid = true; } }
  const enabled = !props.locked && !props.loading;
  return <section className={common.accountSection} aria-labelledby={headingId} aria-busy={props.loading}><h2 id={headingId}>Pembacaan PDF otomatis per halaman</h2>
    <p>OCR membantu menemukan isian pada seluruh PDF. Petugas harus memeriksa dokumen asli dan setiap observasi.
      {' '}Hasil ini tidak menetapkan nilai tagihan, dana masuk, penerimaan barang, atau persetujuan keuangan.</p>
    {props.errorMessage && <p className={common.alert} role="alert">{props.errorMessage}</p>}
    {invalid && <p className={common.alert} role="alert">Identitas atau kelengkapan hasil belum sesuai dengan sumber yang dipilih. Muat ulang; hasil tidak digunakan.</p>}
    <p role="status">{props.loading ? 'Memuat hasil pembacaan seluruh PDF…' : shown ? states[shown.state] : 'Belum ada hasil yang dimuat untuk dokumen ini.'}</p>
    <div className={common.actions}><button className={common.secondary} type="button" disabled={!enabled} onClick={() => { if (props.isCurrent() && props.currentActorId === props.owner.actorId) props.onRefresh(); }}>Muat hasil OCR PDF</button>
      {props.onOpenOriginal && <button className={common.secondary} type="button" disabled={!enabled} onClick={() => { if (props.isCurrent() && props.currentActorId === props.owner.actorId) props.onOpenOriginal?.(); }}>Buka PDF asli untuk pemeriksaan</button>}</div>
    {shown?.failureCode && <p className={styles.warning}>Seluruh dokumen belum berhasil dibaca. Periksa PDF asli atau hubungi petugas dengan kode {shown.failureCode}.</p>}
    {shown?.result && <CompleteResult key={`${pdfDocumentOcrOwnerKey(props.owner)}:${shown.result.id}:${shown.result.resultHash}`} view={shown} props={props}/>}
  </section>;
}
