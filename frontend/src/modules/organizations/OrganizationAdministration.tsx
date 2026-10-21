"use client";

import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import Link from "next/link";
import type { components } from "../../api/generated/schema";
import { createAccessGateway } from "../administration/access-client";
import { AdministrationNavigation } from "../administration/AdministrationNavigation";
import { OperationRecovery, useOperationRecovery } from "../administration/OperationRecovery";
import { submitTrackedRequest } from "../administration/operation-journal";
import { AccessFailure, MutationLedger } from "../administration/workflow";
import { createOrganizationGateway, type OrganizationDecision } from "./organization-client";
import { validReference } from "./access-scope";
import { changeLabels, editOrganizationRequest, newOrganizationRequest, organizationKinds, organizationLabel,
  organizationMessage, parentKinds, statusLabel, validOrganizationDraft, type Organization, type OrganizationKind,
  type OrganizationProposal, type OrganizationRequest } from "./organization-workflow";
import styles from "../administration/access.module.css";
import orgStyles from "./organizations.module.css";
import { formatDateTime } from "@/lib/format";

type Schemas = components["schemas"];
type Selection = { kind: "unit"; value: Organization } | { kind: "proposal"; value: OrganizationProposal } | null;
type Confirmation = { action: OrganizationDecision; proposal: OrganizationProposal } | { action: "suspend"; unit: Organization };

export function OrganizationAdministration() {
  const [gateway] = useState(() => createAccessGateway());
  const [current, setCurrent] = useState<Schemas["CurrentAccessDto"] | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [revision, setRevision] = useState(0);
  const generation = useRef(0);
  const refresh = useCallback(async () => {
    const ticket = ++generation.current;
    setBusy(true); setError(null);
    try {
      const actor = await gateway.current();
      if (ticket === generation.current) { setCurrent(actor); setRevision(value => value + 1); }
    } catch (failure) { if (ticket === generation.current) { setCurrent(null); setError(failure); } }
    finally { if (ticket === generation.current) setBusy(false); }
  }, [gateway]);
  useEffect(() => {
    let active = true;
    const counter = generation;
    void Promise.resolve().then(() => { if (active) void refresh(); });
    return () => { active = false; counter.current++; };
  }, [refresh]);

  return <main className={styles.page}>
    <header className={styles.header}><div><Link href="/identity/account" className={styles.brand}>Unit Toko</Link><h1>Organisasi dan unit</h1>
      <p>Kelola nama, kode, dan status unit melalui pengajuan yang diperiksa secara independen.</p></div></header>
    {error !== null && current && <p role="alert" className={styles.alert}>{organizationMessage(error)}</p>}
    {!current ? busy ? <section className={styles.state} aria-busy="true"><h2>Memeriksa sesi</h2></section>
      : <SessionAccessState error={error} returnPath="/administration/organizations" onRetry={() => void refresh()} detail={error !== null ? organizationMessage(error) : undefined} />
      : <OrganizationDesk key={current.userId} current={current} revision={revision} refreshing={busy} refresh={refresh} />}
  </main>;
}

