"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import type { components } from "../../api/generated/schema";
import { describeScope, localDateTime, scopeKindLabels, toIsoDate, validReference, type ScopeKind } from "../organizations/access-scope";
import { commandOperations, createAccessGateway, identitySuspensionReason, identitySuspensionTarget, type AccessSubject, type ChangeAction } from "./access-client";
import { AccessFailure, accessMessage, MutationLedger, PreviewSession, stableContent, type GrantRequest, type Scope } from "./workflow";
import { submitTrackedRequest } from "./operation-journal";
import { OperationRecovery, useOperationRecovery } from "./OperationRecovery";
import { AdministrationNavigation } from "./AdministrationNavigation";
import { grantProfileLabel, historicalGrantLabel, permissionLabel } from "./permission-labels";
import { buildHistoricalGrant, historicalBranches, historicalDivisions, historicalNodeLabel, historicalParentMemberships, historicalPermissions, historicalReviewedBody, historicalScopeLabel, type HistoricalDirectoryNode, type HistoricalGrantProposal } from "./historical-grant";
import { rolePermissionSuggestions } from "./role-permission-suggestions";
import styles from "./access.module.css";

type Schemas = components["schemas"];
type Proposal = Schemas["AccessProposalDto"];
type PageState = "loading" | "ready" | "unauthenticated" | "denied" | "unavailable";
type PendingAction = { action: ChangeAction; id: string; version: string; label: string };
type Retry = { label: string; run: () => Promise<void> };

const emptyScope: Scope = { kind: "buyerOwned", organizationId: null, branchId: null, divisionId: null, shopId: null, warehouseId: null, channelId: null };

