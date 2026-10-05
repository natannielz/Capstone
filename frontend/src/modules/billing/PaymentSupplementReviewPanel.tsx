'use client';

import {useLayoutEffect, useRef, useState} from 'react';
import common from '@/modules/administration/access.module.css';
import {PdfDocumentOcrPanel} from '../documents/PdfDocumentOcrPanel';
import {pdfDocumentOcrOwnerKey, type PdfDocumentObservationSelection, type PdfDocumentOcrView} from '../documents/pdf-document-ocr-client';
import {createPaymentSupplementGateway, parsePaymentSupplement, paymentSupplementError, paymentSupplementOwnerKey, supplementPdfOwner,
  type PaymentSupplement, type PaymentSupplementOwner, type PaymentSupplementReadContext} from './payment-pdf-supplement-client';
import {paymentCorrectionLabels, correctionFields} from './payment-correction-client';

export type PaymentSupplementObservation = {primary: PaymentSupplementOwner; supplement: PaymentSupplement; observation: PdfDocumentObservationSelection};
export type PaymentSupplementReviewProps = {owner: PaymentSupplementOwner; supplement: PaymentSupplement; context: PaymentSupplementReadContext;
  locked: boolean; current: () => boolean; onDenied: (failure: unknown) => void; onSelectObservation?: (selection: PaymentSupplementObservation) => void};

/** A distinct supplement source is never the declaration's primary image or primary OCR/correction result. */
export function PaymentSupplementReviewPanel(props: PaymentSupplementReviewProps) {
  if (!props.current()) return <p role="status">Sesi atau sumber berubah. Muat lampiran terbaru sebelum membuka dokumen.</p>;
  const saved = parsePaymentSupplement(props.supplement, props.owner);
  if (saved.state !== 'attached') return <p>Lampiran ini belum ditautkan. Bukti foto utama dan proses pembayaran tetap diperiksa secara terpisah.</p>;
  return <ReviewDesk key={props.context + ':' + paymentSupplementOwnerKey(props.owner) + ':' + JSON.stringify(saved)} {...props} supplement={saved}/>;
}
function ReviewDesk(props: PaymentSupplementReviewProps) {
  const [gateway] = useState(createPaymentSupplementGateway), [view, setView] = useState<PdfDocumentOcrView | null>(null), [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null), [downloaded, setDownloaded] = useState(false), [selection, setSelection] = useState<PaymentSupplementObservation | null>(null);
  const latest = useRef(props), active = useRef(false), pending = useRef(false), read = useRef<AbortController | null>(null), privateUrls = useRef(new Set<string>());
  useLayoutEffect(() => {latest.current = props;}, [props]);
  const authorized = props.current();
  const source = supplementPdfOwner(props.owner, props.supplement), key = pdfDocumentOcrOwnerKey(source);
  const current = () => active.current && latest.current.current() && paymentSupplementOwnerKey(latest.current.owner) === paymentSupplementOwnerKey(props.owner) &&
    pdfDocumentOcrOwnerKey(supplementPdfOwner(latest.current.owner, latest.current.supplement)) === key;
  useLayoutEffect(() => {active.current = true; const urls = privateUrls.current;
    return () => {active.current = false; read.current?.abort(); for (const url of urls) URL.revokeObjectURL(url); urls.clear();};}, []);
  useLayoutEffect(() => {if (!authorized) {read.current?.abort(); for (const url of privateUrls.current) URL.revokeObjectURL(url); privateUrls.current.clear();}}, [authorized]);
  async function work(action: (valid: () => boolean, signal: AbortSignal) => Promise<void>) {
    if (props.locked || pending.current || !current()) return; pending.current = true; read.current?.abort();
    const controller = new AbortController(); read.current = controller; const valid = () => current() && read.current === controller && !controller.signal.aborted;
    setBusy(true); setError(null);
    try {await action(valid, controller.signal);} catch (failure) {if (valid()) {setError(failure); latest.current.onDenied(failure);}}
    finally {pending.current = false; if (active.current) setBusy(false);}
  }
  const load = () => work(async (valid, signal) => {setView(null); setSelection(null);
    const shown = await gateway.ocr(props.context, props.owner, props.supplement, valid, signal); if (valid()) setView(shown.view);});
  const download = () => work(async (valid, signal) => {
    setDownloaded(false); setSelection(null); const blob = await gateway.original(props.context, props.owner, props.supplement, valid, signal);
    if (!valid()) return; const url = URL.createObjectURL(blob); privateUrls.current.add(url); const link = document.createElement('a');
    try {link.href = url; link.download = `lampiran-pdf-${props.supplement.supplementId}-${props.supplement.pdfEvidence!.id}.pdf`; document.body.appendChild(link); link.click(); if (valid()) setDownloaded(true);}
    finally {link.remove(); window.setTimeout(() => {URL.revokeObjectURL(url); privateUrls.current.delete(url);}, 1000);}
  });
  if (!authorized) return <p role="status">Sesi atau sumber berubah. Muat lampiran terbaru sebelum membuka dokumen.</p>;
  return <section aria-label="Pemeriksaan lampiran PDF tambahan"><h3>Lampiran PDF tambahan {props.supplement.supplementId}</h3>
    <p>Terikat pada pengajuan foto {props.owner.submissionId}. Lampiran PDF tidak menggantikan foto wajib, koreksi OCR foto, bukti bank independen, atau persetujuan pembayaran.</p>
    <p>Alasan lampiran: {props.supplement.reason}</p><p>Ditautkan: <time dateTime={props.supplement.attachedAt!}>{props.supplement.attachedAt}</time>.</p>
    <PdfDocumentOcrPanel owner={source} currentActorId={props.owner.actorId} view={view} locked={props.locked} loading={busy}
      errorMessage={error === null ? null : paymentSupplementError(error)} isCurrent={current} onRefresh={() => void load()} onOpenOriginal={() => void download()}
      onSelectObservation={downloaded ? observation => {if (!current() || pdfDocumentOcrOwnerKey(observation.source) !== key) return;
        const chosen = {primary: props.owner, supplement: props.supplement, observation}; setSelection(chosen);} : undefined}/>
    {!downloaded && <p className={common.help}>Unduh PDF tambahan asli dan baca seluruh halaman sebelum memilih rujukan. OCR hanya membantu pemeriksaan.</p>}
    {selection && <section className={common.preview} aria-label="Rujukan observasi lampiran PDF tambahan"><h4>Rujukan observasi lampiran yang dipilih</h4>
      <p>Lampiran {selection.supplement.supplementId} · halaman {selection.observation.reference.pageIndex + 1} · {paymentCorrectionLabels[selection.observation.reference.field as typeof correctionFields[number]]}.</p>
      <p>Nilai halaman: {selection.observation.candidate.value ?? 'Belum diketahui'}.</p><p>Pilihan ini tidak mengisi koreksi foto atau menetapkan pembayaran secara otomatis.</p>
      <details><summary>Rincian rujukan dokumen</summary><p>Hasil {selection.observation.reference.resultId} · hash {selection.observation.reference.resultHash}.</p></details>
      {props.context === 'reviewer' && props.onSelectObservation && <button type="button" className={common.secondary} disabled={props.locked || busy} onClick={() => {
        if (current() && !latest.current.locked) latest.current.onSelectObservation?.(selection);
      }}>Gunakan rujukan untuk koreksi foto</button>}
    </section>}
  </section>;
}
