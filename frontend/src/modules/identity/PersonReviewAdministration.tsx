"use client";

import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import type { components } from "../../api/generated/schema";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery, useOperationRecovery } from "../administration/OperationRecovery";
import { submitTrackedRequest } from "../administration/operation-journal";
import { AccessFailure, MutationLedger } from "../administration/workflow";
import { createIdentityGateway } from "./identity-client";
import { IdentityFailure, sessionTiming, stepUpFields, type IdentitySession } from "./identity-workflow";
import { ProtectedNavigationForm } from "./ProtectedNavigationForm";
import { createPersonReviewGateway } from "./person-review-client";
import { PersonReviewQueue, PersonReviewTargetSearch, PersonReviewEditor, type PersonReviewSelection } from "./PersonReviewPanels";
import { LatestPersonReviewRead, personReviewMessage, personReviewNeedsRefresh, personReviewOperations, personReviewPath, personReviewPermission,
  type PersonReviewCommand, type PersonReviewSummary } from "./person-review-workflow";
import styles from "../administration/access.module.css";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Field } from '@/components/workspace-kit/field';
import { Tabs } from '@/components/workspace-kit/tabs';

type Loaded = { current: components["schemas"]["CurrentAccessDto"]; session: IdentitySession; observedAt: number };
const unavailable = (failure: unknown) => failure instanceof AccessFailure && ["person_governance_unavailable", "person_selection_unavailable"].includes(failure.code);
const profileUnavailable = (failure: unknown) => failure instanceof AccessFailure && failure.code === "person_profile_unavailable";

export function PersonReviewAdministration() {
  const [access] = useState(() => createAccessGateway());
  const [identity] = useState(() => createIdentityGateway());
  const [reads] = useState(() => new LatestPersonReviewRead());
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [revision, setRevision] = useState(0);
  const refresh = useCallback(async () => {
    const request = reads.begin();
    setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(request.signal), observedAt = performance.now();
      if (!request.current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, "unauthenticated");
      const current = await access.current();
      if (!request.current()) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, "actor_changed");
      if (!current.allowedActions.includes("person.review.read")) throw new AccessFailure(403, "permission_denied");
      setLoaded({ current, session, observedAt }); setRevision(value => value + 1);
    } catch (failure) { if (request.current()) setError(failure); }
    finally { if (request.current()) setLoading(false); }
  }, [access, identity, reads]);
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => { if (active) void refresh(); });
    const restore = (event: PageTransitionEvent) => { if (event.persisted) window.location.reload(); };
    window.addEventListener("pageshow", restore);
    return () => { active = false; reads.cancel(); window.removeEventListener("pageshow", restore); };
  }, [refresh, reads]);
  const onFailure = useCallback((failure: unknown) => {
    if ((failure instanceof AccessFailure || failure instanceof IdentityFailure) &&
      (failure.status === 401 || failure.code === "actor_changed" || failure.status === 403 && failure.code !== "step_up_required" || unavailable(failure) || profileUnavailable(failure))) {
      reads.cancel(); setLoaded(null); setLoading(false); setError(failure);
    }
  }, [reads]);
  return <main className={styles.page}>
    <PageHeader title="Peninjauan keterkaitan akun" description="Periksa pengajuan penangguhan, akun yang terdampak, dan keputusan petugas yang berwenang." />
    <div className="ui-stack">{loaded ? <PersonReviewBody key={`${loaded.current.userId}:${revision}`} {...loaded} onFailure={onFailure} onRefresh={refresh} />
      : loading ? <StateView kind="loading" title="Memeriksa sesi dan penugasan" />
      : <SessionAccessState error={error} returnPath={personReviewPath} onRetry={() => void refresh()} detail={error !== null ? personReviewMessage(error) : undefined} />}</div>
  </main>;
}