export function AccessAdministration() {
  const [gateway] = useState(() => createAccessGateway());
  const [ledger] = useState(() => new MutationLedger());
  const [previewSession] = useState(() => new PreviewSession<unknown>());
  const generation = useRef(0);
  const loadedActor = useRef<string | null>(null);
  const decisionInput = useRef<HTMLTextAreaElement>(null);
  const [pageState, setPageState] = useState<PageState>("loading");
  const [current, setCurrent] = useState<Schemas["CurrentAccessDto"] | null>(null);
  const [catalog, setCatalog] = useState<Schemas["AccessCatalogDto"] | null>(null);
  const [proposals, setProposals] = useState<readonly Proposal[]>([]);
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);
  const [subjectReference, setSubjectReference] = useState("");
  const [subject, setSubject] = useState<AccessSubject | null>(null);
  const [membershipId, setMembershipId] = useState("");
  const [templateId, setTemplateId] = useState("buyer");
  const [packs, setPacks] = useState<string[]>([]);
  const [extras, setExtras] = useState<string[]>([]);
  const [delegable, setDelegable] = useState<string[]>([]);
  const [effective, setEffective] = useState(() => localDateTime());
  const [expiry, setExpiry] = useState("");
  const [reason, setReason] = useState("");
  const [mode, setMode] = useState<"grant" | "historical" | "membership">("grant");
  const [historicalKind, setHistoricalKind] = useState<"buyingBranch" | "buyingDivision">("buyingBranch");
  const [historicalBranch, setHistoricalBranch] = useState("");
  const [historicalDivision, setHistoricalDivision] = useState("");
  const [historicalSelectedPermissions, setHistoricalSelectedPermissions] = useState<string[]>([]);
  const [historicalDirectory, setHistoricalDirectory] = useState<{ key: string; items: HistoricalDirectoryNode[] } | null>(null);
  const [historicalDirectoryError, setHistoricalDirectoryError] = useState<{ key: string; error: unknown } | null>(null);
  const [directoryReload, setDirectoryReload] = useState(0);
  const [reviewedHistorical, setReviewedHistorical] = useState<HistoricalGrantProposal | null>(null);
  const [membershipScope, setMembershipScope] = useState<Scope>(emptyScope);
  const [preview, setPreview] = useState<Schemas["AccessPreviewDto"] | null>(null);
  const [previewDraft, setPreviewDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState("");
  const [retry, setRetry] = useState<Retry | null>(null);
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null);
  const [decisionReason, setDecisionReason] = useState("");
  const [suspensionConfirmed, setSuspensionConfirmed] = useState(false);
  const recovery = useOperationRecovery(current?.userId);
  const locked = busy || retry !== null || recovery.locked;
  const suspensionTarget = current ? identitySuspensionTarget(subject, current.userId) : null;
  const suspensionPending = pendingAction?.action === "suspendIdentity";
  const suspensionUncertain = suspensionPending && ledger.isUncertain(`suspendIdentity:${pendingAction.id}`);

  const loadPage = useCallback(async () => {
    const actor = await gateway.current();
    const [definitions, queue] = await Promise.all([gateway.catalog(actor.userId), gateway.proposals(offset, actor.userId)]);
    return [actor, definitions, queue] as const;
  }, [gateway, offset]);
  const clearActorData = useCallback(() => {
    setSubject(null); setSubjectReference(""); setMembershipId(""); setSelected(null); setPendingAction(null);
    setReason(""); setDecisionReason(""); setSuspensionConfirmed(false); setPacks([]); setExtras([]); setDelegable([]); setTemplateId("buyer");
    setMembershipScope(emptyScope); setRetry(null); setPreview(null); setPreviewDraft("");
    setMode("grant"); setHistoricalKind("buyingBranch"); setHistoricalBranch(""); setHistoricalDivision("");
    setHistoricalSelectedPermissions([]); setHistoricalDirectory(null); setHistoricalDirectoryError(null); setReviewedHistorical(null);
    previewSession.invalidate(); ledger.resetAfterReconciliation();
  }, [previewSession, ledger]);
  const applyLoaded = useCallback(([actor, definitions, queue]: Awaited<ReturnType<typeof loadPage>>) => {
    if (loadedActor.current !== actor.userId) clearActorData();
    loadedActor.current = actor.userId;
    setCurrent(actor); setCatalog(definitions); setProposals(queue); setPageState("ready");
  }, [clearActorData]);
  const applyFailure = useCallback((failure: unknown) => {
    clearActorData(); loadedActor.current = null;
    setCurrent(null); setCatalog(null); setProposals([]); setError(failure);
    setPageState(failure instanceof AccessFailure && failure.status === 401 ? "unauthenticated"
      : failure instanceof AccessFailure && failure.status === 403 ? "denied" : "unavailable");
  }, [clearActorData]);
  const refresh = useCallback(async () => {
    const sequence = ++generation.current;
    previewSession.invalidate(); setPreview(null); setReviewedHistorical(null); setDirectoryReload(value => value + 1);
    setPageState("loading");
    try {
      const result = await loadPage();
      if (sequence !== generation.current) return;
      applyLoaded(result);
    } catch (failure) {
      if (sequence !== generation.current) return;
      applyFailure(failure);
    }
  }, [loadPage, applyLoaded, applyFailure, previewSession]);

  useEffect(() => {
    let active = true;
    const sequence = ++generation.current;
    void loadPage().then(result => { if (active && sequence === generation.current) applyLoaded(result); },
      failure => { if (active && sequence === generation.current) applyFailure(failure); });
    return () => { active = false; };
  }, [loadPage, applyLoaded, applyFailure]);

  useEffect(() => {
    if (!retry) return;
    const warnBeforeLeaving = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", warnBeforeLeaving);
    return () => window.removeEventListener("beforeunload", warnBeforeLeaving);
  }, [retry]);

  useEffect(() => { if (pendingAction) decisionInput.current?.focus(); }, [pendingAction]);

  const membership = subject?.memberships.find(item => item.id === membershipId && item.status === "active");
  const historicalMemberships = historicalParentMemberships(subject?.memberships ?? []);
  const historicalMembership = historicalMemberships.find(item => item.id === membershipId);
  const historicalDirectoryKey = stableContent([current?.userId, subject?.userId, historicalMembership?.id, directoryReload]);
  const historicalNodes = historicalDirectory?.key === historicalDirectoryKey ? historicalDirectory.items : [];
  const directoryFailure = historicalDirectoryError?.key === historicalDirectoryKey ? historicalDirectoryError.error : null;
  const directoryLoading = mode === "historical" && !!historicalMembership && !directoryFailure && historicalDirectory?.key !== historicalDirectoryKey;

  useEffect(() => {
    if (mode !== "historical" || !current || !subject || !historicalMembership) return;
    let active = true;
    const actorId = current.userId, userId = subject.userId, parentMembershipId = historicalMembership.id;
    void (async () => {
      const items: HistoricalDirectoryNode[] = [];
      for (let pageOffset = 0; pageOffset <= 10000; pageOffset += 100) {
        const page = await gateway.historicalTargets({ userId, membershipId: parentMembershipId, offset: pageOffset, limit: 100 }, actorId);
        if (!active) return;
        items.push(...page.items);
        if (BigInt(items.length) >= BigInt(page.total)) {
          setHistoricalDirectory({ key: historicalDirectoryKey, items }); setHistoricalDirectoryError(null); return;
        }
        if (!page.items.length) break;
      }
      throw new AccessFailure(400, "historical_directory_incomplete");
    })().catch(failure => {
      if (!active) return;
      setHistoricalDirectoryError({ key: historicalDirectoryKey, error: failure });
      previewSession.invalidate(); setPreview(null); setReviewedHistorical(null);
    });
    return () => { active = false; };
  }, [gateway, mode, current, subject, historicalMembership, historicalDirectoryKey, previewSession]);

  const suggestedIds = new Set(catalog ? rolePermissionSuggestions(catalog, templateId, membership?.scope.kind).map(permission => permission.id) : []);
  const grant = useMemo<GrantRequest | null>(() => {
    const effectiveAt = toIsoDate(effective);
    const expiresAt = toIsoDate(expiry);
    if (!subject || !membership || !effectiveAt || expiry && !expiresAt) return null;
    return { userId: subject.userId, membershipId: membership.id, templateId,
      financePacks: [...packs].sort(), additionalPermissions: [...extras].sort(), delegablePermissions: [...delegable].sort(),
      scope: membership.scope, effectiveAt, expiresAt };
  }, [subject, membership, effective, expiry, templateId, packs, extras, delegable]);

  const historicalGrant = buildHistoricalGrant({ userId: subject?.userId ?? "", membership: historicalMembership, directory: historicalNodes,
    kind: historicalKind, branchId: historicalBranch, divisionId: historicalDivision, permissions: historicalSelectedPermissions, effective, expiry });
  const historicalBody = historicalGrant ? historicalReviewedBody(historicalGrant, reason) : null;
  const previewRequest = mode === "historical" ? historicalBody && { actorId: current?.userId, body: historicalBody } : grant;
  const previewContent = stableContent(previewRequest);
  useEffect(() => { previewSession.replace(previewContent); }, [previewContent, previewSession]);
  const activePreview = previewDraft === previewContent ? preview : null;

  const selectedProposal = proposals.find(proposal => proposal.id === selected);
  const canProposeGrant = current?.allowedActions.includes("access.propose") ?? false;
  const canProposeMembership = current?.allowedActions.includes("membership.propose") ?? false;

  async function loadSubject(event: FormEvent) {
    event.preventDefault();
    if (!validReference(subjectReference)) { setError(new AccessFailure(400, "invalid_id")); return; }
    setBusy(true); setError(null); setMessage("");
    try {
      const result = await gateway.subject(subjectReference, current?.userId);
      setSubject(result); setMembershipId(result.memberships.find(item => item.status === "active")?.id ?? "");
      setSelected(null); setPendingAction(null); setPacks([]); setExtras([]); setDelegable([]);
      if (mode === "historical") setMembershipId(historicalParentMemberships(result.memberships)[0]?.id ?? "");
      setHistoricalBranch(""); setHistoricalDivision(""); setHistoricalSelectedPermissions([]); setHistoricalDirectory(null); setReviewedHistorical(null);
      previewSession.invalidate(); setPreview(null); setPreviewDraft("");
    } catch (failure) { setSubject(null); setError(failure); }
    finally { setBusy(false); }
  }

  async function previewGrant() {
    if (!current || (mode === "historical" ? !historicalBody : !grant)) return;
    const ticket = previewSession.begin(previewContent);
    setPreview(null); setBusy(true); setError(null);
    try {
      const result = mode === "historical" && historicalBody ? await gateway.previewHistoricalGrant(historicalBody.grant, current.userId)
        : await gateway.preview(grant!, current?.userId);
      if (previewSession.accept(ticket, result)) {
        setPreview(result); setPreviewDraft(previewContent); setReviewedHistorical(mode === "historical" ? historicalBody : null);
      }
    } catch (failure) { setError(failure); }
    finally { setBusy(false); }
  }

  async function perform(operation: string, payload: unknown, label: string, send: (key: string) => Promise<unknown>) {
    if (!current) return;
    const canonical = commandOperations[operation.split(":")[0] as keyof typeof commandOperations];
    setBusy(true); setError(null); setMessage("");
    try {
      await submitTrackedRequest(ledger, recovery.journal, current.userId, operation, canonical, payload, send);
      setRetry(null); setPendingAction(null); setDecisionReason("");
      previewSession.invalidate(); setPreview(null);
      setMessage(label);
      await refresh();
      if (subject) {
        try { setSubject(await gateway.subject(subject.userId, current.userId)); }
        catch { setSubject(null); setMessage(`${label} Muat ulang akun untuk memeriksa akses terbaru.`); }
      }
    } catch (failure) {
      setError(failure);
      if (failure instanceof AccessFailure && failure.code === "actor_changed") {
        await refresh(); setError(failure);
      } else if (ledger.isUncertain(operation)) {
        // Suspension recovery only looks up the original receipt; it never retransmits the command.
        if (canonical === "access.identity.suspend") setRetry(null);
        else setRetry({ label: "Periksa kembali permintaan yang sama", run: () => perform(operation, payload, label, send) });
      } else if (failure instanceof AccessFailure && failure.status === 409) {
        previewSession.invalidate(); setPreview(null); setPendingAction(null);
        await refresh();
        if (subject) {
          try { setSubject(await gateway.subject(subject.userId, current.userId)); } catch { setSubject(null); }
        }
      } else if (failure instanceof AccessFailure && failure.status === 403) {
        previewSession.invalidate(); setPreview(null);
        if (canonical === "access.identity.suspend") {
          setSubject(null); setPendingAction(null); setDecisionReason(""); setSuspensionConfirmed(false);
        }
        await refresh();
      }
    } finally { setBusy(false); recovery.refresh(); }
  }

  async function submitProposal(event: FormEvent) {
    event.preventDefault();
    if (!current) return;
    if (mode === "historical") {
      previewSession.replace(previewContent);
      if (!historicalBody || !reviewedHistorical || !activePreview || !previewSession.value || !canProposeGrant) return;
      const body = reviewedHistorical;
      await perform("historical-grant-proposal", body, "Pengajuan akses baca riwayat disimpan. Persetujuan petugas lain masih diperlukan.", key => gateway.proposeHistoricalGrant(body, key, current.userId));
    } else if (mode === "grant") {
      previewSession.replace(previewContent);
      if (!grant || !previewSession.value || !reason.trim() || !canProposeGrant) return;
      const body = { grant, reason: reason.trim() };
      await perform("grant-proposal", body, "Pengajuan akses disimpan. Persetujuan petugas lain masih diperlukan.", key => gateway.proposeGrant(body, key, current.userId));
    } else {
      const effectiveAt = toIsoDate(effective);
      if (!subject || !effectiveAt || !reason.trim() || !canProposeMembership) return;
      const body = { membership: { userId: subject.userId, scope: membershipScope, effectiveAt, expiresAt: toIsoDate(expiry) }, reason: reason.trim() };
      await perform("membership-proposal", body, "Pengajuan keanggotaan disimpan. Pemilik cakupan harus menyetujuinya.", key => gateway.proposeMembership(body, key, current.userId));
    }
  }

  async function confirmAction(event: FormEvent) {
    event.preventDefault();
    if (!pendingAction || !decisionReason.trim() || !current || locked) return;
    const action = pendingAction;
    let reviewedReason = decisionReason.trim();
    if (action.action === "suspendIdentity") {
      if (!suspensionConfirmed) return;
      if (!suspensionTarget || suspensionTarget.id !== action.id || suspensionTarget.version !== action.version) {
        setPendingAction(null); setSuspensionConfirmed(false); setError(new AccessFailure(409, "identity_suspension_target_changed")); return;
      }
      try { reviewedReason = identitySuspensionReason(decisionReason); } catch (failure) { setError(failure); return; }
    }
    const payload = { action: action.action, id: action.id, version: action.version, reason: reviewedReason };
    await perform(`${action.action}:${action.id}`, payload, action.action === "suspendIdentity" ? "Akun telah dinonaktifkan. Seluruh akses dan proses masuk akun tersebut dihentikan." : "Perubahan akses telah disimpan.",
      key => gateway.change(action.action, action.id, action.version, payload.reason, key, current.userId));
  }

  function invalidateDraftPreview() {
    previewSession.invalidate(); setPreview(null); setPreviewDraft(""); setReviewedHistorical(null);
  }

  function startNew(nextMode: "grant" | "historical" | "membership") {
    setSelected(null); setPendingAction(null); setMode(nextMode); setError(null); setMessage("");
    previewSession.invalidate(); setPreview(null); setPreviewDraft(""); setReviewedHistorical(null);
    if (nextMode === "historical") {
      setMembershipId(historicalMemberships[0]?.id ?? ""); setHistoricalBranch(""); setHistoricalDivision("");
      setPacks([]); setExtras([]); setDelegable([]);
      setHistoricalSelectedPermissions([]); setDirectoryReload(value => value + 1);
    }
  }

  return <main className={styles.page}>
    <header className={styles.header}>
      <div><Link className={styles.brand} href="/identity/account" onClick={event => { if (locked) event.preventDefault(); }} aria-disabled={locked}>Unit Toko</Link><h1>Kelola akses</h1>
        <p>Ajukan tugas yang sesuai, periksa cakupannya, lalu minta persetujuan independen.</p></div>
      <button className={styles.secondary} onClick={() => { setError(null); void refresh(); }} disabled={locked || pageState === "loading"}>Muat ulang</button>
    </header>

    {current && <AdministrationNavigation current="access" actions={current.allowedActions} locked={locked} />}
    {recovery.error !== null && <p role="alert" className={styles.alert}>{accessMessage(recovery.error)}</p>}
    {current && <OperationRecovery key={current.userId} entries={recovery.entries} journal={recovery.journal} disabled={busy} onResolved={() => {
      recovery.refresh();
      if (recovery.journal.list(current.userId).length === 0) {
        ledger.resetAfterReconciliation(); setRetry(null); setError(null); setPendingAction(null);
        previewSession.invalidate(); setPreview(null);
        const reloadSubject = recovery.entries.some(entry => entry.operation === "access.identity.suspend") ? subject?.userId : null;
        void refresh().then(async () => {
          if (!reloadSubject || loadedActor.current !== current.userId) return;
          try {
            const latest = await gateway.subject(reloadSubject, current.userId);
            if (loadedActor.current === current.userId) setSubject(previous => previous?.userId === reloadSubject ? latest : previous);
          } catch {
            if (loadedActor.current === current.userId) setSubject(previous => previous?.userId === reloadSubject ? null : previous);
          }
        });
      }
    }} />}

    {message && <p className={styles.success} role="status">{message}</p>}
    {error !== null && <div className={styles.alert} role="alert"><p>{suspensionUncertain ? "Hasil penonaktifan belum dapat dipastikan. Periksa hasil melalui Pemulihan permintaan; jangan kirim ulang keputusan. Referensi permintaan akun semula tetap disimpan." : suspensionMessage(retry ? new AccessFailure(503, "commit_outcome_unknown") : error)}</p>
      {error instanceof AccessFailure && error.code === "step_up_required" && !locked && <Link href="/identity/account?returnPath=%2Fadministration%2Faccess">Verifikasi akun untuk melanjutkan</Link>}
      {retry && <p>Tetap di halaman ini sampai hasil permintaan yang sama terkonfirmasi.</p>}
      {error instanceof AccessFailure && error.traceId && <small>Referensi bantuan: {error.traceId}</small>}
      {retry && <button className={styles.primary} onClick={() => void retry.run()} disabled={busy}>{retry.label}</button>}
    </div>}

    {pageState === "loading" && <section className={styles.state} aria-live="polite" aria-busy="true"><h2>Memeriksa akses</h2><p>Memuat keanggotaan, izin, dan pengajuan terbaru.</p></section>}
    {pageState !== "loading" && pageState !== "ready" && <section className={styles.state}>
      <h2>{pageState === "unauthenticated" ? "Sesi terverifikasi diperlukan" : pageState === "denied" ? "Halaman ini memerlukan izin pengelolaan akses" : "Layanan akses belum tersedia"}</h2>
      <p>{pageState === "unauthenticated" ? "Masuk dengan akun organisasi terverifikasi untuk memeriksa akses Anda." : pageState === "denied" ? "Pengelola atau pemberi persetujuan yang ditunjuk dapat meninjau pengajuan sesuai cakupan tugasnya." : "Data belum dapat dimuat. Pengajuan dan perubahan akses tetap dinonaktifkan."}</p>
      {pageState === "unauthenticated" && <Link className={styles.primary} href="/identity/login?returnPath=%2Fadministration%2Faccess">Masuk ke Unit Toko</Link>}
      <button className={styles.secondary} onClick={() => void refresh()} disabled={locked}>Periksa kembali</button>
    </section>}

    {pageState === "ready" && current && catalog && <>
      <section className={styles.accountSection} aria-labelledby="account-heading">
        <h2 id="account-heading">Akun yang dikelola</h2>
        <form onSubmit={loadSubject} className={styles.lookup}>
          <label>Referensi akun<input value={subjectReference} onChange={event => { setSubjectReference(event.target.value); setSubject(null); setPendingAction(null); setDecisionReason(""); setSuspensionConfirmed(false); setMembershipId(""); setPacks([]); setExtras([]); setDelegable([]); setHistoricalBranch(""); setHistoricalDivision(""); setHistoricalSelectedPermissions([]); setHistoricalDirectory(null); setReviewedHistorical(null); previewSession.invalidate(); setPreview(null); setPreviewDraft(""); }}
            inputMode="numeric" autoComplete="off" placeholder="Contoh: 1032" maxLength={19} required disabled={locked || suspensionPending} aria-describedby="account-help" /></label>
          <button className={styles.secondary} disabled={locked || suspensionPending || !validReference(subjectReference)}>Tampilkan akun</button>
        </form>
        <p id="account-help" className={styles.help}>Gunakan referensi akun yang diberikan pengelola.</p>
        {subject && <div className={styles.subjectSummary}>
          <p><strong>Akun {subject.userId}</strong> <span className={styles.status}>{subject.status === "suspended" ? "Dinonaktifkan" : statusLabel(subject.status)}</span></p>
          {subject.status === "suspended" && <p className={styles.help}>Akun ini tidak dapat masuk atau menggunakan izin di bawah. Keanggotaan dan izin tetap tercatat untuk peninjauan petugas.</p>}
          {suspensionTarget && <div className={styles.actions}><button type="button" className={styles.danger} disabled={locked || suspensionPending} onClick={() => {
            setPendingAction({ ...suspensionTarget, label: `Nonaktifkan akun ${suspensionTarget.id}` }); setSelected(null); setDecisionReason(""); setSuspensionConfirmed(false); setError(null); setMessage("");
          }}>Nonaktifkan akun</button></div>}
          <div className={styles.accessLists}>
            <section><h3>Keanggotaan</h3>{subject.memberships.length === 0 && <p>Belum ada keanggotaan yang dapat ditampilkan.</p>}
              <ul>{subject.memberships.map(item => <li key={item.id}><div><strong>Keanggotaan {item.id}</strong><p>{describeScope(item.scope)}</p><small>{statusLabel(item.status)}</small><Validity effectiveAt={item.effectiveAt} expiresAt={item.expiresAt} /></div>
                {item.allowedActions.includes("suspend") && <button className={styles.danger} disabled={locked} onClick={() => { setPendingAction({ action: "suspendMembership", id: item.id, version: item.version, label: `Tangguhkan keanggotaan ${item.id}` }); setSelected(null); }}>Tangguhkan</button>}
              </li>)}</ul>
            </section>
            <section><h3>Izin yang tercatat</h3>{subject.grants.length === 0 && <p>Belum ada izin akses yang dapat ditampilkan.</p>}
              <ul>{subject.grants.map(item => <li key={item.id}><div><strong>{grantProfileLabel(item.templateId, catalog.roles)}</strong><p>{item.templateId === "historical-read" ? historicalScopeLabel(item.scope) : describeScope(item.scope)}</p><small>{statusLabel(item.status)} · Izin {item.id}</small><Validity effectiveAt={item.effectiveAt} expiresAt={item.expiresAt} />
                <details className={styles.recordedPermissions}><summary>Lihat izin dan wewenang</summary><PermissionList permissions={item.permissions} />{item.templateId === "historical-read" ? <HistoricalReadNotice scope={item.scope} /> : <><h3>Wewenang pemberian izin</h3><DelegationList permissions={item.delegablePermissions} /></>}</details></div>
                {item.allowedActions.includes("revoke") && <button className={styles.danger} disabled={locked} onClick={() => { setPendingAction({ action: "revokeGrant", id: item.id, version: item.version, label: `Cabut ${item.templateId === "historical-read" ? "akses baca riwayat" : "izin"} ${item.id}` }); setSelected(null); }}>Cabut izin</button>}
              </li>)}</ul>
            </section>
          </div>
        </div>}
      </section>

      <div className={styles.desk}>
        <aside className={styles.queue} aria-labelledby="queue-heading">
          <div className={styles.queueHeader}><h2 id="queue-heading">Antrian pengajuan</h2><span>{proposals.length} pada halaman ini</span></div>
          <div className={styles.newActions}>
            {canProposeGrant && <button className={styles.secondary} disabled={locked} onClick={() => startNew("grant")}>Ajukan izin</button>}
            {canProposeGrant && <button className={styles.secondary} disabled={locked} onClick={() => startNew("historical")}>Ajukan akses baca riwayat</button>}
            {canProposeMembership && <button className={styles.secondary} disabled={locked} onClick={() => startNew("membership")}>Ajukan keanggotaan</button>}
          </div>
          {proposals.length === 0 ? <p className={styles.empty}>Tidak ada pengajuan dalam cakupan Anda pada halaman ini.</p> : <ul className={styles.queueList}>
            {proposals.map(item => <li key={item.id}><button disabled={locked} className={selected === item.id ? styles.selectedRow : styles.queueRow}
              aria-pressed={selected === item.id} onClick={() => { setSelected(item.id); setPendingAction(null); setError(null); }}>
              <strong>{item.kind === "membership" ? "Keanggotaan" : item.templateId === "historical-read" ? historicalGrantLabel : "Izin akses"} untuk akun {item.subjectId}</strong>
              <span>{item.templateId === "historical-read" ? historicalScopeLabel(item.scope) : describeScope(item.scope)}</span><small>{statusLabel(item.status)} · Pengajuan {item.id}</small>
            </button></li>)}
          </ul>}
          <nav className={styles.pagination} aria-label="Halaman pengajuan">
            <button className={styles.secondary} disabled={locked || offset === 0} onClick={() => { setSelected(null); setPageState("loading"); setOffset(value => Math.max(0, value - 25)); }}>Sebelumnya</button>
            <button className={styles.secondary} disabled={locked || proposals.length < 25 || offset >= 9975} onClick={() => { setSelected(null); setPageState("loading"); setOffset(value => value + 25); }}>Berikutnya</button>
          </nav>
        </aside>

        <section className={styles.detail} aria-label="Rincian dan pengajuan akses">
          {pendingAction ? <form onSubmit={confirmAction} className={styles.form} aria-label={suspensionPending ? "Konfirmasi penonaktifan akun" : undefined}>
            <h2>{pendingAction.label}</h2><p>Perubahan ini dicatat bersama akun pelaksana dan alasan. Server memeriksa kembali izin dan versi terbaru.</p>
            {suspensionPending && <div className={styles.review} role="note"><h3>Seluruh akses akun dihentikan</h3><p>Akun <strong>{pendingAction.id}</strong> tidak dapat masuk atau menggunakan aksesnya pada semua cabang dan unit. Sesi yang sudah ada tidak dapat melanjutkan tindakan.</p><p>Riwayat tetap tersimpan. Penonaktifan ini tidak mengalihkan tugas, mengubah izin menjadi izin baru, atau mengaktifkan akun kembali secara otomatis.</p></div>}
            {selectedProposal && pendingAction.id === selectedProposal.id && <ProposalReview proposal={selectedProposal} />}
            <label><span id="access-decision-reason-label">Alasan keputusan</span><textarea aria-labelledby="access-decision-reason-label" ref={decisionInput} required maxLength={1000} value={decisionReason} onChange={event => { setDecisionReason(event.target.value); if (suspensionPending) setSuspensionConfirmed(false); }} disabled={locked} rows={4} /></label>
            {suspensionPending && <label className={styles.checkbox}><input type="checkbox" required checked={suspensionConfirmed} disabled={locked} onChange={event => setSuspensionConfirmed(event.target.checked)} /><span>Saya memahami bahwa seluruh akses dan proses masuk akun ini akan dihentikan.</span></label>}
            <div className={styles.actions}><button className={suspensionPending ? styles.danger : styles.primary} disabled={locked || !decisionReason.trim() || suspensionPending && !suspensionConfirmed}>{suspensionPending ? "Pastikan dan nonaktifkan akun" : "Simpan keputusan"}</button><button type="button" className={styles.secondary} disabled={locked} onClick={() => { setPendingAction(null); setSuspensionConfirmed(false); }}>Batal</button></div>
          </form> : selectedProposal ? <ProposalDetails proposal={selectedProposal} catalog={catalog} disabled={locked} onApprove={() => {
            setDecisionReason(""); setPendingAction({ action: selectedProposal.kind === "membership" ? "approveMembership" : "approveGrant", id: selectedProposal.id, version: selectedProposal.version, label: `Setujui pengajuan ${selectedProposal.id}` });
          }} onDecision={action => {
            setDecisionReason(""); setPendingAction({ action, id: selectedProposal.id, version: selectedProposal.version,
              label: `${action === "rejectProposal" ? "Tolak" : "Tarik"} pengajuan ${selectedProposal.id}` });
          }} /> : <form onSubmit={submitProposal} className={styles.form}>
            <h2>{mode === "grant" ? "Ajukan izin akses" : mode === "historical" ? "Ajukan akses baca riwayat" : "Ajukan keanggotaan"}</h2>
            {!subject ? <p className={styles.empty}>Tampilkan akun terlebih dahulu untuk memeriksa keanggotaan dan cakupan yang disetujui.</p> : <>
              <p>Akun <strong>{subject.userId}</strong>. Setiap izin baru memerlukan persetujuan petugas lain yang ditunjuk.</p>
              <fieldset disabled={locked || (mode !== "membership" ? !canProposeGrant : !canProposeMembership)} onChangeCapture={invalidateDraftPreview} className={styles.fields}>
                <legend className={styles.srOnly}>Rincian pengajuan</legend>
                {mode === "grant" ? <>
                  <label>Keanggotaan aktif<select required value={membershipId} onChange={event => { setMembershipId(event.target.value); setPacks([]); setExtras([]); setDelegable([]); previewSession.invalidate(); setPreview(null); setPreviewDraft(""); }}><option value="">Pilih keanggotaan</option>
                    {subject.memberships.filter(item => item.status === "active").map(item => <option key={item.id} value={item.id}>{item.id}: {describeScope(item.scope)}</option>)}
                  </select></label>
                  {membership ? <p className={styles.scopeSummary}>{describeScope(membership.scope)}</p> : <p className={styles.help}>Izin hanya dapat diajukan untuk keanggotaan yang telah disetujui.</p>}
                  <label>Peran awal<select value={templateId} onChange={event => { setTemplateId(event.target.value); setPacks([]); setExtras([]); setDelegable([]); previewSession.invalidate(); setPreview(null); setPreviewDraft(""); }}>
                    {catalog.roles.map(role => <option key={role.id} value={role.id}>{role.label}</option>)}
                  </select></label>
                  {templateId === "finance" && <fieldset className={styles.choiceGroup}><legend>Paket tugas keuangan</legend><p className={styles.help}>Peran Keuangan tanpa paket tidak memberikan izin transaksi. Pilih hanya tugas yang diperlukan.</p>
                    {catalog.financePacks.map(pack => <label className={styles.checkbox} key={pack.id}><input type="checkbox" checked={packs.includes(pack.id)} onChange={() => toggle(pack.id, packs, setPacks)} /><span>{pack.label}</span></label>)}
                  </fieldset>}
                  <details className={styles.permissions}><summary>Izin tambahan yang diminta</summary><p className={styles.help}>Termasuk PIC dan verifikasi pembayaran jika diperlukan. Saran peran tidak dicentang otomatis. Cakupan dan penunjukan tetap harus disetujui secara terpisah.</p>
                    {catalog.permissions.map(permission => <label className={styles.checkbox} key={permission.id}><input type="checkbox" checked={extras.includes(permission.id)} onChange={() => toggle(permission.id, extras, setExtras)} /><span>{permissionLabel(permission.id, permission.label)}{suggestedIds.has(permission.id) && <small>Disarankan untuk peran ini</small>}{permission.requiresFreshMfa && <small>Memerlukan verifikasi dua langkah terbaru</small>}</span></label>)}
                  </details>
                  <details className={styles.permissions}><summary>Wewenang pemberian izin</summary><p className={styles.help}>Isi hanya untuk petugas pemberi persetujuan yang ditunjuk. Memiliki izin transaksi tidak otomatis memberikan wewenang ini.</p>
                    {catalog.permissions.map(permission => <label className={styles.checkbox} key={permission.id}><input type="checkbox" checked={delegable.includes(permission.id)} onChange={() => toggle(permission.id, delegable, setDelegable)} /><span>{permissionLabel(permission.id, permission.label)}</span></label>)}
                  </details>
                </> : mode === "historical" ? <>
                  <p className={styles.scopeSummary}>Pilih izin membaca riwayat atau laporan secara eksplisit. Akses ini hanya untuk membaca, termasuk unit yang ditangguhkan; tidak mengizinkan transaksi baru atau pemberian izin kepada orang lain.</p>
                  <label>Keanggotaan organisasi pembeli aktif<select required value={membershipId} onChange={event => {
                    setMembershipId(event.target.value); setHistoricalBranch(""); setHistoricalDivision(""); setHistoricalDirectory(null);
                    setPacks([]); setExtras([]); setDelegable([]);
                    previewSession.invalidate(); setPreview(null); setPreviewDraft(""); setReviewedHistorical(null);
                  }}><option value="">Pilih keanggotaan organisasi</option>{historicalMemberships.map(item => <option key={item.id} value={item.id}>Keanggotaan {item.id} · Organisasi {item.scope.organizationId}</option>)}</select></label>
                  {!historicalMemberships.length && <p className={styles.help}>Akun memerlukan keanggotaan seluruh organisasi pembeli yang telah disetujui dan sedang aktif.</p>}
                  {directoryLoading && <p role="status">Memuat cabang dan divisi yang tersedia…</p>}
                  {directoryFailure !== null && <div role="alert"><p>Daftar unit belum dapat dimuat. {accessMessage(directoryFailure)}</p><button type="button" className={styles.secondary} onClick={() => setDirectoryReload(value => value + 1)}>Periksa daftar unit kembali</button></div>}
                  {historicalMembership && !directoryLoading && directoryFailure === null && historicalNodes.length === 0 && <p className={styles.help}>Tidak ada cabang atau divisi yang dapat dipilih dalam organisasi ini.</p>}
                  <label>Cakupan akses baca<select value={historicalKind} onChange={event => {
                    setHistoricalKind(event.target.value as "buyingBranch" | "buyingDivision"); setHistoricalDivision("");
                  }}><option value="buyingBranch">Cabang beserta divisinya</option><option value="buyingDivision">Satu divisi</option></select></label>
                  <label>Cabang yang dituju<select required disabled={directoryLoading || !historicalMembership || directoryFailure !== null} value={historicalBranch} onChange={event => { setHistoricalBranch(event.target.value); setHistoricalDivision(""); }}>
                    <option value="">Pilih cabang</option>{historicalBranches(historicalNodes, historicalMembership?.scope.organizationId).map(node => <option key={node.id} value={node.id}>{historicalNodeLabel(node)}</option>)}
                  </select></label>
                  {historicalKind === "buyingDivision" && <label>Divisi yang dituju<select required disabled={!historicalBranch || directoryLoading} value={historicalDivision} onChange={event => setHistoricalDivision(event.target.value)}>
                    <option value="">Pilih divisi</option>{historicalDivisions(historicalNodes, historicalBranch).map(node => <option key={node.id} value={node.id}>{historicalNodeLabel(node)}</option>)}
                  </select></label>}
                  <HistoricalReadNotice scope={{ kind: historicalKind }} />
                  <fieldset className={styles.choiceGroup}><legend>Izin baca yang diminta</legend><p className={styles.help}>Pilih sedikitnya satu izin. Tidak ada izin yang dipilih otomatis.</p>
                    {historicalPermissions.map(permission => <label className={styles.checkbox} key={permission}><input type="checkbox" checked={historicalSelectedPermissions.includes(permission)} onChange={() => toggle(permission, historicalSelectedPermissions, setHistoricalSelectedPermissions)} /><span>{permissionLabel(permission)}</span></label>)}
                  </fieldset>
                </> : <ScopeFields value={membershipScope} onChange={setMembershipScope} />}
                <div className={styles.twoFields}><label>Mulai berlaku<input type="datetime-local" value={effective} onChange={event => setEffective(event.target.value)} required /></label>
                  <label>Berakhir <span className={styles.optional}>(opsional)</span><input type="datetime-local" value={expiry} onChange={event => setExpiry(event.target.value)} min={effective} /></label></div>
                <p className={styles.help}>Waktu mengikuti zona waktu perangkat Anda. Cakupan organisasi, toko, dan kanal diperiksa kembali oleh server.</p>
                <label><span id="access-proposal-reason-label">Alasan pengajuan</span><textarea aria-labelledby="access-proposal-reason-label" value={reason} onChange={event => setReason(event.target.value)} required maxLength={mode === "historical" ? 500 : 1000} rows={3} placeholder="Jelaskan tugas dan kebutuhan akses akun ini." /></label>
                {mode === "historical" && <p className={styles.help}>Maksimal 500 karakter. Perubahan isian memerlukan pratinjau ulang.</p>}
                {mode !== "membership" && <button type="button" className={styles.secondary} disabled={mode === "historical" ? !historicalBody : !grant} onClick={() => void previewGrant()}>Periksa pratinjau izin</button>}
                {mode !== "membership" && activePreview && <section className={styles.preview} aria-live="polite"><h3>Pratinjau dari server</h3>{mode === "historical" && <strong>{historicalGrantLabel}</strong>}<p>{mode === "historical" ? historicalScopeLabel(activePreview.scope, historicalNodes) : describeScope(activePreview.scope)}</p>
                  <p>Keanggotaan {activePreview.membershipId}{mode === "grant" ? `; versi peran ${activePreview.templateVersion}` : ""}.</p><Validity effectiveAt={activePreview.effectiveAt} expiresAt={activePreview.expiresAt} />
                  <PermissionList permissions={activePreview.permissions} />{mode === "historical" ? <><HistoricalReadNotice scope={activePreview.scope} /><p className={styles.preserve}>Alasan: {reviewedHistorical?.reason}</p></> : <><h3>Wewenang pemberian izin</h3><DelegationList permissions={activePreview.delegablePermissions} /></>}
                  <p className={styles.help}>{activePreview.existingPermissions.length} izin sudah tercatat pada cakupan ini. Pengajuan tetap menunggu persetujuan independen.</p>
                </section>}
                <button className={styles.primary} disabled={!reason.trim() || !toIsoDate(effective) || mode === "grant" && (!grant || !activePreview) || mode === "historical" && (!historicalBody || !activePreview || !reviewedHistorical)}>Ajukan untuk persetujuan</button>
              </fieldset>
            </>}
          </form>}
        </section>
      </div>
    </>}
  </main>;
}

