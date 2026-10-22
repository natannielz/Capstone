"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import common from "@/modules/administration/access.module.css";
import { isReturnQuery } from "@/modules/identity/return-query";

export type SessionAccessKind = "signin" | "expired" | "denied" | "unavailable";

// One rule for every protected page (round 3, Option A): the request guard (src/proxy.ts) sends visitors without a session cookie
// to /identity/login, so a page that learns "no session" (the session read, access/current or any API answering 401, on load or
// mid-work) is talking to someone who held a session: it ended. The sign-in state appears only where a page asks for it with
// kind="signin"; no protected page does, and guest pages never render this component.
const expiredCodes = new Set(["session_expired", "actor_changed", "reauthentication_required", "unauthenticated"]);

/** Why a page cannot open: a session that ended (sign in again), the account's role does not include the page, or the check itself
 *  could not finish. Each asks for a different action, so each has its own words, everywhere (G-014). */
export function sessionAccessKind(error: unknown): SessionAccessKind {
  if (error === null || error === undefined) return "expired";
  // AccessFailure, IdentityFailure and RequestNotSentError all carry the HTTP status and the server's code.
  const failure = error instanceof Error ? error as Error & { status?: unknown; code?: unknown } : null;
  if (failure && typeof failure.status === "number" && typeof failure.code === "string") {
    if (failure.status === 401 || expiredCodes.has(failure.code)) return "expired";
    if (failure.status === 403) return "denied";
  }
  return "unavailable";
}

// A plain "no permission" refusal says nothing more than the shared denied text.
const genericDenialCodes = new Set(["access_denied", "scope_denied", "permission_denied", "forbidden", "not_authorized"]);
function genericDenial(error: unknown): boolean {
  const code = error instanceof Error ? (error as Error & { code?: unknown }).code : undefined;
  return typeof code !== "string" || genericDenialCodes.has(code);
}

/** For a page that already had a session (work in progress): a missing session now means it ended. */
export function lostAccessKind(error: unknown): SessionAccessKind {
  const kind = sessionAccessKind(error);
  return kind === "signin" ? "expired" : kind;
}

export function sessionLoginHref(returnPath: string) { return "/identity/login?returnPath=" + encodeURIComponent(returnPath); }

/**
 * Where signing in again returns: the page itself with the record it has open (`?draft=7`, `?order=41`), so work continues
 * where it stopped (G-011). Only the page's own address counts, and only a query the API accepts as a return destination.
 */
export function returnTarget(pagePath: string, location: { pathname: string; search: string }): string {
  const query = location.search.startsWith("?") ? location.search.slice(1) : location.search;
  return location.pathname === pagePath && query && isReturnQuery(query) ? pagePath + "?" + query : pagePath;
}

/** The current page's return target, read after mount (the address bar is not known while rendering on the server). */
export function useReturnTarget(pagePath: string): string {
  const [target, setTarget] = useState(pagePath);
  useEffect(() => {
    const read = () => { if (window.location) setTarget(returnTarget(pagePath, window.location)); };
    read(); window.addEventListener("popstate", read);
    return () => window.removeEventListener("popstate", read);
  }, [pagePath]);
  return target;
}

/**
 * The one state every page shows when it cannot open for this person, before loading or after its session or access changed
 * mid-work. `kind` names a state the page already knows (an editor that saw its session end, or access withdrawn); `busy`
 * keeps the retry from interrupting a command in flight; `detail` is the page's own reason, shown for a denied or unavailable page
 * (signing in needs no further reason).
 */
export function SessionAccessState({ error, returnPath, onRetry, kind: known, busy = false, detail }:
  { error: unknown; returnPath: string; onRetry: () => void; kind?: SessionAccessKind; busy?: boolean; detail?: string }) {
  const kind = known ?? sessionAccessKind(error);
  const target = useReturnTarget(returnPath);
  return <section className={common.state} aria-labelledby="workspace-access-state">
    {kind === "signin" ? <>
      <h2 id="workspace-access-state">Masuk untuk melanjutkan</h2>
      <p>Halaman ini untuk akun organisasi yang disetujui. Masuk dengan akun Anda untuk membukanya.</p>
      <div className={common.actions}><Link className={common.primary} href={sessionLoginHref(target)}>Masuk</Link>
        <button type="button" className={common.secondary} disabled={busy} onClick={onRetry}>Periksa ulang</button></div>
    </> : kind === "expired" ? <>
      <h2 id="workspace-access-state">Sesi Anda perlu diperbarui</h2>
      <p>Masuk kembali untuk melanjutkan. Pekerjaan yang sudah tersimpan tetap aman.</p>
      <div className={common.actions}><Link className={common.primary} href={sessionLoginHref(target)}>Masuk kembali</Link>
        <button type="button" className={common.secondary} disabled={busy} onClick={onRetry}>Periksa ulang</button></div>
    </> : kind === "denied" ? <>
      <h2 id="workspace-access-state">Halaman ini di luar tugas akun Anda</h2>
      {/* One body: a page's own reason replaces the generic one only when the refusal is specific (e.g. a scope that changed). */}
      {detail && !genericDenial(error) ? <p role="alert">{detail}</p>
        : <p>Akun Anda belum mendapat peran untuk halaman ini. Minta penanggung jawab akses di unit Anda menambahkan peran yang sesuai, lalu periksa ulang.</p>}
      <div className={common.actions}><Link className={common.primary} href="/workspace">Ke Pekerjaan saya</Link>
        <button type="button" className={common.secondary} disabled={busy} onClick={onRetry}>Periksa ulang akses</button></div>
    </> : <>
      <h2 id="workspace-access-state">Halaman belum dapat dibuka</h2>
      {/* The page's own reason (e.g. data that failed its integrity check) replaces the generic connection hint. */}
      {detail ? <p role="alert">{detail}</p> : <p>Pemeriksaan akun belum selesai. Periksa koneksi, lalu coba lagi.</p>}
      <div className={common.actions}><button type="button" className={common.primary} disabled={busy} onClick={onRetry}>Coba lagi</button></div>
    </>}
  </section>;
}
