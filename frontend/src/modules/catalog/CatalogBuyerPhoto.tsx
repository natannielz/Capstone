'use client';

import {useEffect, useRef, useState} from 'react';
import {ImageOff, PackageOpen, RefreshCw} from 'lucide-react';
import {AccessFailure} from '../administration/workflow';
import {RequestNotSentError} from '../../api/client';
import type {BuyerVariant} from './catalog-buyer-client';
import {buyerPhotoIdentity, buyerPhotoTarget, createBuyerPhotoGateway} from './buyer-photo-client';
import styles from './buyer-catalog.module.css';

type Props = {actorId: string; item: BuyerVariant; isCurrent: () => boolean};
/** deferred: waiting to come near the viewport; nothing is being read, so the photo is not busy. */
type State = {kind: 'deferred'} | {kind: 'loading'} | {kind: 'none'} | {kind: 'hidden'} | {kind: 'error'; message: string} | {kind: 'ready'; url: string};
function failureMessage(error: unknown) {
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.status === 401 || error.status === 403) return 'Foto tidak tersedia untuk sesi atau akses saat ini.';
    if (error.code === 'buyer_photo_response_invalid') return 'Foto belum dapat diverifikasi. Muat ulang untuk memeriksa kembali.';
  }
  return 'Foto belum dapat dimuat. Periksa koneksi lalu coba lagi.';
}

/** Reloading is offered only when there is something to recover from; a loaded or absent photo needs no action. */
export function photoRefreshOffered(kind: State["kind"]): boolean { return kind === "error" || kind === "hidden"; }

/** Each buyer's private photo reads run one at a time, so a card waits until it is about to scroll into view: the photos a
 *  person is looking at come first and a long page does not queue every card at once. */
export const photoViewportMargin = '400px 0px';
export function photoReadAllowed(nearViewport: boolean, observable: boolean): boolean { return nearViewport || !observable; }

export function CatalogBuyerPhoto(props: Props) {
  return <PrivatePhoto key={buyerPhotoIdentity(props.actorId, buyerPhotoTarget(props.item))} {...props}/>;
}
function PrivatePhoto({actorId, item, isCurrent}: Props) {
  const [gateway] = useState(() => createBuyerPhotoGateway()), [target] = useState(() => buyerPhotoTarget(item));
  const [revision, setRevision] = useState(0), [state, setState] = useState<State>({kind: 'deferred'});
  const activeUrl = useRef<string | null>(null), activeRequest = useRef<AbortController | null>(null), frame = useRef<HTMLDivElement>(null);
  const [observable] = useState(() => typeof IntersectionObserver !== 'undefined'), [near, setNear] = useState(false);
  const allowed = photoReadAllowed(near, observable);
  useEffect(() => {
    if (allowed || !frame.current) return;
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { setNear(true); observer.disconnect(); }
    }, {rootMargin: photoViewportMargin});
    observer.observe(frame.current);
    return () => observer.disconnect();
  }, [allowed]);
  const revoke = () => { if (activeUrl.current) URL.revokeObjectURL(activeUrl.current); activeUrl.current = null; };
  useEffect(() => {
    if (!allowed) return;
    let active = true; const abort = new AbortController(); activeRequest.current = abort;
    const current = () => active && !abort.signal.aborted && navigator.onLine && isCurrent();
    const hide = () => { abort.abort(); revoke(); if (active) setState({kind: 'hidden'}); };
    const restored = (event: PageTransitionEvent) => { if (event.persisted) hide(); };
    window.addEventListener('pagehide', hide); window.addEventListener('pageshow', restored); window.addEventListener('offline', hide);
    void Promise.resolve().then(async () => {
      if (!active || abort.signal.aborted) return;
      if (!current()) { hide(); return; }
      setState({kind: 'loading'});
      try {
        const photo = await gateway.read(actorId, target, current, abort.signal);
        if (!current()) return;
        if (photo === null) { setState({kind: 'none'}); return; }
        const url = URL.createObjectURL(photo); activeUrl.current = url; setState({kind: 'ready', url});
      } catch (error) { if (current()) { revoke(); setState({kind: 'error', message: failureMessage(error)}); } }
    });
    return () => { active = false; abort.abort(); revoke(); window.removeEventListener('pagehide', hide); window.removeEventListener('pageshow', restored); window.removeEventListener('offline', hide); };
  }, [actorId, allowed, gateway, isCurrent, revision, target]);
  function refresh() {
    activeRequest.current?.abort(); revoke();
    if (!navigator.onLine || !isCurrent()) { setState({kind: 'hidden'}); return; }
    setState({kind: 'loading'}); setRevision(value => value + 1);
  }
  return <section className={styles.buyerPhoto} aria-label={`Foto kemasan ${item.code}`} aria-busy={state.kind === 'loading'} data-photo-state={state.kind}>
    <div className={styles.buyerPhotoFrame} ref={frame}>
      {state.kind === 'ready' ?
        // A verified private blob must not be sent to a public URL or image optimizer.
        // eslint-disable-next-line @next/next/no-img-element
        <img src={state.url} alt={`Foto ${item.familyName} — ${item.definition.packageLabel}`} onError={() => { if (activeUrl.current !== state.url) return; revoke(); setState({kind: 'error', message: 'Foto belum dapat ditampilkan. Muat ulang untuk memeriksa kembali.'}); }}/>
        : <div className={styles.buyerPhotoState}>{state.kind === 'error' ? <ImageOff size={36} strokeWidth={1.25} aria-hidden="true"/> : <PackageOpen size={40} strokeWidth={1.1} aria-hidden="true"/>}
          {state.kind !== 'deferred' && <p role="status">{state.kind === 'loading' ? 'Memuat foto…' : state.kind === 'none' ? 'Foto belum tersedia' : state.kind === 'hidden' ? 'Foto disembunyikan. Periksa kembali sesi dan koneksi.' : state.message}</p>}</div>}
    </div>
    {photoRefreshOffered(state.kind) && <button type="button" className={styles.buyerPhotoRefresh} aria-label={`Muat ulang foto ${item.code}`} onClick={refresh}><RefreshCw size={14} aria-hidden="true"/>Muat ulang foto</button>}
  </section>;
}