function ProposalDetails({ proposal, catalog, disabled, onApprove, onDecision }: { proposal: Proposal; catalog: Schemas["AccessCatalogDto"]; disabled: boolean; onApprove: () => void; onDecision: (action: "rejectProposal" | "withdrawProposal") => void }) {
  return <div className={styles.form}><h2>Pengajuan {proposal.id}</h2><dl className={styles.facts}>
    <div><dt>Akun penerima</dt><dd>{proposal.subjectId}</dd></div><div><dt>Diajukan oleh</dt><dd>Akun {proposal.requestedBy}</dd></div>
    <div><dt>Status</dt><dd>{statusLabel(proposal.status)}</dd></div><div><dt>Diajukan pada</dt><dd>{new Date(proposal.createdAt).toLocaleString("id-ID")}</dd></div>
    {proposal.templateId && <div><dt>{proposal.templateId === "historical-read" ? "Profil izin" : "Peran awal"}</dt><dd>{grantProfileLabel(proposal.templateId, catalog.roles)}</dd></div>}
  </dl><ProposalReview proposal={proposal} />
    {proposal.allowedActions.includes("approve") ? <><p className={styles.help}>Persetujuan hanya berlaku untuk cakupan dan izin pada pengajuan ini. Izin terbaru serta independensi akun diperiksa saat disimpan.</p><button className={styles.primary} disabled={disabled} onClick={onApprove}>Tinjau dan setujui</button></>
      : <p className={styles.help}>Tidak ada tindakan persetujuan yang tersedia untuk akun Anda pada pengajuan ini.</p>}
    <div className={styles.actions}>
      {proposal.allowedActions.includes("reject") && <button className={styles.danger} disabled={disabled} onClick={() => onDecision("rejectProposal")}>Tolak pengajuan</button>}
      {proposal.allowedActions.includes("withdraw") && <button className={styles.secondary} disabled={disabled} onClick={() => onDecision("withdrawProposal")}>Tarik pengajuan saya</button>}
    </div>
  </div>;
}

