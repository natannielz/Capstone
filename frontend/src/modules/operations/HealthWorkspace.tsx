'use client';

import Link from 'next/link';
import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure } from '../administration/workflow';
import common from '../administration/access.module.css';
import { createOperatorHealthGateway, type OperatorHealth } from './health-client';
import { HealthReportView } from './HealthReportView';
import styles from './health.module.css';

// The report is cheap (counts on indexed fields); a minute keeps the page current without hammering the database.
const REFRESH_MS = 60_000;

function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  if (error instanceof AccessFailure && (error.status === 401 || error.status === 403)) return 'Halaman ini memerlukan izin pengaturan sistem.';
  return 'Laporan kesehatan belum dapat dibaca. Periksa ulang dalam beberapa saat; bila tetap gagal, periksa layanan API dan basis data.';
}

/** Q10 operator page: one read on open, a refresh every minute while visible, and a manual "Periksa ulang". */
export function HealthWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway), [gateway] = useState(createOperatorHealthGateway);
  const [actorId, setActorId] = useState<string | null>(null), [report, setReport] = useState<OperatorHealth | null>(null);
  const [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(true), [attempt, setAttempt] = useState(0);
  const inFlight = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const session = await identity.session(controller.signal);
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const scope = await access.current();
        if (scope.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!scope.allowedActions.includes('technical.configure')) throw new AccessFailure(403, 'scope_denied');
        if (!controller.signal.aborted) setActorId(scope.userId);
      } catch (failure) { if (!controller.signal.aborted) { setError(failure); setActorId(null); setBusy(false); } }
    })();
    return () => controller.abort();
  }, [identity, access, attempt]);

  // One read at a time. State is applied in the promise callback, so the opening read may start from an effect.
  const read = useCallback((actor: string) => {
    inFlight.current?.abort(); const controller = new AbortController(); inFlight.current = controller;
    void gateway.read(actor, controller.signal).then(value => ({ value }), (failure: unknown) => ({ failure })).then(outcome => {
      if (controller.signal.aborted) return;
      if ('value' in outcome) { setReport(outcome.value); setError(null); } else setError(outcome.failure);
      setBusy(false);
    });
  }, [gateway]);
  const refresh = useCallback((actor: string) => { setBusy(true); read(actor); }, [read]);

  useEffect(() => {
    if (actorId === null) return;
    read(actorId);
    const tick = window.setInterval(() => { if (document.visibilityState === 'visible') refresh(actorId); }, REFRESH_MS);
    return () => { window.clearInterval(tick); inFlight.current?.abort(); };
  }, [actorId, read, refresh]);

  const denied = error instanceof IdentityFailure || error instanceof AccessFailure && [401, 403, 409].includes(error.status) && report === null;
  return <main className={common.page}>
    <header className={common.header}>
      <div><Link className={common.brand} href="/identity/account">Unit Toko</Link><h1>Kesehatan sistem</h1>
        <p>Antrean latar, OCR dokumen, pembersihan draf, versi skema dan replikasi basis data. Hanya jumlah dan umur; tidak ada data pelanggan.</p></div>
      <Link href="/identity/account">Akun saya</Link>
    </header>
    {error !== null && !denied && <p className={common.alert} role="alert">{message(error)}</p>}
    {denied ? <SessionAccessState error={error} returnPath="/operations/health" onRetry={() => { setError(null); setBusy(true); setAttempt(value => value + 1); }} />
      : <>
        {report === null ? <p className={styles.notice} aria-live="polite">{busy ? 'Membaca laporan kesehatan…' : 'Laporan belum tersedia.'}</p> : <HealthReportView report={report} />}
        <div className={styles.actions}>
          <button type="button" disabled={busy || actorId === null} onClick={() => { if (actorId) refresh(actorId); }}>{busy ? 'Memeriksa…' : 'Periksa ulang'}</button>
          <span aria-live="polite">Diperbarui otomatis setiap menit selama halaman terbuka.</span>
        </div>
      </>}
  </main>;
}
