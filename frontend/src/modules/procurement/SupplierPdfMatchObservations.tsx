'use client';

import styles from '../administration/access.module.css';
import {supplierPdfLabels, type SupplierDocumentPdfMatchObservation, type RememberedSupplierPdfObservation} from './supplier-document-pdf-observations';
import { Button } from '@/components/workspace-kit/button';

function Details({reference}: {reference: SupplierDocumentPdfMatchObservation}) {
  return <details><summary>Rincian rujukan dokumen</summary><p>Hasil pembacaan {reference.candidate.resultId} · sidik hasil {reference.candidate.resultHash}.</p>
    <p>Bukti {reference.candidate.evidence.id} · versi {reference.candidate.evidence.version} · sidik bukti {reference.candidate.evidence.sha256}.</p></details>;
}
export function SupplierPdfObservationList({observations}: {observations: SupplierDocumentPdfMatchObservation[]}) {
  return <section className={styles.preview} aria-label="Rujukan PDF tersimpan untuk pencocokan"><h3>Rujukan PDF yang diperiksa</h3>
    <p>Rujukan halaman melengkapi tinjauan manusia. Nilai dan alokasi pencocokan tetap mengikuti isian manual yang ditampilkan.</p>
    <ul>{observations.map(reference => <li key={reference.field}>{supplierPdfLabels[reference.field]} · halaman {reference.candidate.pageIndex + 1} · PDF asli diperiksa.
      <Details reference={reference}/></li>)}</ul></section>;
}
function Comparison({observation}: {observation: RememberedSupplierPdfObservation}) {
  const labels: Record<string, string> = {unknown: 'belum diketahui', ambiguous: 'lebih dari satu kemungkinan', low_confidence: 'keyakinan pembacaan rendah',
    unparsed: 'belum dapat dibaca', masked: 'sebagian disamarkan', timezone_unverified: 'zona waktu belum dipastikan'};
  return <><p><strong>{supplierPdfLabels[observation.reference.field]}</strong> · halaman {observation.reference.candidate.pageIndex + 1}.</p>
    <p>Nilai pada halaman PDF: {observation.value ?? 'Belum diketahui'}. Nilai ini tidak disalin ke fakta atau alokasi pencocokan.</p>
    {observation.flags.length > 0 && <p>Perlu perhatian: {observation.flags.map(flag => labels[flag] ?? 'perlu pemeriksaan manusia').join(', ')}.</p>}
    <Details reference={observation.reference}/></>;
}
export function SupplierPdfMatchSelection({candidate, observations, locked, mayUse, onUse, onRemove}: {
  candidate: RememberedSupplierPdfObservation | null; observations: RememberedSupplierPdfObservation[]; locked: boolean; mayUse: boolean;
  onUse: () => void; onRemove: (field: SupplierDocumentPdfMatchObservation['field']) => void;
}) {
  if (!candidate && observations.length === 0) return null;
  return <section className={styles.preview} aria-label="Rujukan PDF untuk pencocokan"><h3>Bandingkan PDF dengan pencocokan manual</h3>
    <p>Pilih rujukan setelah memeriksa PDF asli. Setiap isian, sumber, jumlah kemasan dan nilai tetap dipilih atau diisi sendiri.</p>
    {candidate && <section aria-label="Observasi PDF yang akan digunakan"><Comparison observation={candidate}/>
      {!mayUse && <p>Buka pencocokan manual dan perbarui verifikasi sebelum menggunakan rujukan ini.</p>}
      <Button variant="secondary" type="button" disabled={locked || !mayUse} onClick={onUse}>Gunakan rujukan PDF untuk pencocokan</Button></section>}
    {observations.length > 0 && <><p role="status">Rujukan berikut akan ikut dalam tinjauan akhir. Rujukan lain untuk isian yang sama akan menggantikan pilihan sebelumnya.</p>
      <ul>{observations.map(observation => <li key={observation.reference.field}><Comparison observation={observation}/>
        <Button variant="secondary" type="button" disabled={locked || !mayUse} onClick={() => onRemove(observation.reference.field)}>
          Hapus rujukan PDF: {supplierPdfLabels[observation.reference.field]}</Button></li>)}</ul></>}
  </section>;
}