function ProposalReview({ proposal }: { proposal: Proposal }) {
  const historical = proposal.templateId === "historical-read";
  return <div className={styles.review}><section><h3>Akun dan cakupan yang ditinjau</h3>{historical && <strong>{historicalGrantLabel}</strong>}<p>Akun {proposal.subjectId}; diajukan oleh akun {proposal.requestedBy}.</p><p>{historical ? historicalScopeLabel(proposal.scope) : describeScope(proposal.scope)}</p>
    <p>{proposal.membershipId ? `Keanggotaan ${proposal.membershipId}` : "Pengajuan keanggotaan baru"}{!historical && proposal.templateVersion !== null ? `; versi peran ${proposal.templateVersion}` : ""}.</p>
    <Validity effectiveAt={proposal.effectiveAt} expiresAt={proposal.expiresAt} />
  </section><section><h3>Alasan pengajuan</h3><p className={styles.preserve}>{proposal.reason}</p></section>
    {proposal.kind !== "membership" && <section><h3>Izin yang diajukan</h3><PermissionList permissions={proposal.permissions} /></section>}
    {historical ? <HistoricalReadNotice scope={proposal.scope} /> : <section><h3>Wewenang pemberian izin</h3><DelegationList permissions={proposal.delegablePermissions} /></section>}
  </div>;
}