function PersonReviewBody({ current, session, observedAt, onFailure, onRefresh }: Loaded & { onFailure: (failure: unknown) => void; onRefresh: () => Promise<void> }) {
  const [gateway] = useState(() => createPersonReviewGateway());
  const [ledger] = useState(() => new MutationLedger());
  const [detailReads] = useState(() => new LatestPersonReviewRead());
  const recovery = useOperationRecovery(current.userId);
  const [selection, setSelection] = useState<PersonReviewSelection | null>(null);
  const [mode, setMode] = useState<"queue" | "propose">("queue");
  const [revision, setRevision] = useState(0);
  const [busy, setBusy] = useState(false);
  const [recoveryBusy, setRecoveryBusy] = useState(false);
  const [detailBusy, setDetailBusy] = useState(false);
  const [privateHidden, setPrivateHidden] = useState(false);
  const [retry, setRetry] = useState<PersonReviewCommand | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState("");
  const [elapsed, setElapsed] = useState(0);
  const [factorOperation, setFactorOperation] = useState(session.stepUpOperations.includes("person.review.propose") ? "person.review.propose" : "person.review.approve");
  const mounted = useRef(true), pending = useRef(false);
  const operationReference = useRef<{ operation: string; key: string } | null>(null);
  const locked = busy || recoveryBusy || retry !== null || recovery.locked;
  const timing = sessionTiming(session, elapsed);
  const now = () => Date.parse(session.serverTime) + Math.max(0, performance.now() - observedAt);
  useEffect(() => {
    mounted.current = true;
    const timer = window.setInterval(() => setElapsed(performance.now() - observedAt), 1000);
    return () => { mounted.current = false; detailReads.cancel(); window.clearInterval(timer); };
  }, [observedAt, detailReads]);
  useEffect(() => {
    if (!locked) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", warn); return () => window.removeEventListener("beforeunload", warn);
  }, [locked]);
  const resetSelection = useCallback(() => { detailReads.cancel(); setSelection(null); setDetailBusy(false); }, [detailReads]);
  const failedRead = useCallback((failure: unknown) => {
    setError(failure);
    if (personReviewNeedsRefresh(failure)) { detailReads.cancel(); setSelection(null); setDetailBusy(false); setPrivateHidden(true); }
    const privateUnavailable = failure instanceof AccessFailure && (failure.status === 403 && failure.code !== "step_up_required" || unavailable(failure) || profileUnavailable(failure));
    if (privateUnavailable) { detailReads.cancel(); setSelection(null); setDetailBusy(false); setPrivateHidden(true); }
    else onFailure(failure);
  }, [onFailure, detailReads]);
  async function choose(summary: PersonReviewSummary) {
    if (locked) return;
    const request = detailReads.begin(); setSelection(null); setDetailBusy(true); setError(null);
    try { const detail = await gateway.detail(current.userId, summary.result.proposalId, request.signal); if (request.current()) setSelection({ kind: "proposal", detail }); }
    catch (failure) { if (request.current()) failedRead(failure); }
    finally { if (request.current()) setDetailBusy(false); }
  }
  async function run(command: PersonReviewCommand) {
    if (pending.current || recoveryBusy) return;
    if (command.actorId !== current.userId) throw new AccessFailure(409, "actor_changed");
    pending.current = true; setBusy(true); setError(null); setMessage("");
    const operation = personReviewOperations[command.action];
    try {
      await submitTrackedRequest(ledger, recovery.journal, current.userId, "person-review", operation, command, key => {
        operationReference.current = { operation, key }; return gateway.submit(command, key, () => mounted.current);
      });
      if (mounted.current) {
        operationReference.current = null; setRetry(null); resetSelection(); setMode("queue"); setRevision(value => value + 1);
        setMessage(command.action === "propose" ? "Pengajuan tersimpan dan menunggu keputusan petugas lain." : command.action === "approve" ? "Penangguhan disetujui. Sesi akun yang terdampak telah dicabut." : "Keputusan tersimpan. Periksa status terbaru di daftar pengajuan.");
      }
    } catch (failure) {
      if (mounted.current) {
        setError(failure);
        const uncertain = ledger.isUncertain("person-review");
        if (uncertain) { setRetry(command); if (personReviewNeedsRefresh(failure)) { resetSelection(); setPrivateHidden(true); } }
        else { setRetry(null); if (personReviewNeedsRefresh(failure)) { resetSelection(); setPrivateHidden(true); } }
        if (!uncertain || !personReviewNeedsRefresh(failure)) onFailure(failure);
      }
      throw failure;
    } finally { pending.current = false; if (mounted.current) { setBusy(false); recovery.refresh(); } }
  }
  function resolved() {
    const active = operationReference.current;
    const retained = active && recovery.journal.list(current.userId).some(item => item.operation === active.operation && item.key === active.key);
    if (!retained) { ledger.resetAfterReconciliation(); operationReference.current = null; setRetry(null); resetSelection(); setPrivateHidden(false); setError(null); setRevision(value => value + 1); }
    recovery.refresh();
  }
  const factors = session.stepUpOperations.filter(item => ["person.review.propose", "person.review.approve"].includes(item) && current.allowedActions.includes(item));
  let fields: Record<string, string> | null = null;
  try { if (factors.includes(factorOperation)) fields = stepUpFields(session, factorOperation, personReviewPath); } catch { /* A stale session cannot prepare navigation. */ }
  const retryFactorHref = "/identity/account?operation=" + encodeURIComponent(retry ? personReviewPermission(retry.action) : factorOperation) + "&returnPath=" + encodeURIComponent(personReviewPath);
  if (timing.expired) return <SessionAccessState error={null} kind="expired" returnPath={personReviewPath} busy={busy} onRetry={() => void onRefresh()} />;
  return <>
    <AdministrationNavigation current="person-reviews" actions={current.allowedActions} locked={locked} />
    <Section title="Sesi peninjauan" id="person-session-title" description={<>Anda masuk sebagai <strong>{session.account?.displayName ?? session.account?.verifiedEmail}</strong>.</>}
      actions={<Button variant="secondary" type="button" disabled={locked} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</Button>}>
      <p className={styles.help}>{timing.fresh ? "Verifikasi dua langkah masih baru. Setiap tindakan tetap diperiksa terhadap izin dan penugasan terbaru." : "Verifikasi dua langkah terbaru diperlukan sebelum mengirim keputusan."}</p>
      <Tabs label="Tampilan peninjauan" disabled={locked} current={mode} onSelect={value => { resetSelection(); setMode(value as "queue" | "propose"); }}
        items={[{ label: "Daftar pengajuan", value: "queue" }, ...(current.allowedActions.includes("person.review.propose") ? [{ label: "Ajukan penangguhan baru", value: "propose" }] : [])]} />
      {factors.length > 0 && !retry && <div className={styles.preview}><Field id="person-review-factor" label="Tindakan untuk verifikasi"><select value={factorOperation} disabled={busy || recoveryBusy} onChange={event => setFactorOperation(event.target.value)}>
        {factors.map(operation => <option key={operation} value={operation}>{operation === "person.review.propose" ? "Pengajuan atau penarikan" : "Persetujuan atau penolakan"}</option>)}</select></Field>
        <ProtectedNavigationForm action="/auth/step-up" fields={fields ?? {}} disabled={busy || recoveryBusy || !fields} label="Verifikasi dua langkah" secondary />
        <p className={styles.help}>Verifikasi membuka layanan akun. Setelah kembali, pilih dan periksa pengajuan kembali. Verifikasi tidak mengaktifkan peninjauan yang belum disetujui.</p></div>}
    </Section>
    {message && <StateView kind="success" title={message} />}
    {error !== null && <StateView kind="error" title={personReviewMessage(error)} message={retry ? "Hasil belum pasti. Isian dan referensi pengajuan yang sama tetap tersimpan hanya di tab ini." : undefined}
      action={retry ? <Button variant="secondary" type="button" disabled={busy || recoveryBusy} onClick={() => void run(retry).catch(() => undefined)}>Periksa ulang permintaan yang sama</Button> : undefined}
      detail={retry ? <><Link href={retryFactorHref} target="_blank" rel="noopener noreferrer">Buka Akun saya untuk verifikasi dua langkah di tab baru</Link>. Setelah selesai, kembali ke tab ini untuk memeriksa hasil. Jangan membuat pengajuan pengganti.</> : undefined} />}
    {recovery.error !== null && <StateView kind="error" title={personReviewMessage(recovery.error)} />}
    <OperationRecovery key={current.userId} entries={recovery.entries} journal={recovery.journal} disabled={busy} onResolved={resolved} onFailure={failedRead} onBusyChange={setRecoveryBusy}
      recoveryAdapters={{ "person.review": {
        lookup: entry => gateway.receipt(entry, retry && operationReference.current?.operation === entry.operation && operationReference.current.key === entry.key ? retry : undefined),
        close: {
          allowed: operation => current.allowedActions.includes("person.review.read") && current.allowedActions.includes(operation === "person.review.approve" || operation === "person.review.reject" ? "person.review.approve" : "person.review.propose") && sessionTiming(session, performance.now() - observedAt).fresh,
          resolve: (entry, reason) => gateway.resolve(entry, reason, retry && operationReference.current?.operation === entry.operation && operationReference.current.key === entry.key ? retry : undefined),
        },
      } }} />
    {recovery.entries.some(entry => entry.operation.startsWith("person.review.")) && <p className={styles.help}>Penutupan menggunakan referensi asli, tanpa mengirim ulang isian pengajuan. Izin tindakan semula, verifikasi dua langkah terbaru, dan penugasan khusus tetap diperiksa server. Hasil yang sudah tersimpan tidak dibatalkan.</p>}
    {privateHidden ? <StateView kind="denied" title={profileUnavailable(error) ? "Data akun belum dapat ditinjau" : unavailable(error) ? "Peninjauan belum diaktifkan" : personReviewNeedsRefresh(error) ? "Data peninjauan perlu diperiksa kembali" : error instanceof AccessFailure && error.status === 403 && error.code !== "step_up_required" ? "Akses peninjauan belum disetujui" : "Data peninjauan perlu diperiksa kembali"}
      message={<>Data sebelumnya disembunyikan setelah perubahan izin, ketersediaan data, atau pratinjau. {retry || recovery.entries.length > 0 ? "Selesaikan pemulihan permintaan yang belum pasti, lalu muat ulang sesi dan akses." : "Muat ulang sesi dan akses untuk memeriksa data terbaru."}</>} /> : <div className="ui-desk">
      {mode === "queue" ? <PersonReviewQueue gateway={gateway} actorId={current.userId} revision={revision} locked={locked} onReset={resetSelection} onFailure={failedRead} onSelect={summary => void choose(summary)} />
        : <PersonReviewTargetSearch gateway={gateway} actorId={current.userId} revision={revision} locked={locked} onReset={resetSelection} onFailure={failedRead} onSelect={target => { resetSelection(); setSelection({ kind: "target", target }); }} />}
      {selection ? <PersonReviewEditor key={selection.kind === "target" ? `target:${selection.target.groupId}:${selection.target.version}` : `proposal:${selection.detail.summary.result.proposalId}:${selection.detail.summary.result.version}`}
        selection={selection} actorId={current.userId} permissions={current.allowedActions} gateway={gateway} locked={locked} fresh={timing.fresh} now={now} onFailure={failedRead} onSubmit={run} onCancel={resetSelection} />
        : <section className="ui-section" aria-busy={detailBusy}><h2>{detailBusy ? "Memuat rincian pengajuan" : "Pilih untuk memeriksa"}</h2>
          <p role={detailBusy ? "status" : undefined}>{detailBusy ? "Memeriksa data dalam lingkup penugasan Anda…" : mode === "queue" ? "Buka pengajuan dari daftar untuk melihat alasan, akun terdampak, dan keputusan yang tersedia." : "Cari dan pilih keterkaitan akun, lalu periksa dampaknya sebelum mengajukan."}</p></section>}
    </div>}
  </>;
}