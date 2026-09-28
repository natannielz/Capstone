"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import type { PersonReviewGateway } from "./person-review-client";
import { LatestPersonReviewRead, PersonReviewPreviewSession, personReviewActionAllowed, personReviewCommand, personReviewDate, personReviewMessage, personReviewStatus,
  type PersonReviewAccount, type PersonReviewAction, type PersonReviewCommand, type PersonReviewDetail, type PersonReviewDraft, type PersonReviewPage,
  type PersonReviewPreview, type PersonReviewSummary, type PersonReviewTarget, type PersonReviewTargetPage } from "./person-review-workflow";
import styles from "../administration/access.module.css";

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
  return <section className={styles.queue} aria-labelledby="person-queue-title" aria-busy={busy}>
    <div className={styles.queueHeader}><h2 id="person-queue-title">Daftar pengajuan</h2><p className={styles.help}>Pengajuan yang tersedia dalam penugasan Anda.</p>
      <label>Status pengajuan<select value={query.status} disabled={locked} onChange={event => change({ status: event.target.value, offset: 0 })}>
        <option value="pending">Menunggu keputusan</option><option value="approved">Disetujui</option><option value="rejected">Ditolak</option><option value="withdrawn">Ditarik</option><option value="">Semua status</option>
      </select></label></div>
    {busy && <p role="status" className={styles.empty}>Memuat pengajuan…</p>}
    {error !== null && <p role="alert" className={styles.alert}>{personReviewMessage(error)}</p>}
    {page && <><p className={styles.empty} role="status">{page.total} pengajuan tersedia.</p>
      <ul className={styles.queueList}>{page.items.map(item => <li key={item.result.proposalId}><button type="button" className={styles.queueRow} disabled={locked} onClick={() => onSelect(item)}>
        <strong>Pengajuan oleh {name(item.requester)}</strong><span>{personReviewStatus(item.result.status)} · {item.affectedAccountCount} akun</span>
        <small>{personReviewDate(item.result.recordedAt)}</small><span className={styles.srOnly}>Buka rincian pengajuan</span>
      </button></li>)}</ul>
      {page.items.length === 0 && <p className={styles.empty}>Belum ada pengajuan pada status ini.</p>}
      <PersonReviewPagination offset={query.offset} total={page.total} locked={locked || busy} onChange={offset => change({ ...query, offset })} /></>}
  </section>;
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
  return <section className={styles.queue} aria-labelledby="person-search-title" aria-busy={busy}>
    <div className={styles.queueHeader}><h2 id="person-search-title">Pilih keterkaitan akun</h2><p className={styles.help}>Cari nama atau email dalam lingkup penugasan Anda. Periksa semua akun dalam hasil yang dipilih.</p>
      <form onSubmit={search} className={styles.lookup}><label>Cari nama atau email<input type="search" minLength={2} maxLength={80} autoComplete="off" value={input} disabled={locked} onChange={event => {
        reads.cancel(); setInput(event.target.value); setPage(null); setError(null); setBusy(false); onReset();
      }} /></label><button type="submit" className={styles.secondary} disabled={locked || input.trim().length < 2}>Cari</button></form></div>
    {busy && <p role="status" className={styles.empty}>Mencari akun yang dapat ditinjau…</p>}
    {error !== null && <p role="alert" className={styles.alert}>{personReviewMessage(error)}</p>}
    {page && <><p role="status" className={styles.empty}>{page.total} keterkaitan akun ditemukan.</p>
      <ul className={styles.queueList}>{page.items.map(item => <li key={item.groupId}><button type="button" className={styles.queueRow} disabled={locked} onClick={() => onSelect(item)}>
        <strong>{item.accounts.map(name).join(", ")}</strong><span>{personReviewStatus(item.status)} · {String(item.accounts.length)} akun terkait</span>
        <span>Pilih untuk memeriksa dampak</span>
      </button></li>)}</ul>
      {page.items.length === 0 && <p className={styles.empty}>Tidak ada keterkaitan akun yang cocok dalam lingkup Anda.</p>}
      <PersonReviewPagination offset={query?.offset ?? 0} total={page.total} locked={locked || busy} onChange={offset => query && change({ ...query, offset })} /></>}
  </section>;
}

