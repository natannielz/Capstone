"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import type { components } from "../../api/generated/schema";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery, useOperationRecovery } from "../administration/OperationRecovery";
import { submitTrackedRequest } from "../administration/operation-journal";
import { AccessFailure, MutationLedger, accessMessage } from "../administration/workflow";
import { createIdentityAdministrationGateway, identityOperations } from "./identity-administration-client";
import { AccountDirectory, InvitationQueue, type IdentityDecision } from "./IdentityAdministrationPanels";
import { IdentityDecisionReview } from "./IdentityDecisionReview";
import { InvitationProposalForm } from "./InvitationProposalForm";
import { canManageAccounts } from "./identity-workflow";
import styles from "../administration/access.module.css";

type Schemas = components["schemas"];
type Loaded = { current: Schemas["CurrentAccessDto"]; catalog: Schemas["AccessCatalogDto"] };

export function IdentityAdministration() {
  const [access] = useState(() => createAccessGateway());
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [revision, setRevision] = useState(0);
  const sequence = useRef(0);
  const refresh = useCallback(async () => {
    const ticket = ++sequence.current;
    setLoading(true); setError(null); setLoaded(null);
    try {
      const current = await access.current();
      if (!canManageAccounts(current.allowedActions)) throw new AccessFailure(403, "permission_denied");
      const catalog = await access.catalog(current.userId);
      if (ticket === sequence.current) { setLoaded({ current, catalog }); setRevision(value => value + 1); }
    } catch (failure) { if (ticket === sequence.current) setError(failure); }
    finally { if (ticket === sequence.current) setLoading(false); }
  }, [access]);
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => { if (active) void refresh(); });
    const restore = (event: PageTransitionEvent) => { if (event.persisted) window.location.reload(); };
    window.addEventListener("pageshow", restore);
    return () => { active = false; window.removeEventListener("pageshow", restore); };
  }, [refresh]);
  const onFailure = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === "actor_changed")) {
      sequence.current++; setLoaded(null); setLoading(false); setError(failure);
    }
  }, []);
  return <main className={styles.page}>
    <header className={styles.header}><div><Link className={styles.brand} href="/identity/account">Unit Toko</Link><h1>Akun dan undangan</h1>
      <p>Kelola akun terverifikasi, tinjau undangan, dan catat kebutuhan pemulihan sesuai lingkup tugas.</p></div></header>
    {error !== null && <p role="alert" className={styles.alert}>{accessMessage(error)}</p>}
    {loaded ? <IdentityAdministrationBody key={`${loaded.current.userId}:${revision}`} {...loaded} onFailure={onFailure} onRefresh={refresh} />
      : <section className={styles.state} aria-busy={loading}><h2>{loading ? "Memeriksa akses" : error instanceof AccessFailure && error.status === 401 ? "Sesi terverifikasi diperlukan" : "Pengelolaan akun belum tersedia"}</h2>
        <Link href="/identity/login" className={styles.primary}>Masuk dengan akun organisasi</Link><button className={styles.secondary} disabled={loading} onClick={() => void refresh()}>Periksa kembali</button></section>}
  </main>;
}

