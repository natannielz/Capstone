"use client";

import { useEffect, useState, type FormEvent } from "react";
import type { components } from "../../api/generated/schema";
import { accessMessage } from "../administration/workflow";
import { describeScope } from "../organizations/access-scope";
import type { IdentityAdministrationGateway, InvitationAction } from "./identity-administration-client";
import styles from "../administration/access.module.css";
import { dateTimeFormatter } from "@/lib/format";

type Schemas = components["schemas"];
export type Profile = Schemas["IdentityProfileDto"];
export type Invitation = Schemas["IdentityInvitationDto"];
export type IdentityDecision = { kind: "invitation"; value: Invitation; action: InvitationAction } | { kind: "recovery"; value: Profile };
const actions: Record<InvitationAction, string> = { approve: "Setujui", reject: "Tolak", withdraw: "Tarik pengajuan", revoke: "Cabut undangan" };
export const invitationStatus = (status: string) => ({ pending: "Menunggu persetujuan", approved: "Disetujui · pengiriman tertahan", rejected: "Ditolak", withdrawn: "Ditarik", revoked: "Dicabut", redeemed: "Telah digunakan", active: "Aktif", suspended: "Ditangguhkan" } as Record<string, string>)[status] ?? status;
export const jakartaTime = (value: string) => dateTimeFormatter.format(new Date(value));

type Common = { gateway: IdentityAdministrationGateway; actorId: string; revision: number; locked: boolean; onSelect: (decision: IdentityDecision) => void; onFailure: (error: unknown) => void };
export function AccountDirectory({ gateway, actorId, revision, locked, onSelect, onFailure, canRecover }: Common & { canRecover: boolean }) {
  const [input, setInput] = useState("");
  const [query, setQuery] = useState({ term: "", offset: 0 });
  const [page, setPage] = useState<Schemas["IdentityDirectoryPageDto"] | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(async () => {
      if (!active) return;
      setBusy(true); setError(null); setPage(null);
      try { const next = await gateway.profiles(actorId, query.term, query.offset); if (active) setPage(next); }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } }
      finally { if (active) setBusy(false); }
    });
    return () => { active = false; };
  }, [gateway, actorId, revision, query, onFailure]);
  function search(event: FormEvent) { event.preventDefault(); if (!locked && !busy) setQuery({ term: input.trim(), offset: 0 }); }
  return <section className={styles.accountSection} aria-busy={busy}>
    <h2>Direktori akun</h2><p className={styles.help}>Cari nama atau email terverifikasi dalam lingkup yang boleh Anda tinjau.</p>
    <form onSubmit={search} className={styles.lookup}><label>Cari akun<input value={input} onChange={event => setInput(event.target.value)} minLength={2} maxLength={80} disabled={locked || busy} type="search" autoComplete="off" /></label><button className={styles.secondary} disabled={locked || busy}>Cari</button></form>
    {error !== null && <p role="alert" className={styles.alert}>{accessMessage(error)}</p>}
    {busy && <p role="status">Memuat akun…</p>}
    {page && <><p role="status">{page.total} akun dapat ditinjau.</p><ul className={styles.historyList}>
      {page.items.map(profile => <li key={profile.userId}><div className={styles.historyHeading}><strong>{profile.displayName ?? "Nama belum ditetapkan"}</strong><span>{invitationStatus(profile.status)}</span></div>
        <p className={styles.preserve}>{profile.verifiedEmail}</p><p className={styles.help}>Email terverifikasi {jakartaTime(profile.verifiedAt)} · Akun {profile.userId}</p>
        {canRecover && <button type="button" className={styles.secondary} disabled={locked} onClick={() => onSelect({ kind: "recovery", value: profile })}>Tinjau pemulihan {profile.displayName ?? profile.verifiedEmail}</button>}
      </li>)}
    </ul>{page.items.length === 0 && <p>Tidak ada akun yang cocok dalam lingkup Anda.</p>}
    <Pagination offset={query.offset} total={page.total} locked={locked || busy} onChange={offset => setQuery({ ...query, offset })} /></>}
  </section>;
}

export function InvitationQueue({ gateway, actorId, revision, locked, onSelect, onFailure }: Common) {
  const [offset, setOffset] = useState(0);
  const [page, setPage] = useState<Schemas["IdentityInvitationPageDto"] | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(async () => {
      if (!active) return;
      setBusy(true); setError(null); setPage(null);
      try { const next = await gateway.invitations(actorId, offset); if (active) setPage(next); }
      catch (failure) { if (active) { setError(failure); onFailure(failure); } }
      finally { if (active) setBusy(false); }
    });
    return () => { active = false; };
  }, [gateway, actorId, offset, revision, onFailure]);
  return <section className={styles.accountSection} aria-busy={busy}><h2>Tinjau undangan</h2>
    <p className={styles.help}>Persetujuan keanggotaan diperiksa terpisah dari verifikasi email. Pengiriman undangan melalui penyedia belum aktif.</p>
    {error !== null && <p role="alert" className={styles.alert}>{accessMessage(error)}</p>}
    {busy && <p role="status">Memuat undangan…</p>}
    {page && <><p>{page.total} undangan dalam lingkup Anda.</p><ul className={styles.historyList}>{page.items.map(invitation => <li key={invitation.id}>
      <div className={styles.historyHeading}><strong className={styles.preserve}>{invitation.intendedEmail}</strong><span>{invitationStatus(invitation.status)}</span></div>
      <p>{describeScope(invitation.scope)}</p><p className={styles.help}>Berlaku sampai {jakartaTime(invitation.expiresAt)} · Versi {invitation.version}</p>
      <div className={styles.actions}>{invitation.allowedActions.filter((action): action is InvitationAction => Object.hasOwn(actions, action)).map(action => <button type="button" className={styles.secondary} key={action} disabled={locked} onClick={() => onSelect({ kind: "invitation", value: invitation, action })}>{actions[action]} undangan {invitation.id}</button>)}</div>
    </li>)}</ul>{page.items.length === 0 && <p>Tidak ada undangan yang dapat ditinjau.</p>}
    <Pagination offset={offset} total={page.total} locked={locked || busy} onChange={setOffset} /></>}
  </section>;
}

function Pagination({ offset, total, locked, onChange }: { offset: number; total: string; locked: boolean; onChange: (offset: number) => void }) {
  return <div className={styles.actions} aria-label="Halaman hasil"><button type="button" className={styles.secondary} disabled={locked || offset === 0} onClick={() => onChange(Math.max(0, offset - 25))}>Sebelumnya</button>
    <span>Halaman {Math.floor(offset / 25) + 1}</span><button type="button" className={styles.secondary} disabled={locked || BigInt(offset + 25) >= BigInt(total)} onClick={() => onChange(offset + 25)}>Berikutnya</button></div>;
}