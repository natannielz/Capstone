"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import type { PersonReviewGateway } from "./person-review-client";
import { LatestPersonReviewRead, PersonReviewPreviewSession, personReviewActionAllowed, personReviewCommand, personReviewDate, personReviewMessage, personReviewStatus,
  type PersonReviewAccount, type PersonReviewAction, type PersonReviewCommand, type PersonReviewDetail, type PersonReviewDraft, type PersonReviewPage,
  type PersonReviewPreview, type PersonReviewSummary, type PersonReviewTarget, type PersonReviewTargetPage } from "./person-review-workflow";
import styles from "../administration/access.module.css";
import { Button } from "@/components/workspace-kit/button";
import { Section } from "@/components/workspace-kit/section";
import { StateView } from "@/components/workspace-kit/state-view";
import { DataList } from "@/components/workspace-kit/data-list";
import { Toolbar } from "@/components/workspace-kit/toolbar";
import { Field } from "@/components/workspace-kit/field";
import { Facts } from "@/components/workspace-kit/facts";
import { Badge } from "@/components/workspace-kit/badge";
import { Tabs } from "@/components/workspace-kit/tabs";
import type { TablePagination } from "@/components/workspace-kit/data-table";

type ReadProps = { gateway: PersonReviewGateway; actorId: string; revision: number; locked: boolean; onReset: () => void; onFailure: (error: unknown) => void };
export type PersonReviewSelection = { kind: "target"; target: PersonReviewTarget } | { kind: "proposal"; detail: PersonReviewDetail };
const actionLabels = { propose: "Ajukan penangguhan", approve: "Setujui penangguhan", reject: "Tolak pengajuan", withdraw: "Tarik pengajuan" };
const name = (account: PersonReviewAccount) => account.displayName ?? account.verifiedEmail;

export function PersonReviewQueue({ gateway, actorId, revision, locked, onReset, onFailure, onSelect }: ReadProps & { onSelect: (summary: PersonReviewSummary) => void }) {
  const [reads] = useState(() => new LatestPersonReviewRead());
  const [query, setQuery] = useState({ status: "pending", offset: 0 });
  const [page, setPage] = useState<PersonReviewPage | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    const ticket = reads.begin();
    void Promise.resolve().then(async () => {
      if (!ticket.current()) return;
      setPage(null); setBusy(true); setError(null);
      try { const result = await gateway.list(actorId, query.status, query.offset, ticket.signal); if (ticket.current()) setPage(result); }
      catch (failure) { if (ticket.current()) { setError(failure); onFailure(failure); } }
      finally { if (ticket.current()) setBusy(false); }
    });
    return () => reads.cancel();
  }, [gateway, actorId, revision, query, onFailure, reads]);
  function change(next: typeof query) { reads.cancel(); setPage(null); setQuery(next); onReset(); }
  return <Section title="Daftar pengajuan" id="person-queue-title" description="Pengajuan yang tersedia dalam penugasan Anda." busy={busy}>
    <Toolbar label="Saring pengajuan" filters={<Field id="person-queue-status" label="Status pengajuan"><select value={query.status} disabled={locked} onChange={event => change({ status: event.target.value, offset: 0 })}>
        <option value="pending">Menunggu keputusan</option><option value="approved">Disetujui</option><option value="rejected">Ditolak</option><option value="withdrawn">Ditarik</option><option value="">Semua status</option>
      </select></Field>} resultText={page ? `${page.total} pengajuan tersedia.` : undefined} />
    <DataList label="Daftar pengajuan" items={page?.items ?? []} getKey={item => item.result.proposalId} loading={busy} disabled={locked}
      error={error !== null ? { message: personReviewMessage(error) } : undefined} empty={page ? { title: "Belum ada pengajuan pada status ini." } : undefined}
      onSelect={onSelect} renderTitle={item => <>Pengajuan oleh {name(item.requester)}</>} renderBadge={item => <Badge status={personReviewStatus(item.result.status)} />}
      renderMeta={item => <>{item.affectedAccountCount} akun · {personReviewDate(item.result.recordedAt)}<span className={styles.srOnly}> Buka rincian pengajuan</span></>}
      pagination={page ? pager(query.offset, page.items.length, page.total, locked || busy, offset => change({ ...query, offset })) : undefined} />
  </Section>;
}

