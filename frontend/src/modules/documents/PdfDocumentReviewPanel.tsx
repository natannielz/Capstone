'use client';

import {useEffect, useRef, useState} from 'react';
import {AccessFailure, accessMessage} from '@/modules/administration/workflow';
import common from '@/modules/administration/access.module.css';
import {PdfDocumentOcrPanel} from './PdfDocumentOcrPanel';
import {pdfDocumentOcrOwnerKey, type PdfDocumentOcrOwner, type PdfDocumentOcrView, type PdfDocumentObservationSelection} from './pdf-document-ocr-client';
import {createPdfEvidenceGateway, pdfEvidenceTargetFor, type PdfReadContext} from './pdf-evidence-client';

/** Mount only after the server-owned evidence-format read has returned pdf; key by full owner binding. */
export function PdfDocumentReviewPanel(props: {context: PdfReadContext; owner: PdfDocumentOcrOwner; reservationId: string; locked: boolean;
  current: () => boolean; onDenied: (failure: unknown) => void; onSelectObservation?: (selection: PdfDocumentObservationSelection) => void}) {
  const [gateway] = useState(createPdfEvidenceGateway), [view, setView] = useState<PdfDocumentOcrView | null>(null), [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null), [downloaded, setDownloaded] = useState(false), [selection, setSelection] = useState<PdfDocumentObservationSelection | null>(null);
  const alive = useRef(false), controller = useRef<AbortController | null>(null), urls = useRef(new Set<string>()), key = pdfDocumentOcrOwnerKey(props.owner);
  useEffect(() => {alive.current = true; const privateUrls = urls.current; return () => {alive.current = false; controller.current?.abort();
    for (const url of privateUrls) URL.revokeObjectURL(url); privateUrls.clear();}; }, []);
  const current = () => alive.current && props.current();
  async function perform(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (props.locked || loading || !current()) return; controller.current?.abort(); const request = new AbortController(); controller.current = request;
    const valid = () => current() && controller.current === request && !request.signal.aborted;
    setLoading(true); setError(null);
    try {await action(valid, request.signal);} catch (failure) {if (valid()) {setError(failure); props.onDenied(failure);}}
    finally {if (valid()) setLoading(false);}
  }
  const load = () => perform(async (valid, signal) => {setView(null); setSelection(null); const shown = await gateway.ocr(props.context, props.owner, valid, signal); if (valid()) setView(shown);});
  const download = () => perform(async (valid, signal) => {
    setDownloaded(false); setSelection(null); const blob = await gateway.original(props.context, pdfEvidenceTargetFor(props.owner), props.reservationId, props.owner.evidence, valid, signal);
    if (!valid()) return; const url = URL.createObjectURL(blob); urls.current.add(url); const link = document.createElement('a');
    try {link.href = url; link.download = `bukti-${props.owner.evidence.id}-${props.owner.evidence.version}.pdf`; document.body.appendChild(link); link.click(); setDownloaded(true);}
    finally {link.remove(); window.setTimeout(() => {URL.revokeObjectURL(url); urls.current.delete(url);}, 1000);}
  });
  return <section aria-label="Pemeriksaan PDF yang diajukan"><p>Jenis bukti PDF berasal dari pencatatan server. Unduh dan baca seluruh PDF asli sebelum memilih rujukan observasi.</p>
    <PdfDocumentOcrPanel owner={props.owner} currentActorId={props.owner.actorId} view={view} locked={props.locked} loading={loading}
      errorMessage={error === null ? null : error instanceof AccessFailure && error.code === 'pdf_evidence_response_invalid' ? 'Bukti asli atau hasil belum dapat ditampilkan dengan aman. Muat sumber terbaru.' : accessMessage(error)}
      isCurrent={current} onRefresh={() => void load()} onOpenOriginal={() => void download()}
      onSelectObservation={downloaded ? chosen => {if (!current() || pdfDocumentOcrOwnerKey(chosen.source) !== key) return; setSelection(chosen); props.onSelectObservation?.(chosen);} : undefined}/>
    {!downloaded && <p className={common.help}>Unduh PDF asli untuk membuka pilihan rujukan. Hasil OCR tidak menggantikan pemeriksaan dokumen asli.</p>}
    {selection && <section className={common.preview} aria-label="Rujukan observasi PDF yang dipilih"><h3>Rujukan observasi yang dipilih</h3>
      <p>Halaman {selection.reference.pageIndex + 1} · isian {selection.reference.field} · hasil {selection.reference.resultId}.</p>
      <p>Hash hasil: {selection.reference.resultHash}</p><p>Nilai pada halaman ini: {selection.candidate.value ?? 'Belum diketahui'}</p>
      <p>Pilihan ini tidak mengisi isian pemeriksaan, mencocokkan stok, atau menyetujui pembayaran secara otomatis.</p></section>}
  </section>;
}
