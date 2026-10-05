'use client';

import {useEffect, useRef, useState, type ReactNode} from 'react';
import {type OperationJournal} from '@/modules/administration/operation-journal';
import {accessMessage} from '@/modules/administration/workflow';
import common from '@/modules/administration/access.module.css';
import {pdfDocumentOcrOwnerKey, type PdfDocumentOcrOwner, type PdfDocumentObservationSelection} from './pdf-document-ocr-client';
import {createPdfEvidenceGateway, type PdfEvidenceTarget, type PdfEvidenceStatus, type PdfReadContext} from './pdf-evidence-client';
import {EvidenceFormatChoice} from './EvidenceFormatChoice';
import {PdfEvidenceAdmissionPanel} from './PdfEvidenceAdmissionPanel';
import {PdfDocumentReviewPanel} from './PdfDocumentReviewPanel';

export type EvidenceAttachmentBoundaryProps = {context: PdfReadContext; target: PdfEvidenceTarget;
  submitted: {owner: PdfDocumentOcrOwner; reservationId: string} | null; locked: boolean; fresh: boolean; now: number;
  current: () => boolean; journal: OperationJournal; onBusyChange: (busy: boolean) => void; onDenied: (failure: unknown) => void;
  onReviewSubmission?: (selected: {status: PdfEvidenceStatus; reviewedOriginalKey: string}) => void;
  onKind?: (kind: 'image' | 'pdf' | null) => void; onSelectObservation?: (selection: PdfDocumentObservationSelection) => void; renderImage: () => ReactNode};

/** Existing image UI is supplied unchanged; each admission gets a deliberately chosen format. */
export function EvidenceAttachmentBoundary(props: EvidenceAttachmentBoundaryProps) {
  const [gateway] = useState(createPdfEvidenceGateway), [chosen, setChosen] = useState<'image' | 'pdf' | null>(null);
  const [resolved, setResolved] = useState<{key: string; kind: 'image' | 'pdf'} | null>(null), [error, setError] = useState<unknown>(null), [refresh, setRefresh] = useState(0);
  const alive = useRef(false), key = props.submitted ? pdfDocumentOcrOwnerKey(props.submitted.owner) : null;
  const submitted = props.submitted;
  const authorized = props.current(), context = props.context;
  const latest = useRef({current: props.current, onDenied: props.onDenied, onKind: props.onKind, key, context});
  useEffect(() => {latest.current = {current: props.current, onDenied: props.onDenied, onKind: props.onKind, key, context};});
  useEffect(() => {alive.current = true; return () => {alive.current = false;};}, []);
  useEffect(() => {
    if (key === null || !authorized) return; const owner = JSON.parse(key) as PdfDocumentOcrOwner; let active = true; const controller = new AbortController();
    const valid = () => active && alive.current && latest.current.key === key && latest.current.context === context && latest.current.current() && !controller.signal.aborted;
    void Promise.resolve().then(async () => {
      if (!valid()) return; setResolved(null); setError(null); latest.current.onKind?.(null);
      try {const saved = await gateway.format(context, owner, valid, controller.signal);
        if (valid()) {setResolved({key, kind: saved.kind}); latest.current.onKind?.(saved.kind);}}
      catch (failure) {if (valid()) {setError(failure); latest.current.onDenied(failure);}}
    }); return () => {active = false; controller.abort();};
  }, [gateway, context, authorized, key, refresh]);
  if (!authorized) return <p role="status">Sesi atau sumber berubah. Muat ulang sumber yang masih diizinkan.</p>;
  if (submitted) {
    if (error !== null) return <section className={common.accountSection}><p role="alert">{accessMessage(error)}</p><button type="button" className={common.secondary}
      disabled={props.locked} onClick={() => setRefresh(value => value + 1)}>Muat ulang jenis bukti tersimpan</button></section>;
    if (!resolved || resolved.key !== key) return <p role="status">Memeriksa jenis bukti dari pencatatan server…</p>;
    return resolved.kind === 'image' ? <>{props.renderImage()}</> : <PdfDocumentReviewPanel key={key!} context={props.context} owner={submitted.owner}
      reservationId={submitted.reservationId} locked={props.locked} current={props.current} onDenied={props.onDenied} onSelectObservation={props.onSelectObservation}/>;
  }
  return <><EvidenceFormatChoice value={chosen} disabled={props.locked} onChange={value => {setChosen(value); props.onKind?.(value);}}/>
    {chosen === 'image' && props.renderImage()}
    {chosen === 'pdf' && props.onReviewSubmission && <PdfEvidenceAdmissionPanel key={`${props.target.actorId}:${props.target.sourceId}:${props.target.sourceRevision}:pdf`}
      owner={props.target} locked={props.locked} fresh={props.fresh} now={props.now} current={props.current} journal={props.journal}
      onBusyChange={props.onBusyChange} onDenied={props.onDenied} onReviewSubmission={props.onReviewSubmission}/>}</>;
}
