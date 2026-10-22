"use client";

import { SessionAccessState, lostAccessKind } from '@/components/workspace-kit/session-access-state';
import Link from "next/link";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { RequestNotSentError } from "../../api/client";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery } from "../administration/OperationRecovery";
import { OperationJournal } from "../administration/operation-journal";
import { AccessFailure, accessMessage } from "../administration/workflow";
import { createIdentityGateway } from "../identity/identity-client";
import { canUseStockSourcePolicies, IdentityFailure, identityMessage, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import { sourceActor, type SourceDecision } from "./stock-source-workflow";
import { createStockSourcePolicyGateway } from "./stock-source-policy-client";
import { StockSourcePolicyEditor } from "./stock-source-policy-editor-state";
import { PolicyConfigurationFacts, PolicyDraftForm, PolicyFinalReview, PolicyInputFacts, PolicyProposalFacts, policyStatus } from "./StockSourcePolicyForms";
import common from "../administration/access.module.css";
import styles from "./stock-source-policy.module.css";
import { formatNumber } from "@/lib/format";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { Badge } from '@/components/workspace-kit/badge';

const path = "/inventory/sources/policies";
const factorHref = "/identity/account?operation=stock.source.policy.approve&returnPath=" + encodeURIComponent(path);
type Loaded = { actorId: string; actions: string[]; session: IdentitySession; observedAt: number; generation: number };
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return accessMessage(error);
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (["stock_source_policy_input_invalid", "stock_source_input_invalid", "stock_source_selection_required"].includes(error.code))
      return "Periksa referensi alokasi, pengajuan, pencatat stok, dan acuan prosedur. Lengkapi alasan sebelum meninjau.";
    if (error.code === "stock_source_policy_changed" || error.status === 409)
      return "Pengaturan atau pengajuan telah berubah. Muat kembali dan periksa informasi terbaru sebelum melanjutkan.";
    if (error.code === "step_up_required") return "Verifikasi dua langkah terbaru diperlukan untuk persetujuan, penolakan, dan pemeriksaan hasil keputusan.";
    if (error.status >= 500 || error.status === 0)
      return "Hasil belum dapat dipastikan. Gunakan Periksa hasil jika ada referensi pemulihan. Tunggu hasil yang jelas sebelum mengirim permintaan baru.";
    return accessMessage(error);
  }
  return "Koneksi terputus. Periksa hasil melalui referensi pemulihan yang tersimpan sebelum melanjutkan.";
}
export function StockSourcePolicyWorkspace() {
  const [access] = useState(() => createAccessGateway()), [identity] = useState(() => createIdentityGateway());
  const request = useRef<AbortController | null>(null), generation = useRef(0);
  const [loaded, setLoaded] = useState<Loaded | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true);
  const refresh = useCallback(async () => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const current = () => request.current === controller && !controller.signal.aborted;
    setLoaded(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!current()) return;
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, "unauthenticated");
      const account = await access.current(); if (!current()) return;
      if (account.userId !== session.account.userId) throw new AccessFailure(409, "actor_changed");
      const actorId = sourceActor(account.userId);
      if (!Array.isArray(account.allowedActions) || account.allowedActions.length > 200 ||
        account.allowedActions.some(action => typeof action !== "string" || !/^[a-z]+(?:[.-][a-z]+){1,5}$/.test(action)))
        throw new AccessFailure(502, "stock_source_policy_response_invalid");
      if (!canUseStockSourcePolicies(account.allowedActions)) throw new AccessFailure(403, "access_denied");
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, "session_expired");
      setLoaded({ actorId, actions: [...account.allowedActions], session, observedAt, generation: ++generation.current });
    } catch (failure) { if (current()) setError(failure); }
    finally { if (current()) setLoading(false); }
  }, [access, identity]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => { if (active) void refresh(); });
    const restored = (event: PageTransitionEvent) => { if (event.persisted) void refresh(); };
    window.addEventListener("pageshow", restored);
    return () => { active = false; request.current?.abort(); window.removeEventListener("pageshow", restored); };
  }, [refresh]);
  return <main className={common.page}>
    <a className={styles.skip} href="#policy-workspace">Langsung ke kebijakan operasional</a>
    <PageHeader title="Kebijakan operasional stok" description="Tetapkan pencatat stok utama serta prosedur rekonsiliasi dan penanganan gangguan." />
    <div id="policy-workspace" className="ui-stack">{loaded ? <PolicyDesk key={loaded.actorId + ":" + loaded.generation} loaded={loaded} onRefresh={refresh} /> :
      (loading ? <StateView kind="loading" title="Memeriksa sesi dan akses" />
        : <SessionAccessState error={error} returnPath={path} onRetry={() => void refresh()} detail={error !== null ? message(error) : undefined} />)}</div>
  </main>;
}
function PolicyDesk({ loaded, onRefresh }: { loaded: Loaded; onRefresh: () => Promise<void> }) {
  const [editor] = useState(() => new StockSourcePolicyEditor({ actorId: loaded.actorId, actions: loaded.actions, session: loaded.session,
    observedAt: loaded.observedAt, now: () => performance.now(), gateway: createStockSourcePolicyGateway(),
    journal: new OperationJournal(() => window.localStorage) }));
  const state = useSyncExternalStore(editor.subscribe, editor.snapshot, editor.snapshot);
  const reviewFocus = useRef<HTMLDivElement>(null), resultFocus = useRef<HTMLDivElement>(null);
  useEffect(() => {
    void editor.start(); const timer = window.setInterval(() => editor.checkSession(), 500);
    const changed = (event: StorageEvent) => { if (event.key === null || event.key.startsWith("unit-toko:pending:v1:")) editor.refreshReferences(); };
    const leaving = () => editor.dispose();
    window.addEventListener("storage", changed); window.addEventListener("pagehide", leaving);
    return () => { editor.dispose(); window.clearInterval(timer); window.removeEventListener("storage", changed); window.removeEventListener("pagehide", leaving); };
  }, [editor]);
  const warn = state.sending || state.attempt !== null || state.review !== null || state.references.length > 0 || state.recoveryError !== null;
  useEffect(() => {
    if (!warn) return;
    const leaving = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", leaving); return () => window.removeEventListener("beforeunload", leaving);
  }, [warn]);
  useEffect(() => { if (state.review) reviewFocus.current?.focus(); }, [state.review]);
  useEffect(() => { if (state.result) resultFocus.current?.focus(); }, [state.result]);
  const busy = state.sending || state.recovering, locked = editor.locked;
  if (state.expired || state.suspended) return <SessionAccessState error={state.error} kind={state.expired ? 'expired' : lostAccessKind(state.error)} returnPath={path} busy={busy} onRetry={() => void onRefresh()} detail={state.error !== null ? message(state.error) : undefined} />;
  return <>
    <AdministrationNavigation current="source-policies" actions={loaded.actions} locked={locked} />
    <Section title="Periksa sumber yang ditugaskan" id="policy-account-heading" description={<>Petugas: <strong>{loaded.session.account?.displayName ?? loaded.session.account?.verifiedEmail}</strong></>}
      actions={<Button variant="secondary" disabled={busy || state.review !== null} onClick={() => void onRefresh()}>Muat ulang sesi dan akses</Button>}>
      {editor.canApprove && <p className={common.help}>{state.fresh ? "Verifikasi dua langkah masih baru. " : "Persetujuan dan penolakan memerlukan verifikasi dua langkah terbaru. "}
        <Link href={factorHref} aria-disabled={busy} onClick={event => { if (busy) event.preventDefault(); }}>Verifikasi untuk peninjauan</Link></p>}
    </Section>
    <aside className={styles.notice}><strong>Persetujuan kebijakan belum mengaktifkan sumber stok.</strong>
      <p>Pengoperasian memerlukan pemeriksaan kesiapan dan serah terima tersendiri. Saldo dan jumlah tersedia belum berubah.</p></aside>
    {state.sending && <p role="status">Menyimpan permintaan… Tunggu hasil sebelum melanjutkan.</p>}
    {state.error !== null && <StateView kind="error" title={message(state.error)} />}
    {state.recoveryError !== null && <StateView kind="error" title={message(state.recoveryError)} />}
    {state.references.length > 0 && <Section title="Periksa permintaan semula" id="policy-recovery-heading">
      <p>Gunakan referensi alokasi yang dipakai saat mengirim permintaan. Pemeriksaan hasil mempertahankan referensi permintaan semula.</p>
      <label>Referensi alokasi semula<input inputMode="numeric" maxLength={19} value={state.recoveryPoolId} disabled={busy || state.review !== null}
        onChange={event => editor.setRecoveryPool(event.target.value)} /></label>
      {!editor.canPropose && !editor.canApprove && <p className={common.help}>Pemulihan memerlukan izin tindakan semula pada seluruh kanal terkait.</p>}
    </Section>}
    <OperationRecovery entries={state.references} journal={editor.journal}
      disabled={state.sending || state.review !== null || !editor.canPropose && !editor.canApprove || !/^[1-9][0-9]{0,18}$/.test(state.recoveryPoolId)}
      recoveryAdapters={{ "stock.source.policy": { lookup: editor.receipt } }} onResolved={editor.reconciled} onFailure={editor.recoveryFailed} onBusyChange={editor.setRecovering} />
    {state.result && <div ref={resultFocus} tabIndex={-1} className={common.success} role="status"><h2>Kebijakan tercatat</h2>
      <p>{policyStatus[state.result.status]}. Referensi pengajuan {state.result.proposalId} · alokasi {state.result.physicalPoolId}.</p>
      {state.result.policyVersion && <p>Kebijakan {state.result.policyId} · versi {state.result.policyVersion}.</p>}
      <p>Muat alokasi kembali untuk memeriksa keadaan terbaru.</p></div>}
    {state.review && <div className={styles.review} ref={reviewFocus} tabIndex={-1}>
      <PolicyFinalReview command={state.review} busy={busy} onSubmit={confirmed => void editor.submitReviewed(confirmed)} onCancel={() => editor.cancelReview()} />
    </div>}
    <Section title="Pilih alokasi" id="policy-pool-heading" busy={state.loadingPool} description="Gunakan referensi alokasi dari pengaturan sumber atau penugasan Anda.">
      <form className={styles.selection} onSubmit={event => { event.preventDefault(); void editor.loadPool(); }}>
        <label>Referensi alokasi<input inputMode="numeric" required maxLength={19} value={state.poolInput}
          disabled={locked} onChange={event => editor.changePool(event.target.value)} /></label>
        <Button type="submit" variant="secondary" disabled={locked || state.loadingPool || !state.poolInput}>Muat alokasi</Button>
      </form>
      {state.loadingPool && <p role="status">Memeriksa alokasi dan kanal…</p>}
      {state.poolId && !state.preview && !state.loadingPool && <p className={common.help}>Alokasi yang dipilih: {state.poolId}. Setiap pemeriksaan pengajuan mengikuti akses Anda saat ini.</p>}
      {state.preview && <div className={styles.preview}><PolicyConfigurationFacts configuration={state.preview.configuration} />
        <PolicyDraftForm preview={state.preview} draft={state.draft} disabled={locked || state.loadingPool}
          onChange={change => editor.changeDraft(change)} onReview={() => editor.reviewProposal()} />
      </div>}
    </Section>
    {(editor.canPropose || editor.canApprove) && state.poolId && <Section title="Periksa pengajuan yang ditugaskan" id="policy-action-heading" busy={state.loadingAction}
      description="Persetujuan dan penolakan memerlukan orang yang berbeda dari pengaju. Penarikan hanya dapat dilakukan oleh pengaju semula.">
      <form className={styles.decision} onSubmit={event => { event.preventDefault(); void editor.loadAction(); }}>
        <label>Referensi pengajuan<input inputMode="numeric" required maxLength={19} value={state.proposalInput}
          disabled={locked} onChange={event => editor.changeProposal(event.target.value)} /></label>
        <label><span id="policy-action-label">Tindakan</span><select aria-labelledby="policy-action-label" value={state.action} disabled={locked} onChange={event => editor.changeAction(event.target.value as SourceDecision)}>
          {editor.canApprove && <><option value="approve">Setujui kebijakan</option><option value="reject">Tolak pengajuan</option></>}
          {editor.canPropose && <option value="withdraw">Tarik pengajuan saya</option>}
        </select></label>
        <Button type="submit" variant="secondary" disabled={locked || state.loadingPool || state.loadingAction || !state.proposalInput || state.action !== "withdraw" && !state.fresh}>Periksa pengajuan</Button>
      </form>
      {state.loadingAction && <p role="status">Memeriksa pengajuan dan kewenangan…</p>}
      {state.actionPreview && <div className={styles.preview}><section>
        {!state.actionPreview.bindingCurrent && <div role="status" className="ui-notice" data-tone="warning">Dasar pengajuan telah berubah. Pengajuan ini dapat ditolak atau ditarik; persetujuan memerlukan dasar yang masih sesuai.</div>}
        <PolicyProposalFacts proposal={state.actionPreview.proposal} />
      </section><form className={styles.form} onSubmit={event => { event.preventDefault(); editor.reviewDecision(); }}>
        <label>Acuan peninjauan<input required maxLength={160} value={state.reviewReference} disabled={locked}
          onChange={event => editor.changeDecision(event.target.value, state.decisionReason)} /></label>
        <label>Alasan keputusan<textarea required rows={3} maxLength={1000} value={state.decisionReason} disabled={locked}
          onChange={event => editor.changeDecision(state.reviewReference, event.target.value)} /></label>
        <Button type="submit" variant="primary" disabled={locked || state.action !== "withdraw" && !state.fresh}>Tinjau keputusan</Button>
      </form></div>}
    </Section>}
    {editor.canRead && state.poolId && <Section title="Riwayat kebijakan" busy={state.loadingHistory} description={<>{formatNumber(BigInt(state.history.total))} pengajuan pada alokasi {state.poolId}.</>}
      actions={<Button variant="secondary" disabled={locked || state.loadingPool || state.loadingHistory} onClick={() => void editor.loadHistory()}>Muat ulang riwayat</Button>}>
      {state.latest && <section className={styles.detail}><h3>Kebijakan terakhir yang disetujui</h3>
        {state.latest.policy ? <><p>Pengajuan #{state.latest.policy.id} · versi {state.latest.policy.policyVersion}</p>
          <p>{state.latest.bindingCurrent ? "Sesuai dengan pengaturan yang diperiksa." : "Dasar kebijakan telah berubah; perlu pemeriksaan ulang."}</p>
          <PolicyInputFacts input={state.latest.policy.input} /></> : <p>Belum ada kebijakan yang disetujui.</p>}
        <p className={common.help}>Kebijakan terakhir dan daftar riwayat diperiksa secara terpisah. Muat ulang untuk memeriksa perubahan terbaru.</p>
      </section>}
      <div className={styles.historyGrid}><div>
        <DataList label="Riwayat kebijakan" items={state.loadingHistory ? [] : state.history.items} getKey={item => item.id} loading={state.loadingHistory}
          empty={state.error === null ? { title: "Belum ada catatan pada halaman ini." } : undefined}
          renderTitle={item => item.reason} renderMeta={item => <>Pengajuan #{item.id}</>} renderBadge={item => <Badge status={policyStatus[item.status]} />}
          onSelect={item => void editor.chooseHistory(item.id)} selectedKey={state.selected?.id ?? null} disabled={locked}
          openLabel={item => item.reason + " Pengajuan #" + item.id + " · " + policyStatus[item.status]}
          pagination={{ label: "Halaman riwayat", start: state.historyOffset + 1, end: state.historyOffset + state.history.items.length, total: Number(state.history.total), disabled: locked,
            hasPrevious: state.historyOffset > 0, hasNext: !(state.historyOffset >= 10000 || BigInt(state.historyOffset + 25) >= BigInt(state.history.total)),
            onPrevious: () => void editor.loadHistory(state.historyOffset - 25), onNext: () => void editor.loadHistory(state.historyOffset + 25) }} />
      </div><section className={styles.detail} aria-busy={state.loadingDetail}>
        {state.loadingDetail ? <StateView kind="loading" title="Memeriksa catatan…" /> : state.selected ? <PolicyProposalFacts proposal={state.selected} /> :
          <><h3>Pilih catatan</h3><p>Periksa kebijakan, dasar pengaturan, dan keputusan yang tercatat.</p></>}
      </section></div>
    </Section>}
  </>;
}