function HistoricalReadNotice({ scope }: { scope: Scope }) {
  return <p className={styles.help}>{scope.kind === "buyingBranch" ? "Akses cabang mencakup divisi yang sah di bawah cabang tersebut, termasuk unit yang ditangguhkan. " : "Akses hanya berlaku untuk divisi yang dipilih, termasuk saat unit ditangguhkan. "}Hanya membaca riwayat atau laporan yang dipilih. Tidak mengizinkan transaksi baru atau pemberian izin.</p>;
}

function Validity({ effectiveAt, expiresAt }: { effectiveAt: string; expiresAt: string | null }) {
  return <p className={styles.help}>Mulai: {new Date(effectiveAt).toLocaleString("id-ID")}. Berakhir: {expiresAt ? new Date(expiresAt).toLocaleString("id-ID") : "tanpa tanggal akhir"}.</p>;
}

function DelegationList({ permissions }: { permissions: readonly string[] }) {
  return permissions.length === 0 ? <p>Tidak ada wewenang pemberian izin yang diminta.</p> : <PermissionList permissions={permissions} />;
}

function PermissionList({ permissions }: { permissions: readonly string[] }) {
  return permissions.length === 0 ? <p>Tidak ada izin transaksi dalam pilihan ini.</p>
    : <ul className={styles.permissionList}>{permissions.map(permission => <li key={permission}>{permissionLabel(permission)}</li>)}</ul>;
}