export function PersonReviewTargetSearch({ gateway, actorId, revision, locked, onReset, onFailure, onSelect }: ReadProps & { onSelect: (target: PersonReviewTarget) => void }) {
  const [reads] = useState(() => new LatestPersonReviewRead());
  const [input, setInput] = useState("");
  const [query, setQuery] = useState<{ term: string; offset: number } | null>(null);
  const [page, setPage] = useState<PersonReviewTargetPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!query) return;
    const ticket = reads.begin();
    void Promise.resolve().then(async () => {
      if (!ticket.current()) return;
      setPage(null); setBusy(true); setError(null);
      try { const result = await gateway.targets(actorId, query.term, query.offset, ticket.signal); if (ticket.current()) setPage(result); }
      catch (failure) { if (ticket.current()) { setError(failure); onFailure(failure); } }
      finally { if (ticket.current()) setBusy(false); }
    });
    return () => reads.cancel();
  }, [gateway, actorId, query, revision, onFailure, reads]);
  function change(next: { term: string; offset: number }) { reads.cancel(); setPage(null); setQuery(next); onReset(); }
  function search(event: FormEvent) { event.preventDefault(); if (!locked && input.trim().length >= 2) change({ term: input.trim(), offset: 0 }); }
  return <Section title="Pilih keterkaitan akun" id="person-search-title" description="Cari nama atau email dalam lingkup penugasan Anda. Periksa semua akun dalam hasil yang dipilih." busy={busy}>
    <form onSubmit={search}><Toolbar label="Cari keterkaitan akun" search={<Field id="person-search-term" label="Cari nama atau email" width="search"><input type="search" minLength={2} maxLength={80} autoComplete="off" value={input} disabled={locked} onChange={event => {
        reads.cancel(); setInput(event.target.value); setPage(null); setError(null); setBusy(false); onReset();
      }} /></Field>} filters={<Button type="submit" variant="secondary" disabled={locked || input.trim().length < 2}>Cari</Button>}
      resultText={page ? `${page.total} keterkaitan akun ditemukan.` : undefined} /></form>
    {(busy || error !== null || page) && <DataList label="Hasil pencarian keterkaitan akun" items={page?.items ?? []} getKey={item => item.groupId} loading={busy} disabled={locked}
      error={error !== null ? { message: personReviewMessage(error) } : undefined} empty={page ? { title: "Tidak ada keterkaitan akun yang cocok dalam lingkup Anda." } : undefined}
      onSelect={onSelect} renderTitle={item => item.accounts.map(name).join(", ")} renderBadge={item => <Badge status={personReviewStatus(item.status)} />}
      renderMeta={item => <>{String(item.accounts.length)} akun terkait · Pilih untuk memeriksa dampak</>}
      pagination={page && query ? pager(query.offset, page.items.length, page.total, locked || busy, offset => change({ ...query, offset })) : undefined} />}
  </Section>;
}

/** Offset pages of 25 (the API's page size), as the kit pager: hidden when everything fits; at most 10,000 rows deep. */
function pager(offset: number, count: number, total: string, locked: boolean, onChange: (offset: number) => void): TablePagination {
  return { label: "Halaman hasil", start: offset + 1, end: offset + count, total: Number(total), hasPrevious: offset > 0, hasNext: offset < 10000 && BigInt(offset + 25) < BigInt(total),
    disabled: locked, onPrevious: () => onChange(Math.max(0, offset - 25)), onNext: () => onChange(offset + 25) };
}