function PersonReviewPagination({ offset, total, locked, onChange }: { offset: number; total: string; locked: boolean; onChange: (offset: number) => void }) {
  return <div className={styles.pagination} aria-label="Halaman hasil"><button type="button" className={styles.secondary} disabled={locked || offset === 0} onClick={() => onChange(Math.max(0, offset - 25))}>Sebelumnya</button>
    <span>Halaman {String(Math.floor(offset / 25) + 1)}</span><button type="button" className={styles.secondary} disabled={locked || offset >= 10000 || BigInt(offset + 25) >= BigInt(total)} onClick={() => onChange(offset + 25)}>Berikutnya</button></div>;
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
  return <section className={styles.detail} aria-labelledby="person-review-title" aria-busy={busy}><div className={styles.form}>
    <h2 id="person-review-title" ref={heading} tabIndex={-1}>{original ? "Periksa pengajuan" : "Ajukan penangguhan keterkaitan"}</h2>
    {shownSummary && <><p><strong>{personReviewStatus(shownSummary.result.status)}</strong></p><dl className={styles.facts}>
      <div><dt>Pengaju</dt><dd>{name(shownSummary.requester)}</dd></div><div><dt>Waktu tercatat</dt><dd>{personReviewDate(shownSummary.result.recordedAt)}</dd></div>
      <div><dt>Versi pengajuan yang diperiksa</dt><dd>{shownSummary.result.version}</dd></div><div><dt>Jumlah akun terdampak</dt><dd>{shownSummary.affectedAccountCount}</dd></div></dl>
      <p className={styles.preserve}><strong>Alasan pengajuan</strong><br />{shownSummary.reason}</p></>}
    <AffectedAccounts accounts={affected} />
    <p className={styles.help}>Versi keterkaitan yang diperiksa: {preview?.target.version ?? target?.version ?? original?.result.groupVersion}.</p>
    {selection.kind === "proposal" && selection.detail.decision && <section className={styles.review}><h3>Keputusan yang tercatat</h3>
      <p className={styles.preserve}>Diputuskan oleh <strong>{name(selection.detail.decision.actor)}</strong> · {personReviewDate(selection.detail.decision.decidedAt)}</p><p className={styles.preserve}>{selection.detail.decision.reason}</p></section>}
    {original?.result.status === "pending" && <div className={styles.actions} aria-label="Pilih keputusan">{(["approve", "reject", "withdraw"] as const).filter(item => personReviewActionAllowed(item, original, permissions)).map(item =>
      <button type="button" key={item} className={action === item ? styles.primary : styles.secondary} aria-pressed={action === item} disabled={locked} onClick={() => { invalidate(); setAction(item); }}>{actionLabels[item]}</button>)}</div>}
    {action && canAct && <form onSubmit={event => void submit(event)}>
      <fieldset className={styles.fields} disabled={locked}>
        <legend>{action === "propose" ? "Alasan dan pratinjau" : actionLabels[action]}</legend>
        <label>Alasan {action === "propose" ? "pengajuan" : "keputusan"}<textarea required rows={3} maxLength={1000} value={reason} onChange={event => { invalidate(); setReason(event.target.value); }} /></label>
        {needsPreview && <><p className={styles.help}>{action === "propose" ? "Pengajuan akan menunggu keputusan petugas lain. Penangguhan diterapkan setelah disetujui." : "Persetujuan memerlukan petugas yang independen dari pengaju dan akun terdampak."}</p>
          <button type="button" className={styles.secondary} disabled={!reason.trim() || busy || !fresh} onClick={() => void inspect()}>{busy ? "Menyiapkan pratinjau…" : "Periksa dampak terbaru"}</button>
          {preview && <div className={styles.preview} role="status"><strong>{validPreview ? "Pratinjau siap diperiksa" : "Pratinjau telah berakhir"}</strong>
            <p>Berlaku sampai {personReviewDate(preview.expiresAt)}. Perubahan pilihan atau alasan memerlukan pratinjau baru.</p></div>}
          {validPreview && <p className={styles.alert}>Jika disetujui, keterkaitan akun di atas akan ditangguhkan. Sesi aplikasi akun terdampak akan dicabut, dan izin yang bergantung pada keterkaitan ini harus diperiksa kembali.</p>}
        </>}
        {!needsPreview && <p className={styles.help}>Keputusan ini mengakhiri pengajuan yang dipilih. Penangguhan keterkaitan akun tidak diterapkan oleh keputusan ini.</p>}
        <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={busy || (needsPreview && !validPreview) || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa akun, alasan, versi, dan dampak tindakan ini.</label>
        {!fresh && <p className={styles.help}>Verifikasi dua langkah terbaru diperlukan. Gunakan pilihan verifikasi di atas sebelum melanjutkan.</p>}
        {error !== null && <p role="alert" className={styles.alert}>{personReviewMessage(error)}</p>}
        <div className={styles.actions}><button type="submit" className={styles.primary} disabled={!reason.trim() || busy || !confirmed || !fresh || (needsPreview && !validPreview)}>{actionLabels[action]}</button>
          <button type="button" className={styles.secondary} onClick={onCancel}>Batal</button></div>
      </fieldset>
    </form>}
    {(!action || !canAct) && <button type="button" className={styles.secondary} disabled={locked} onClick={onCancel}>Kembali ke daftar</button>}
  </div></section>;
}