function ScopeFields({ value, onChange }: { value: Scope; onChange: (scope: Scope) => void }) {
  const fields = value.kind === "system" ? [] : value.kind.startsWith("buy")
    ? [["organizationId", "Referensi organisasi pembeli"], ...(value.kind === "buyingOrganization" ? [] : [["branchId", "Referensi cabang"], ...(value.kind === "buyingBranch" ? [] : [["divisionId", "Referensi divisi"]])])]
    : [["organizationId", "Referensi organisasi pemenuhan"], ["shopId", "Referensi toko"], ["channelId", "Referensi kanal"], ...(value.kind === "fulfillmentShop" ? [] : [["warehouseId", "Referensi gudang"]])];
  return <><label>Cakupan keanggotaan<select value={value.kind} onChange={event => onChange({ ...emptyScope, kind: event.target.value as ScopeKind })}>
    {Object.entries(scopeKindLabels).map(([kind, label]) => <option key={kind} value={kind}>{label}</option>)}
  </select></label><p className={styles.help}>Gunakan referensi unit yang disetujui pemilik organisasi. Keanggotaan baru tidak aktif sebelum mendapat persetujuan independen.</p>
    <div className={styles.twoFields}>{fields.map(([field, label]) => <label key={field}>{label}<input inputMode="numeric" autoComplete="off" maxLength={19}
      value={typeof value[field as keyof Scope] === "string" ? String(value[field as keyof Scope]) : ""}
      onChange={event => onChange({ ...value, [field]: event.target.value || null })}
      required={field === "organizationId" || field === "shopId" || field === "channelId" || value.kind === "buyingDivision" || value.kind === "buyingBranch" || value.kind === "fulfillmentWarehouse"}
      pattern="[1-9][0-9]{0,18}" /></label>)}</div></>;
}