function IdentityAdministrationBody({ current, catalog, onFailure, onRefresh }: Loaded & { onFailure: (error: unknown) => void; onRefresh: () => Promise<void> }) {
  const [gateway] = useState(() => createIdentityAdministrationGateway());
  const [ledger] = useState(() => new MutationLedger());
  const recovery = useOperationRecovery(current.userId);
  const [selected, setSelected] = useState<IdentityDecision | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState("");
  const [revision, setRevision] = useState(0);
  const [formRevision, setFormRevision] = useState(0);
  const [retry, setRetry] = useState<{ run: () => Promise<void> } | null>(null);
  const mounted = useRef(true);
  const locked = busy || retry !== null || recovery.locked;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!locked) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [locked]);
  async function command(logical: string, operation: string, payload: unknown, send: (key: string) => Promise<unknown>, success: string): Promise<void> {
    setBusy(true); setError(null); setMessage("");
    try {
      await submitTrackedRequest(ledger, recovery.journal, current.userId, logical, operation, payload, send);
      if (mounted.current) { setRetry(null); setSelected(null); setRevision(value => value + 1); if (logical === "invitation-proposal") setFormRevision(value => value + 1); setMessage(success); }
    } catch (failure) {
      if (mounted.current) {
        setError(failure); onFailure(failure);
        if (ledger.isUncertain(logical)) setRetry({ run: () => command(logical, operation, payload, send, success) });
        else { setRetry(null); if (failure instanceof AccessFailure && failure.status === 409) { setSelected(null); setRevision(value => value + 1); } }
      }
      throw failure;
    } finally { if (mounted.current) { setBusy(false); recovery.refresh(); } }
  }
  async function propose(body: Schemas["IdentityInvitationProposalDto"]) {
    await command("invitation-proposal", identityOperations.propose, body, key => gateway.propose(body, key, current.userId), "Pengajuan undangan tersimpan. Persetujuan independen diperlukan sebelum pengiriman dapat diproses.");
  }
  async function confirm(reason: string, kind: "password" | "factor") {
    if (!selected || locked) return;
    const reviewed = selected;
    try {
      if (reviewed.kind === "invitation") {
        const { action, value } = reviewed;
        await command(`invitation-${action}:${value.id}`, identityOperations[action], { id: value.id, version: value.version, reason }, key => gateway.decide(action, value.id, value.version, reason, key, current.userId),
          action === "approve" ? "Undangan disetujui. Pengiriman melalui penyedia identitas masih tertahan; akun belum diaktifkan oleh keputusan ini." : "Keputusan undangan tersimpan. Periksa status terbaru.");
      } else {
        const { value } = reviewed;
        await command(`recovery:${value.userId}`, identityOperations.recovery, { userId: value.userId, securityVersion: value.securityVersion, kind, reason }, key => gateway.recover(value.userId, value.securityVersion, kind, reason, key, current.userId),
          "Sesi aplikasi telah dicabut dan permintaan pemulihan tercatat sebagai tertahan. Pemulihan di penyedia belum selesai.");
      }
    } catch { /* The shared command state retains the error and exact uncertain request. */ }
  }
  function resolved() { setFormRevision(value => value + 1); ledger.resetAfterReconciliation(); setRetry(null); setSelected(null); setError(null); recovery.refresh(); setRevision(value => value + 1); }
  return <>
    <AdministrationNavigation current="accounts" actions={current.allowedActions} locked={locked} />
    <div className={styles.actions}><p>Akun yang memeriksa: {current.userId}</p><button className={styles.secondary} disabled={locked} onClick={() => void onRefresh()}>Muat ulang akses</button><Link href="/identity/account">Verifikasi sesi dan dua langkah</Link></div>
    {message && <p role="status" className={styles.success}>{message}</p>}
    {error !== null && <div role="alert" className={styles.alert}><p>{accessMessage(error)}</p>{retry && <button className={styles.secondary} disabled={busy} onClick={() => void retry.run().catch(() => undefined)}>Periksa ulang permintaan yang sama</button>}</div>}
    {recovery.error !== null && <p role="alert" className={styles.alert}>{accessMessage(recovery.error)}</p>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy} onResolved={resolved} onFailure={onFailure} />
    {selected && <IdentityDecisionReview key={selected.kind === "invitation" ? `${selected.kind}:${selected.value.id}:${selected.action}:${selected.value.version}` : `${selected.kind}:${selected.value.userId}:${selected.value.securityVersion}`} decision={selected} locked={locked} onCancel={() => setSelected(null)} onConfirm={confirm} />}
    {current.allowedActions.includes("invite.manage") && current.allowedActions.includes("membership.propose") && <InvitationProposalForm key={formRevision} current={current} catalog={catalog} locked={locked} onSubmit={propose} />}
    <InvitationQueue gateway={gateway} actorId={current.userId} revision={revision} locked={locked} onSelect={setSelected} onFailure={onFailure} />
    <AccountDirectory gateway={gateway} actorId={current.userId} revision={revision} locked={locked} onSelect={setSelected} onFailure={onFailure} canRecover={current.allowedActions.includes("recovery.initiate")} />
  </>;
}