function OrganizationDesk({ current, revision, refreshing, refresh }: {
  current: Schemas["CurrentAccessDto"]; revision: number; refreshing: boolean; refresh: () => Promise<void>;
}) {
  const [gateway] = useState(() => createOrganizationGateway());
  const [ledger] = useState(() => new MutationLedger());
  const recovery = useOperationRecovery(current.userId);
  const [units, setUnits] = useState<Schemas["OrganizationPageDto"] | null>(null);
  const [proposals, setProposals] = useState<Schemas["OrganizationProposalPageDto"] | null>(null);
  const [mode, setMode] = useState<"units" | "proposals">("units");
  const [offset, setOffset] = useState(0);
  const [selection, setSelection] = useState<Selection>(null);
  const [draft, setDraft] = useState<OrganizationRequest | null>(null);
  const [reason, setReason] = useState("");
  const [review, setReview] = useState<{ request: OrganizationRequest; reason: string; parent: Organization | null } | null>(null);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [decisionReason, setDecisionReason] = useState("");
  const [lookup, setLookup] = useState("");
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState("");
  const generation = useRef(0);
  const mounted = useRef(true);
  const commandBusy = useRef(false);
  const locked = refreshing || busy || recovery.locked;

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    const ticket = ++generation.current;
    void Promise.resolve().then(async () => {
      if (!mounted.current || ticket !== generation.current) return;
      setBusy(true); setSelection(null); setDraft(null); setReview(null); setConfirmation(null); setReason(""); setDecisionReason("");
      try {
        const [nextUnits, nextProposals] = await Promise.all([gateway.list(current.userId, mode === "units" ? offset : 0), gateway.proposals(current.userId, mode === "proposals" ? offset : 0)]);
        if (mounted.current && ticket === generation.current) { setUnits(nextUnits); setProposals(nextProposals); }
      } catch (failure) { if (mounted.current && ticket === generation.current) { setUnits(null); setProposals(null); setError(failure); } }
      finally { if (mounted.current && ticket === generation.current) setBusy(false); }
    });
  }, [current.userId, gateway, mode, offset, revision]);
  useEffect(() => {
    if (recovery.entries.length === 0 && !commandBusy.current) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [busy, recovery.entries.length]);

  function clearDetails() { setSelection(null); setDraft(null); setReview(null); setConfirmation(null); setReason(""); setDecisionReason(""); }
  async function select(id: string, kind: "unit" | "proposal") {
    const ticket = ++generation.current;
    clearDetails(); setBusy(true); setError(null); setMessage("");
    try {
      const selected: Selection = kind === "unit" ? { kind, value: await gateway.read(id, current.userId) } : { kind, value: await gateway.proposal(id, current.userId) };
      if (mounted.current && ticket === generation.current) setSelection(selected);
    } catch (failure) { if (mounted.current && ticket === generation.current) setError(failure); }
    finally { if (mounted.current && ticket === generation.current) setBusy(false); }
  }
  function edit(request: OrganizationRequest) { setDraft(request); setReview(null); setConfirmation(null); setReason(""); setMessage(""); setError(null); }
  function changeDraft(next: OrganizationRequest) { setDraft(next); setReview(null); }
  async function prepare(event: FormEvent) {
    event.preventDefault(); if (!draft || locked || !validOrganizationDraft(draft, reason)) return;
    setBusy(true); setError(null);
    try {
      const parent = draft.parentId ? await gateway.read(draft.parentId, current.userId) : null;
      if (parent && (parent.kind !== parentKinds[draft.kind] || parent.status !== "active")) throw new AccessFailure(400, "organization_parent_unavailable");
      if (mounted.current) setReview({ request: { ...draft, code: draft.code.normalize("NFKC").trim().toUpperCase(), name: draft.name.normalize("NFKC").trim() }, reason: reason.trim(), parent });
    } catch (failure) { if (mounted.current) setError(failure); }
    finally { if (mounted.current) setBusy(false); }
  }
  async function command(logical: string, operation: string, payload: unknown, send: (key: string) => Promise<unknown>, success: string) {
    if (commandBusy.current || locked) return;
    commandBusy.current = true; setBusy(true); setError(null); setMessage("");
    try {
      await submitTrackedRequest(ledger, recovery.journal, current.userId, logical, operation, payload, send);
      if (mounted.current) { clearDetails(); setMessage(success); recovery.refresh(); await refresh(); }
    } catch (failure) {
      if (mounted.current) {
        setError(failure); recovery.refresh();
        if (failure instanceof AccessFailure && (failure.status === 409 || failure.code === "actor_changed")) {
          clearDetails(); await refresh();
          if (mounted.current) setError(failure);
        }
      }
    } finally { commandBusy.current = false; if (mounted.current) setBusy(false); }
  }
  function submitProposal() {
    if (!review) return;
    const payload = { change: review.request, reason: review.reason };
    void command("organization-proposal", "organization.propose", payload,
      key => gateway.propose(payload, key, current.userId), "Pengajuan tersimpan dan menunggu keputusan pemeriksa lain.");
  }
  function decide(event: FormEvent) {
    event.preventDefault(); if (!confirmation || !decisionReason.trim()) return;
    const target = confirmation.action === "suspend" ? confirmation.unit : confirmation.proposal;
    const version = confirmation.action === "suspend" ? confirmation.unit.scopeVersion : confirmation.proposal.version;
    const payload = { action: confirmation.action, id: target.id, version, reason: decisionReason.trim() };
    if (confirmation.action === "suspend") void command(`suspend:${target.id}`, "access.scope.suspend", payload,
      key => gateway.suspend(target.id, version, payload.reason, key, current.userId), "Unit telah ditangguhkan. Pengaktifan kembali memerlukan pengajuan baru.");
    else {
      const action = confirmation.action;
      void command(`${action}:${target.id}`, `organization.${action}`, payload,
        key => gateway.decide(action, target.id, version, payload.reason, key, current.userId), "Keputusan tersimpan. Periksa data terbaru untuk hasil perubahan.");
    }
  }
  function reconcile() { ledger.resetAfterReconciliation(); clearDetails(); recovery.refresh(); void refresh(); }

  const rows = mode === "units" ? units?.items : proposals?.items;
  return <>
    <AdministrationNavigation current="organizations" actions={current.allowedActions} locked={locked} />
    {error !== null && <p role="alert" className={styles.alert}>{organizationMessage(error)}</p>}
    {recovery.error !== null && <p role="alert" className={styles.alert}>{organizationMessage(recovery.error)}</p>}
    {message && <p role="status" className={styles.success}>{message}</p>}
    <OperationRecovery key={current.userId} entries={recovery.entries} journal={recovery.journal} disabled={busy || refreshing} onResolved={reconcile} />
    <div className={styles.actions}><button className={styles.secondary} disabled={locked} onClick={() => void refresh()}>Muat data terbaru</button></div>
    {busy && <p role="status">Memuat struktur dan pengajuan yang diizinkan…</p>}
    {!units || !proposals ? <section className={styles.state} aria-busy={busy}><h2>{busy ? "Memuat data organisasi" : "Data organisasi belum tersedia"}</h2>
      <p>Data dan tindakan ditampilkan setelah izin akun Anda terverifikasi.</p></section> : <div className={styles.desk}>
      <section className={styles.queue} aria-labelledby="organization-register-heading">
        <div className={styles.queueHeader}><h2 id="organization-register-heading">Daftar organisasi</h2><span>{mode === "units" ? units.total : proposals.total} {mode === "units" ? "unit" : "pengajuan"}</span></div>
        <div className={orgStyles.modeSwitch}><button className={styles.secondary} aria-pressed={mode === "units"} disabled={locked} onClick={() => { setMode("units"); setOffset(0); }}>Unit</button>
          <button className={styles.secondary} aria-pressed={mode === "proposals"} disabled={locked} onClick={() => { setMode("proposals"); setOffset(0); }}>Pengajuan</button></div>
        <form className={orgStyles.reference} onSubmit={event => { event.preventDefault(); if (validReference(lookup)) void select(lookup, mode === "units" ? "unit" : "proposal"); }}>
          <label>Referensi {mode === "units" ? "unit" : "pengajuan"}<input inputMode="numeric" maxLength={19} autoComplete="off" value={lookup} disabled={locked} onChange={event => setLookup(event.target.value)} /></label>
          <button className={styles.secondary} disabled={locked || !validReference(lookup)}>Cari</button></form>
        {units.allowedActions.includes("propose") && <div className={styles.newActions}><button className={styles.primary} disabled={locked} onClick={() => { clearDetails(); edit(newOrganizationRequest()); }}>Tambah unit</button></div>}
        <ul className={styles.queueList}>{mode === "units" ? units.items.map(item => <li key={item.id}><button disabled={locked}
          className={selection?.kind === "unit" && selection.value.id === item.id ? styles.selectedRow : styles.queueRow} onClick={() => void select(item.id, "unit")}>
          <strong>{organizationLabel(item)}</strong><span>{organizationKinds[item.kind]} · {statusLabel(item.status)}</span><small>Referensi {item.id}{item.parentId ? ` · Induk ${item.parentId}` : " · Organisasi induk"}</small></button></li>)
          : proposals.items.map(item => <li key={item.id}><button disabled={locked} className={selection?.kind === "proposal" && selection.value.id === item.id ? styles.selectedRow : styles.queueRow}
            onClick={() => void select(item.id, "proposal")}><strong>{item.request.code} · {item.request.name}</strong><span>{changeLabels[item.request.changeKind]} · {statusLabel(item.status)}</span><small>Pengajuan {item.id} · Akun {item.requestedBy}</small></button></li>)}</ul>
        {rows?.length === 0 && <p className={styles.empty}>Belum ada {mode === "units" ? "unit" : "pengajuan"} pada halaman ini.</p>}
        <nav className={styles.pagination} aria-label="Halaman organisasi"><button className={styles.secondary} disabled={locked || offset === 0} onClick={() => setOffset(Math.max(0, offset - 25))}>Sebelumnya</button>
          <button className={styles.secondary} disabled={locked || (rows?.length ?? 0) < 25 || offset >= 9975} onClick={() => setOffset(offset + 25)}>Berikutnya</button></nav>
      </section>
      <section className={styles.detail} aria-label="Rincian organisasi">
        {draft ? <form className={styles.form} onSubmit={prepare}>
          <h2>{changeLabels[draft.changeKind]}</h2><p className={styles.help}>Pengajuan belum mengubah data aktif. Pemeriksa lain memutuskan perubahan setelah meninjau rincian yang tersimpan.</p>
          <fieldset className={styles.fields} disabled={locked}>
            {draft.changeKind === "create" ? <><label>Jenis unit<select value={draft.kind} onChange={event => changeDraft({ ...draft, kind: event.target.value as OrganizationKind, parentId: null })}>
              {Object.entries(organizationKinds).map(([kind, label]) => <option key={kind} value={kind}>{label}</option>)}</select></label>
              {parentKinds[draft.kind] !== null && <label>Referensi {organizationKinds[parentKinds[draft.kind]!] } induk<input inputMode="numeric" autoComplete="off" maxLength={19} required value={draft.parentId ?? ""} onChange={event => changeDraft({ ...draft, parentId: event.target.value || null })} /></label>}</>
              : <dl className={styles.facts}><div><dt>Unit</dt><dd>{draft.scopeId}</dd></div><div><dt>Jenis dan induk tetap</dt><dd>{organizationKinds[draft.kind]} · {draft.parentId ?? "Tanpa induk"}</dd></div><div><dt>Versi unit</dt><dd>{draft.expectedScopeVersion}</dd></div><div><dt>Versi nama dan kode</dt><dd>{draft.expectedProfileVersion}</dd></div></dl>}
            <div className={styles.twoFields}><label>Kode unit<input required maxLength={64} autoComplete="off" value={draft.code} onChange={event => changeDraft({ ...draft, code: event.target.value })} /></label>
              <label>Nama unit<input required maxLength={160} autoComplete="off" value={draft.name} onChange={event => changeDraft({ ...draft, name: event.target.value })} /></label></div>
            <p className={styles.help}>Kode menggunakan huruf, angka, titik, garis bawah, atau tanda hubung; harus unik untuk jenis unit dan induk yang sama.</p>
            <label><span id="organization-proposal-reason">Alasan pengajuan</span><textarea aria-labelledby="organization-proposal-reason" required maxLength={1000} rows={3} value={reason} onChange={event => { setReason(event.target.value); setReview(null); }} /></label>
            {draft.changeKind === "reactivate" && <p className={styles.scopeSummary}>Pengaktifan kembali dapat memulihkan akses pada unit ini. Jumlah unit, keanggotaan, izin, dan akun yang berpotensi terdampak akan ditampilkan pada pengajuan untuk diperiksa sebelum persetujuan.</p>}
            <button className={styles.secondary} disabled={!validOrganizationDraft(draft, reason)}>Periksa rincian pengajuan</button>
          </fieldset>
          {review && <div className={styles.review}><h3>Rincian yang akan diajukan</h3><p><strong>{review.request.code} · {review.request.name}</strong><br />{organizationKinds[review.request.kind]}</p>
            <p>Induk: {review.parent ? organizationLabel(review.parent) : "Tanpa induk"}</p><p className={styles.preserve}>{review.reason}</p>
            <button type="button" className={styles.primary} disabled={locked} onClick={submitProposal}>Kirim untuk pemeriksaan</button></div>}
        </form> : selection?.kind === "unit" ? <div className={styles.form}><h2>{organizationLabel(selection.value)}</h2>
          <dl className={styles.facts}><div><dt>Jenis</dt><dd>{organizationKinds[selection.value.kind]}</dd></div><div><dt>Status</dt><dd>{statusLabel(selection.value.status)}</dd></div>
            <div><dt>Referensi unit</dt><dd>{selection.value.id}</dd></div><div><dt>Induk</dt><dd>{selection.value.parentId ?? "Tanpa induk"}</dd></div>
            <div><dt>Versi unit</dt><dd>{selection.value.scopeVersion}</dd></div><div><dt>Versi nama dan kode</dt><dd>{selection.value.profileVersion ?? "Belum ditetapkan"}</dd></div></dl>
          {!selection.value.name && <p className={styles.help}>Unit ini belum memiliki nama dan kode yang telah diperiksa.</p>}
          <div className={styles.actions}>{selection.value.allowedActions.includes("updateProfile") && <button className={styles.secondary} disabled={locked} onClick={() => edit(editOrganizationRequest(selection.value, "updateProfile"))}>Ajukan nama atau kode</button>}
            {selection.value.allowedActions.includes("reactivate") && <button className={styles.primary} disabled={locked} onClick={() => edit(editOrganizationRequest(selection.value, "reactivate"))}>Ajukan pengaktifan kembali</button>}
            {selection.value.allowedActions.includes("suspend") && <button className={styles.danger} disabled={locked} onClick={() => { setConfirmation({ action: "suspend", unit: selection.value }); setDecisionReason(""); }}>Tangguhkan unit</button>}</div>
        </div> : selection?.kind === "proposal" ? <div className={styles.form}>
          <ProposalDetails proposal={selection.value} />
          <div className={styles.actions}>{(["approve", "reject", "withdraw"] as const).filter(action => selection.value.allowedActions.includes(action)).map(action =>
            <button key={action} className={action === "approve" ? styles.primary : styles.secondary} disabled={locked} onClick={() => { setConfirmation({ action, proposal: selection.value }); setDecisionReason(""); }}>
              {action === "approve" ? "Tinjau persetujuan" : action === "reject" ? "Tolak pengajuan" : "Tarik pengajuan"}</button>)}</div>
          {selection.value.status === "pending" && selection.value.allowedActions.length === 0 && <p className={styles.help}>Tidak ada keputusan yang tersedia bagi akun ini untuk keadaan pengajuan saat ini.</p>}
        </div> : <div className={styles.form}><h2>Pilih unit atau pengajuan</h2><p>Rincian, hubungan induk, dan tindakan yang tersedia ditampilkan di sini.</p></div>}
        {confirmation && <form className={styles.form} onSubmit={decide}><div className={styles.review}>
          <h2>{confirmation.action === "approve" ? "Pastikan persetujuan" : confirmation.action === "reject" ? "Pastikan penolakan" : confirmation.action === "withdraw" ? "Pastikan penarikan" : "Pastikan penangguhan"}</h2>
          {confirmation.action === "suspend" ? <p>Unit {organizationLabel(confirmation.unit)} akan ditangguhkan. Akses yang bergantung pada unit ini berhenti berlaku. Pengaktifan kembali memerlukan pengajuan dan pemeriksaan independen.</p>
            : <ProposalDetails proposal={confirmation.proposal} compact />}
          <label><span id="organization-decision-reason">Alasan keputusan</span><textarea aria-labelledby="organization-decision-reason" rows={3} maxLength={1000} required disabled={locked} value={decisionReason} onChange={event => setDecisionReason(event.target.value)} /></label>
          <div className={styles.actions}><button className={confirmation.action === "suspend" ? styles.danger : styles.primary} disabled={locked || !decisionReason.trim()}>
            {confirmation.action === "approve" ? "Setujui perubahan" : confirmation.action === "reject" ? "Simpan penolakan" : confirmation.action === "withdraw" ? "Tarik pengajuan ini" : "Tangguhkan unit ini"}</button>
            <button type="button" className={styles.secondary} disabled={locked} onClick={() => setConfirmation(null)}>Kembali</button></div>
        </div></form>}
      </section>
    </div>}
  </>;
}