function toggle(value: string, values: string[], update: (values: string[]) => void) { update(values.includes(value) ? values.filter(item => item !== value) : [...values, value]); }
function statusLabel(value: string) { return ({ active: "Aktif", pending: "Menunggu persetujuan", approved: "Disetujui", suspended: "Ditangguhkan", revoked: "Dicabut", rejected: "Ditolak", withdrawn: "Ditarik pengaju" } as Record<string, string>)[value] ?? value; }

function suspensionMessage(error: unknown): string {
  if (error instanceof AccessFailure) {
    if (error.code === "identity_self_suspension_forbidden") return "Akun yang sedang Anda gunakan tidak dapat dinonaktifkan. Gunakan petugas berwenang lain.";
    if (error.code === "identity_suspension_reason_invalid") return "Isi alasan penonaktifan yang jelas, maksimal 1.000 karakter.";
    if (error.code === "identity_suspension_target_changed") return "Akun atau versi akses berubah. Tampilkan akun terbaru dan tinjau kembali sebelum menonaktifkannya.";
    if (error.code === "access_subject_invalid") return "Data akun belum dapat dibaca dengan aman. Muat ulang untuk memeriksa status dan tindakan yang diizinkan; perubahan tetap dinonaktifkan.";
  }
  return accessMessage(error);
}
