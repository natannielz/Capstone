import Link from "next/link";
import common from "@/modules/administration/access.module.css";

export type SessionAccessKind = "signin" | "denied" | "unavailable";

const signinCodes = new Set(["unauthenticated", "session_expired", "actor_changed", "reauthentication_required"]);

/** Why a workspace cannot open: the session needs signing in again, the account's role does not include the page, or the
 *  check itself could not finish. Each one asks for a different action, so each gets its own words. */
export function sessionAccessKind(error: unknown): SessionAccessKind {
  if (error === null || error === undefined) return "signin";
  // AccessFailure and RequestNotSentError both carry the HTTP status and the server's code.
  const failure = error instanceof Error ? error as Error & { status?: unknown; code?: unknown } : null;
  if (failure && typeof failure.status === "number" && typeof failure.code === "string") {
    if (failure.status === 401 || signinCodes.has(failure.code)) return "signin";
    if (failure.status === 403) return "denied";
  }
  return "unavailable";
}

export function sessionLoginHref(returnPath: string) { return "/identity/login?returnPath=" + encodeURIComponent(returnPath); }

/** The one state every workspace shows when it cannot open for this person. */
export function SessionAccessState({ error, returnPath, onRetry }: { error: unknown; returnPath: string; onRetry: () => void }) {
  const kind = sessionAccessKind(error);
  return <section className={common.state} aria-labelledby="workspace-access-state">
    {kind === "signin" ? <>
      <h2 id="workspace-access-state">Sesi Anda perlu diperbarui</h2>
      <p>Masuk kembali untuk melanjutkan. Pekerjaan yang sudah tersimpan tetap aman.</p>
      <div className={common.actions}><Link className={common.primary} href={sessionLoginHref(returnPath)}>Masuk kembali</Link>
        <button type="button" className={common.secondary} onClick={onRetry}>Periksa ulang</button></div>
    </> : kind === "denied" ? <>
      <h2 id="workspace-access-state">Halaman ini di luar tugas akun Anda</h2>
      <p>Akun Anda belum mendapat peran untuk halaman ini. Minta penanggung jawab akses di unit Anda menambahkan peran yang sesuai, lalu periksa ulang.</p>
      <div className={common.actions}><Link className={common.primary} href="/workspace">Ke Pekerjaan saya</Link>
        <button type="button" className={common.secondary} onClick={onRetry}>Periksa ulang akses</button></div>
    </> : <>
      <h2 id="workspace-access-state">Halaman belum dapat dibuka</h2>
      <p>Pemeriksaan akun belum selesai. Periksa koneksi, lalu coba lagi.</p>
      <div className={common.actions}><button type="button" className={common.primary} onClick={onRetry}>Coba lagi</button></div>
    </>}
  </section>;
}