function ProposalDetails({ proposal, compact = false }: { proposal: OrganizationProposal; compact?: boolean }) {
  return <>{!compact && <><h2>{changeLabels[proposal.request.changeKind]}</h2><p>Pengajuan {proposal.id} · {statusLabel(proposal.status)} · Versi {proposal.version}</p></>}
    <dl className={styles.facts}><div><dt>Jenis unit</dt><dd>{organizationKinds[proposal.request.kind]}</dd></div><div><dt>Induk tetap</dt><dd>{proposal.request.parentId ?? "Tanpa induk"}</dd></div>
      <div><dt>Pengaju</dt><dd>Akun {proposal.requestedBy}</dd></div><div><dt>Referensi unit</dt><dd>{proposal.request.scopeId ?? "Dialokasikan setelah persetujuan"}</dd></div></dl>
    <table className={orgStyles.compare}><caption className={styles.srOnly}>Data sebelum dan setelah perubahan yang diminta</caption><thead><tr><th>Data</th><th>Sebelum</th><th>Diajukan</th></tr></thead>
      <tbody><tr><th>Kode</th><td>{proposal.before.code ?? "Belum ada"}</td><td>{proposal.request.code}</td></tr>
        <tr><th>Nama</th><td>{proposal.before.name ?? "Belum ada"}</td><td>{proposal.request.name}</td></tr>
        <tr><th>Status</th><td>{proposal.before.status ? statusLabel(proposal.before.status) : "Belum ada"}</td><td>{proposal.request.changeKind === "updateProfile" ? statusLabel(proposal.before.status ?? "") : "Aktif"}</td></tr></tbody></table>
    <p className={styles.help}>Versi unit yang diperiksa: {proposal.request.expectedScopeVersion}; versi nama dan kode: {proposal.request.expectedProfileVersion}.</p>
    {proposal.request.changeKind === "reactivate" && <section className={orgStyles.impact} aria-label="Dampak pengaktifan kembali"><h3>Potensi akses yang dipulihkan</h3><dl>
      <div><dt>Unit dalam cakupan</dt><dd>{proposal.impact.affectedScopes}</dd></div><div><dt>Keanggotaan berstatus aktif</dt><dd>{proposal.impact.activeMemberships}</dd></div>
      <div><dt>Izin berstatus aktif</dt><dd>{proposal.impact.activeGrants}</dd></div><div><dt>Akun terkait</dt><dd>{proposal.impact.affectedAccounts}</dd></div></dl>
      <p className={styles.help}>Angka ini mencakup potensi akses, termasuk izin dengan cakupan lebih luas. Masa berlaku dan verifikasi akun tetap diperiksa. Unit turunan yang ditangguhkan tetap ditangguhkan. Sesi lama akun terkait perlu diperbarui.</p>
      <details><summary>Referensi pemeriksaan dampak</summary><p className={styles.preserve}>{proposal.impact.revision}</p></details></section>}
    <div><h3>Alasan pengajuan</h3><p className={styles.preserve}>{proposal.reason}</p></div>
    {!compact && <><p className={styles.help}>Diajukan {formatDateTime(proposal.createdAt)}</p>
      {proposal.approvedBy && <p>Disetujui akun {proposal.approvedBy}{proposal.resultScopeId ? `; hasil pada unit ${proposal.resultScopeId}` : ""}.</p>}
      {proposal.decisionBy && <p>Keputusan oleh akun {proposal.decisionBy}: <span className={styles.preserve}>{proposal.decisionReason}</span></p>}</>}
  </>;
}
