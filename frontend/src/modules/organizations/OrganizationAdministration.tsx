"use client";

import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
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
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Field } from '@/components/workspace-kit/field';
import { Facts } from '@/components/workspace-kit/facts';
import { Badge } from '@/components/workspace-kit/badge';
import { Tabs } from '@/components/workspace-kit/tabs';
import { DataList } from '@/components/workspace-kit/data-list';

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

  // The desk owns the refresh, so it renders the header with it; the access states render it without.
  const header = (action?: ReactNode) => <PageHeader title="Organisasi dan unit" description="Kelola nama, kode, dan status unit melalui pengajuan yang diperiksa secara independen." secondaryAction={action} />;
  return <main className={styles.page}>
    {current ? <OrganizationDesk key={current.userId} current={current} revision={revision} refreshing={busy} refresh={refresh} header={header} />
      : <>{header()}<div className="ui-stack">{busy ? <StateView kind="loading" title="Memeriksa sesi" />
        : <SessionAccessState error={error} returnPath="/administration/organizations" onRetry={() => void refresh()} detail={error !== null ? organizationMessage(error) : undefined} />}</div></>}
  </main>;
}

function OrganizationDesk({ current, revision, refreshing, refresh, header }: {
  current: Schemas["CurrentAccessDto"]; revision: number; refreshing: boolean; refresh: () => Promise<void>; header: (action?: ReactNode) => ReactNode;
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
  const noun = mode === "units" ? "unit" : "pengajuan";
  const loadFailed = error !== null && (!units || !proposals);
  const pagination = { label: "Halaman organisasi", start: offset + 1, end: offset + (rows?.length ?? 0), disabled: locked, hasPrevious: offset > 0,
    hasNext: (rows?.length ?? 0) >= 25 && offset < 9975, onPrevious: () => setOffset(Math.max(0, offset - 25)), onNext: () => setOffset(offset + 25) };
  return <>
    {header(<Button variant="secondary" disabled={locked} onClick={() => void refresh()}>Muat data terbaru</Button>)}
    <div className="ui-stack">
    <AdministrationNavigation current="organizations" actions={current.allowedActions} locked={locked} />
    {error !== null && !loadFailed && <StateView kind="error" title={organizationMessage(error)} />}
    {recovery.error !== null && <StateView kind="error" title={organizationMessage(recovery.error)} />}
    {message && <StateView kind="success" title={message} />}
    <OperationRecovery key={current.userId} entries={recovery.entries} journal={recovery.journal} disabled={busy || refreshing} onResolved={reconcile} />
    {busy && units && proposals && <p role="status" className={styles.help}>Memuat struktur dan pengajuan yang diizinkan…</p>}
    {!units || !proposals ? busy ? <StateView kind="loading" title="Memuat struktur dan pengajuan yang diizinkan…" />
      : loadFailed ? <StateView kind="error" title="Data organisasi belum tersedia" message={organizationMessage(error)} />
      : <StateView kind="unknown" title="Data organisasi belum tersedia" message="Data dan tindakan ditampilkan setelah izin akun Anda terverifikasi." />
      : <div className="ui-desk">
      <Section title="Daftar organisasi" id="organization-register-heading" description={<>{mode === "units" ? units.total : proposals.total} {noun}</>}>
        <Tabs label="Jenis daftar" current={mode} disabled={locked} onSelect={value => { setMode(value as "units" | "proposals"); setOffset(0); }}
          items={[{ label: "Unit", value: "units" }, { label: "Pengajuan", value: "proposals" }]} />
        <form className="ui-form" onSubmit={event => { event.preventDefault(); if (validReference(lookup)) void select(lookup, mode === "units" ? "unit" : "proposal"); }}>
          <Field id="organization-lookup" label={`Referensi ${noun}`} width="search"><input inputMode="numeric" maxLength={19} autoComplete="off" value={lookup} disabled={locked} onChange={event => setLookup(event.target.value)} /></Field>
          <Button variant="secondary" type="submit" disabled={locked || !validReference(lookup)}>Cari</Button></form>
        {units.allowedActions.includes("propose") && <Button variant="primary" disabled={locked} onClick={() => { clearDetails(); edit(newOrganizationRequest()); }}>Tambah unit</Button>}
        {mode === "units"
          ? <DataList label="Unit organisasi" items={units.items} getKey={item => item.id} disabled={locked} selectedKey={selection?.kind === "unit" ? selection.value.id : null}
            empty={{ title: "Belum ada unit pada halaman ini." }} onSelect={item => void select(item.id, "unit")} pagination={pagination}
            renderTitle={item => organizationLabel(item)} renderBadge={item => <Badge status={statusLabel(item.status)} />}
            renderMeta={item => <>{organizationKinds[item.kind]} · Referensi {item.id}{item.parentId ? ` · Induk ${item.parentId}` : " · Organisasi induk"}</>} />
          : <DataList label="Pengajuan organisasi" items={proposals.items} getKey={item => item.id} disabled={locked} selectedKey={selection?.kind === "proposal" ? selection.value.id : null}
            empty={{ title: "Belum ada pengajuan pada halaman ini." }} onSelect={item => void select(item.id, "proposal")} pagination={pagination}
            renderTitle={item => <>{item.request.code} · {item.request.name}</>} renderBadge={item => <Badge status={statusLabel(item.status)} />}
            renderMeta={item => <>{changeLabels[item.request.changeKind]} · Pengajuan {item.id} · Akun {item.requestedBy}</>} />}
      </Section>
      <section className="ui-section" aria-label="Rincian organisasi"><div className="ui-stack">
        {draft ? <form className="ui-form" onSubmit={prepare}>
          <h2>{changeLabels[draft.changeKind]}</h2><p className={styles.help}>Pengajuan belum mengubah data aktif. Pemeriksa lain memutuskan perubahan setelah meninjau rincian yang tersimpan.</p>
          <fieldset disabled={locked}>
            {draft.changeKind === "create" ? <><Field id="organization-kind" label="Jenis unit"><select value={draft.kind} onChange={event => changeDraft({ ...draft, kind: event.target.value as OrganizationKind, parentId: null })}>
              {Object.entries(organizationKinds).map(([kind, label]) => <option key={kind} value={kind}>{label}</option>)}</select></Field>
              {parentKinds[draft.kind] !== null && <Field id="organization-parent" label={`Referensi ${organizationKinds[parentKinds[draft.kind]!]} induk`}><input inputMode="numeric" autoComplete="off" maxLength={19} required value={draft.parentId ?? ""} onChange={event => changeDraft({ ...draft, parentId: event.target.value || null })} /></Field>}</>
              : <Facts items={[{ term: "Unit", value: draft.scopeId }, { term: "Jenis dan induk tetap", value: <>{organizationKinds[draft.kind]} · {draft.parentId ?? "Tanpa induk"}</> },
                { term: "Versi unit", value: draft.expectedScopeVersion }, { term: "Versi nama dan kode", value: draft.expectedProfileVersion }]} />}
            <div className={styles.twoFields}><Field id="organization-code" label="Kode unit"><input required maxLength={64} autoComplete="off" value={draft.code} onChange={event => changeDraft({ ...draft, code: event.target.value })} /></Field>
              <Field id="organization-name" label="Nama unit"><input required maxLength={160} autoComplete="off" value={draft.name} onChange={event => changeDraft({ ...draft, name: event.target.value })} /></Field></div>
            <p className={styles.help}>Kode menggunakan huruf, angka, titik, garis bawah, atau tanda hubung; harus unik untuk jenis unit dan induk yang sama.</p>
            <Field id="organization-proposal-reason" label="Alasan pengajuan"><textarea required maxLength={1000} rows={3} value={reason} onChange={event => { setReason(event.target.value); setReview(null); }} /></Field>
            {draft.changeKind === "reactivate" && <p className={styles.scopeSummary}>Pengaktifan kembali dapat memulihkan akses pada unit ini. Jumlah unit, keanggotaan, izin, dan akun yang berpotensi terdampak akan ditampilkan pada pengajuan untuk diperiksa sebelum persetujuan.</p>}
            <Button variant="secondary" type="submit" disabled={!validOrganizationDraft(draft, reason)}>Periksa rincian pengajuan</Button>
          </fieldset>
          {review && <div className={styles.preview}><h3>Rincian yang akan diajukan</h3><p><strong>{review.request.code} · {review.request.name}</strong><br />{organizationKinds[review.request.kind]}</p>
            <p>Induk: {review.parent ? organizationLabel(review.parent) : "Tanpa induk"}</p><p className={styles.preserve}>{review.reason}</p>
            <Button variant="primary" type="button" disabled={locked} onClick={submitProposal}>Kirim untuk pemeriksaan</Button></div>}
        </form> : selection?.kind === "unit" ? <div className="ui-form"><h2>{organizationLabel(selection.value)}</h2>
          <Facts items={[{ term: "Jenis", value: organizationKinds[selection.value.kind] }, { term: "Status", value: statusLabel(selection.value.status) },
            { term: "Referensi unit", value: selection.value.id }, { term: "Induk", value: selection.value.parentId ?? "Tanpa induk" },
            { term: "Versi unit", value: selection.value.scopeVersion }, { term: "Versi nama dan kode", value: selection.value.profileVersion ?? "Belum ditetapkan" }]} />
          {!selection.value.name && <p className={styles.help}>Unit ini belum memiliki nama dan kode yang telah diperiksa.</p>}
          <div className={styles.actions}>{selection.value.allowedActions.includes("updateProfile") && <Button variant="secondary" disabled={locked} onClick={() => edit(editOrganizationRequest(selection.value, "updateProfile"))}>Ajukan nama atau kode</Button>}
            {selection.value.allowedActions.includes("reactivate") && <Button variant="primary" disabled={locked} onClick={() => edit(editOrganizationRequest(selection.value, "reactivate"))}>Ajukan pengaktifan kembali</Button>}
            {selection.value.allowedActions.includes("suspend") && <Button variant="danger" disabled={locked} onClick={() => { setConfirmation({ action: "suspend", unit: selection.value }); setDecisionReason(""); }}>Tangguhkan unit</Button>}</div>
        </div> : selection?.kind === "proposal" ? <div className="ui-form">
          <ProposalDetails proposal={selection.value} />
          <div className={styles.actions}>{(["approve", "reject", "withdraw"] as const).filter(action => selection.value.allowedActions.includes(action)).map(action =>
            <Button key={action} variant={action === "approve" ? "primary" : "secondary"} disabled={locked} onClick={() => { setConfirmation({ action, proposal: selection.value }); setDecisionReason(""); }}>
              {action === "approve" ? "Tinjau persetujuan" : action === "reject" ? "Tolak pengajuan" : "Tarik pengajuan"}</Button>)}</div>
          {selection.value.status === "pending" && selection.value.allowedActions.length === 0 && <p className={styles.help}>Tidak ada keputusan yang tersedia bagi akun ini untuk keadaan pengajuan saat ini.</p>}
        </div> : <div className="ui-form"><h2>Pilih unit atau pengajuan</h2><p>Rincian, hubungan induk, dan tindakan yang tersedia ditampilkan di sini.</p></div>}
        {confirmation && <form className="ui-form" onSubmit={decide}><div className={styles.preview}>
          <h2>{confirmation.action === "approve" ? "Pastikan persetujuan" : confirmation.action === "reject" ? "Pastikan penolakan" : confirmation.action === "withdraw" ? "Pastikan penarikan" : "Pastikan penangguhan"}</h2>
          {confirmation.action === "suspend" ? <p>Unit {organizationLabel(confirmation.unit)} akan ditangguhkan. Akses yang bergantung pada unit ini berhenti berlaku. Pengaktifan kembali memerlukan pengajuan dan pemeriksaan independen.</p>
            : <ProposalDetails proposal={confirmation.proposal} compact />}
          <Field id="organization-decision-reason" label="Alasan keputusan"><textarea rows={3} maxLength={1000} required disabled={locked} value={decisionReason} onChange={event => setDecisionReason(event.target.value)} /></Field>
          <div className={styles.actions}><Button type="submit" variant={confirmation.action === "suspend" ? "danger" : "primary"} disabled={locked || !decisionReason.trim()}>
            {confirmation.action === "approve" ? "Setujui perubahan" : confirmation.action === "reject" ? "Simpan penolakan" : confirmation.action === "withdraw" ? "Tarik pengajuan ini" : "Tangguhkan unit ini"}</Button>
            <Button variant="secondary" type="button" disabled={locked} onClick={() => setConfirmation(null)}>Kembali</Button></div>
        </div></form>}
      </div></section>
    </div>}
    </div>
  </>;
}

function ProposalDetails({ proposal, compact = false }: { proposal: OrganizationProposal; compact?: boolean }) {
  return <>{!compact && <><h2>{changeLabels[proposal.request.changeKind]}</h2><p>Pengajuan {proposal.id} · {statusLabel(proposal.status)} · Versi {proposal.version}</p></>}
    <Facts items={[{ term: "Jenis unit", value: organizationKinds[proposal.request.kind] }, { term: "Induk tetap", value: proposal.request.parentId ?? "Tanpa induk" },
      { term: "Pengaju", value: <>Akun {proposal.requestedBy}</> }, { term: "Referensi unit", value: proposal.request.scopeId ?? "Dialokasikan setelah persetujuan" }]} />
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
