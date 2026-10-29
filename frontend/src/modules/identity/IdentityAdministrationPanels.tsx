"use client";

import { useEffect, useState, type FormEvent } from "react";
import type { components } from "../../api/generated/schema";
import { accessMessage } from "../administration/workflow";
import { describeScope } from "../organizations/access-scope";
import type { IdentityAdministrationGateway, InvitationAction } from "./identity-administration-client";
import styles from "../administration/access.module.css";
import { Button } from "@/components/workspace-kit/button";
import { Section } from "@/components/workspace-kit/section";
import { DataList } from "@/components/workspace-kit/data-list";
import { Toolbar } from "@/components/workspace-kit/toolbar";
import { Field } from "@/components/workspace-kit/field";
import { Badge } from "@/components/workspace-kit/badge";
import type { TablePagination } from "@/components/workspace-kit/data-table";
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
  return <Section title="Direktori akun" description="Cari nama atau email terverifikasi dalam lingkup yang boleh Anda tinjau." busy={busy}>
    <form onSubmit={search}><Toolbar label="Cari akun" search={<Field id="identity-directory-search" label="Cari akun" width="search"><input value={input} onChange={event => setInput(event.target.value)} minLength={2} maxLength={80} disabled={locked || busy} type="search" autoComplete="off" /></Field>}
      filters={<Button type="submit" variant="secondary" disabled={locked || busy}>Cari</Button>} resultText={page ? `${page.total} akun dapat ditinjau.` : undefined} /></form>
    <DataList label="Direktori akun" items={page?.items ?? []} getKey={profile => profile.userId} loading={busy}
      error={error !== null ? { message: accessMessage(error) } : undefined} empty={page ? { title: "Tidak ada akun yang cocok dalam lingkup Anda." } : undefined}
      renderTitle={profile => profile.displayName ?? "Nama belum ditetapkan"} renderBadge={profile => <Badge status={invitationStatus(profile.status)} />}
      renderMeta={profile => <>Email terverifikasi {jakartaTime(profile.verifiedAt)} · Akun {profile.userId}</>}
      renderContent={profile => <p className={styles.preserve}>{profile.verifiedEmail}</p>}
      renderActions={canRecover ? profile => <Button variant="secondary" disabled={locked} onClick={() => onSelect({ kind: "recovery", value: profile })}>Tinjau pemulihan {profile.displayName ?? profile.verifiedEmail}</Button> : undefined}
      pagination={page ? pager(query.offset, page.items.length, page.total, locked || busy, offset => setQuery({ ...query, offset })) : undefined} />
  </Section>;
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
  return <Section title="Tinjau undangan" description="Persetujuan keanggotaan diperiksa terpisah dari verifikasi email. Pengiriman undangan melalui penyedia belum aktif." busy={busy}>
    {page && <p className={styles.help}>{page.total} undangan dalam lingkup Anda.</p>}
    <DataList label="Tinjau undangan" items={page?.items ?? []} getKey={invitation => invitation.id} loading={busy}
      error={error !== null ? { message: accessMessage(error) } : undefined} empty={page ? { title: "Tidak ada undangan yang dapat ditinjau." } : undefined}
      renderTitle={invitation => <span className={styles.preserve}>{invitation.intendedEmail}</span>} renderBadge={invitation => <Badge status={invitationStatus(invitation.status)} />}
      renderMeta={invitation => <>Berlaku sampai {jakartaTime(invitation.expiresAt)} · Versi {invitation.version}</>}
      renderContent={invitation => <p>{describeScope(invitation.scope)}</p>}
      renderActions={invitation => <>{invitation.allowedActions.filter((action): action is InvitationAction => Object.hasOwn(actions, action)).map(action => <Button key={action} variant="secondary" disabled={locked} onClick={() => onSelect({ kind: "invitation", value: invitation, action })}>{actions[action]} undangan {invitation.id}</Button>)}</>}
      pagination={page ? pager(offset, page.items.length, page.total, locked || busy, setOffset) : undefined} />
  </Section>;
}

/** Offset pages of 25 (the API's page size), as the kit pager: hidden when everything fits. */
function pager(offset: number, count: number, total: string, locked: boolean, onChange: (offset: number) => void): TablePagination {
  return { label: "Halaman hasil", start: offset + 1, end: offset + count, total: Number(total), hasPrevious: offset > 0, hasNext: BigInt(offset + 25) < BigInt(total),
    disabled: locked, onPrevious: () => onChange(Math.max(0, offset - 25)), onNext: () => onChange(offset + 25) };
}