function AffectedAccounts({ accounts }: { accounts: PersonReviewAccount[] }) {
  return <><h3>Akun yang terdampak</h3><ul className={styles.historyList}>{accounts.map(account => <li key={account.userId}>
    <strong className={styles.preserve}>{name(account)}</strong><p className={styles.preserve}>{account.verifiedEmail}</p><span>{personReviewStatus(account.status)}</span>
  </li>)}</ul></>;
}

export function PersonReviewEditor({ selection, actorId, permissions, gateway, locked, fresh, now, onFailure, onSubmit, onCancel }: {
  selection: PersonReviewSelection; actorId: string; permissions: readonly string[]; gateway: PersonReviewGateway; locked: boolean; fresh: boolean;
  now: () => number; onFailure: (failure: unknown) => void; onSubmit: (command: PersonReviewCommand) => Promise<void>; onCancel: () => void;
}) {
  const [reason, setReason] = useState("");
  const [action, setAction] = useState<PersonReviewAction | null>(selection.kind === "target" ? "propose" : null);
  const [preview, setPreview] = useState<PersonReviewPreview | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [previews] = useState(() => new PersonReviewPreviewSession());
  const [reads] = useState(() => new LatestPersonReviewRead());
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { heading.current?.focus(); heading.current?.scrollIntoView({ block: "nearest", behavior: "instant" }); return () => { reads.cancel(); previews.invalidate(); }; }, [reads, previews]);
  const original = selection.kind === "proposal" ? selection.detail.summary : null;
  const target = selection.kind === "target" ? selection.target : null;
  const shownSummary = preview?.proposal ?? original;
  const affected = preview?.target.accounts ?? target?.accounts ?? (selection.kind === "proposal" ? selection.detail.accounts : []);
  const needsPreview = action === "propose" || action === "approve";
  const validPreview = preview !== null && Date.parse(preview.expiresAt) > now();
  const canAct = action === "propose" ? permissions.includes("person.review.propose") : action !== null && original !== null && personReviewActionAllowed(action, original, permissions);
  const editable = !locked && canAct;
  function invalidate() { reads.cancel(); previews.invalidate(); setPreview(null); setConfirmed(false); setBusy(false); setError(null); }
  function draft(): PersonReviewDraft {
    return { actorId, action: action!, groupId: target?.groupId ?? original!.result.groupId, groupVersion: target?.version ?? original!.result.groupVersion,
      ...(original ? { proposalId: original.result.proposalId, proposalVersion: original.result.version } : {}), reason: reason.trim() };
  }
  async function inspect() {
    if (!editable || !needsPreview || !reason.trim() || !fresh) return;
    const reviewed = draft(), request = reads.begin(), ticket = previews.begin(reviewed);
    setBusy(true); setPreview(null); setConfirmed(false); setError(null);
    try {
      const result = await gateway.preview(reviewed, request.signal);
      if (request.current() && previews.accept(ticket, reviewed, result, now())) setPreview(previews.value);
    } catch (failure) { if (request.current()) { setError(failure); onFailure(failure); } }
    finally { if (request.current()) setBusy(false); }
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!editable || busy || !fresh || !confirmed || (needsPreview && !validPreview)) return;
    try { await onSubmit(personReviewCommand(draft(), needsPreview ? previews.value : null, now())); }
    catch (failure) { setError(failure); }
  }
  return <section className="ui-section" aria-labelledby="person-review-title" aria-busy={busy}>
    <h2 id="person-review-title" ref={heading} tabIndex={-1}>{original ? "Periksa pengajuan" : "Ajukan penangguhan keterkaitan"}</h2>
    {shownSummary && <><p><Badge status={personReviewStatus(shownSummary.result.status)} /></p><Facts items={[
      { term: "Pengaju", value: name(shownSummary.requester) }, { term: "Waktu tercatat", value: personReviewDate(shownSummary.result.recordedAt) },
      { term: "Versi pengajuan yang diperiksa", value: shownSummary.result.version }, { term: "Jumlah akun terdampak", value: shownSummary.affectedAccountCount }]} />
      <p className={styles.preserve}><strong>Alasan pengajuan</strong><br />{shownSummary.reason}</p></>}
    <AffectedAccounts accounts={affected} />
    <p className={styles.help}>Versi keterkaitan yang diperiksa: {preview?.target.version ?? target?.version ?? original?.result.groupVersion}.</p>
    {selection.kind === "proposal" && selection.detail.decision && <section className={styles.preview}><h3>Keputusan yang tercatat</h3>
      <p className={styles.preserve}>Diputuskan oleh <strong>{name(selection.detail.decision.actor)}</strong> · {personReviewDate(selection.detail.decision.decidedAt)}</p><p className={styles.preserve}>{selection.detail.decision.reason}</p></section>}
    {original?.result.status === "pending" && <Tabs label="Pilih keputusan" disabled={locked} current={action ?? ""} onSelect={value => { invalidate(); setAction(value as PersonReviewAction); }}
      items={(["approve", "reject", "withdraw"] as const).filter(item => personReviewActionAllowed(item, original, permissions)).map(item => ({ label: actionLabels[item], value: item }))} />}
    {action && canAct && <form className="ui-form" onSubmit={event => void submit(event)}>
      <fieldset disabled={locked}>
        <legend>{action === "propose" ? "Alasan dan pratinjau" : actionLabels[action]}</legend>
        <Field id="person-review-reason" label={"Alasan " + (action === "propose" ? "pengajuan" : "keputusan")}><textarea required rows={3} maxLength={1000} value={reason} onChange={event => { invalidate(); setReason(event.target.value); }} /></Field>
        {needsPreview && <><p className={styles.help}>{action === "propose" ? "Pengajuan akan menunggu keputusan petugas lain. Penangguhan diterapkan setelah disetujui." : "Persetujuan memerlukan petugas yang independen dari pengaju dan akun terdampak."}</p>
          <Button variant="secondary" type="button" disabled={!reason.trim() || busy || !fresh} onClick={() => void inspect()}>{busy ? "Menyiapkan pratinjau…" : "Periksa dampak terbaru"}</Button>
          {preview && <div className={styles.preview} role="status"><strong>{validPreview ? "Pratinjau siap diperiksa" : "Pratinjau telah berakhir"}</strong>
            <p>Berlaku sampai {personReviewDate(preview.expiresAt)}. Perubahan pilihan atau alasan memerlukan pratinjau baru.</p></div>}
          {validPreview && <StateView kind="unknown" title="Jika disetujui, keterkaitan akun di atas akan ditangguhkan. Sesi aplikasi akun terdampak akan dicabut, dan izin yang bergantung pada keterkaitan ini harus diperiksa kembali." />}
        </>}
        {!needsPreview && <p className={styles.help}>Keputusan ini mengakhiri pengajuan yang dipilih. Penangguhan keterkaitan akun tidak diterapkan oleh keputusan ini.</p>}
        <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={busy || (needsPreview && !validPreview) || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa akun, alasan, versi, dan dampak tindakan ini.</label>
        {!fresh && <p className={styles.help}>Verifikasi dua langkah terbaru diperlukan. Gunakan pilihan verifikasi di atas sebelum melanjutkan.</p>}
        {error !== null && <StateView kind="error" title={personReviewMessage(error)} />}
        <div className="ui-actions"><Button type="submit" variant="primary" disabled={!reason.trim() || busy || !confirmed || !fresh || (needsPreview && !validPreview)}>{actionLabels[action]}</Button>
          <Button variant="secondary" type="button" onClick={onCancel}>Batal</Button></div>
      </fieldset>
    </form>}
    {(!action || !canAct) && <Button variant="secondary" type="button" disabled={locked} onClick={onCancel}>Kembali ke daftar</Button>}
  </section>;
}