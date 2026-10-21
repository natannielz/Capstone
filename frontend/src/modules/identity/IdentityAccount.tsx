"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { createAccessGateway } from "../administration/access-client";
import { AccessFailure } from "../administration/workflow";
import { permissionLabel } from "../administration/permission-labels";
import { createIdentityGateway } from "./identity-client";
import { useRouter } from "next/navigation";
import { HOME_PATH, IdentityFailure, homeFor, identityMessage, identityReturnPath, isStaffActor, sessionTiming, stepUpFields, storeDoor, type IdentitySession } from "./identity-workflow";
import { SessionAccessState } from "../../components/workspace-kit/session-access-state";
import { ProtectedNavigationForm } from "./ProtectedNavigationForm";
import { WorkspaceDirectory } from "./WorkspaceDirectory";
import { WorkSummary } from "./WorkSummary";
import { Brand } from "../../components/brand";
import { AccountContactPanel } from "./AccountContactPanel";
import styles from "./identity.module.css";
import { isPilotRelease } from '../../lib/release-scope';
import { dateTimeFormatter } from "@/lib/format";

const displayTime = (value: string | null) => value ? dateTimeFormatter.format(new Date(value)) : "—";
const availableStepUp = (session: IdentitySession) => session.stepUpOperations.filter(operation => !isPilotRelease || ['catalog.manage', 'order.request.submit', 'order.request.decide', 'order.request.read'].includes(operation));

/**
 * One home per person (G-020). With variant "workspace" this is the staff home (/workspace): waiting work, the workspaces and
 * the door to the store; a buyer without back-office work is sent on to the store. Without it this is "Akun saya": profile,
 * verification, contact and sign-out only, for everyone, with the way back to that person's home.
 */
export function IdentityAccount({ variant = "account" }: { variant?: "account" | "workspace" } = {}) {
  const home = variant === "workspace";
  const router = useRouter();
  const [gateway] = useState(() => createIdentityGateway());
  const [access] = useState(() => createAccessGateway());
  const [session, setSession] = useState<IdentitySession | null>(null);
  const [actions, setActions] = useState<readonly string[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [navigationError, setNavigationError] = useState<'denied' | 'unavailable' | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [operation, setOperation] = useState("");
  const [returnPath, setReturnPath] = useState("/identity/account");
  const [observedAt, setObservedAt] = useState(0);
  const [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const result = await gateway.session(controller.signal);
        if (controller.signal.aborted) return;
        setSession(result); setObservedAt(performance.now()); setElapsed(0); setError(null);
        const params = new URL(window.location.href).searchParams;
        setReturnPath(identityReturnPath(params.get("returnPath"), "/identity/account"));
        const visibleOperations = availableStepUp(result);
        setOperation(visibleOperations.includes(params.get("operation") ?? "") ? params.get("operation")! : visibleOperations[0] ?? "");
        if (result.authenticated && result.account) {
          try {
            const current = await access.current();
            if (controller.signal.aborted) return;
            if (current.userId !== result.account.userId) throw new IdentityFailure(409, "actor_changed");
            setActions(current.allowedActions); setNavigationError(null);
          } catch (failure) {
            if (controller.signal.aborted) return;
            if (failure instanceof IdentityFailure && failure.code === "actor_changed") throw failure;
            if (failure instanceof AccessFailure && failure.status === 401) throw new IdentityFailure(401, "unauthenticated");
            setActions([]); setNavigationError(failure instanceof AccessFailure && failure.status === 403 ? 'denied' : 'unavailable');
          }
        } else { setActions([]); setNavigationError(null); }
      } catch (failure) {
        if (!controller.signal.aborted) { setSession(null); setActions([]); setError(failure); }
      } finally { if (!controller.signal.aborted) setLoading(false); }
    })();
    return () => controller.abort();
  }, [gateway, access, attempt]);
  useEffect(() => {
    const restore = (event: PageTransitionEvent) => {
      if (!event.persisted) return;
      setSession(null); setActions([]); setLoading(true); setError(null); setAttempt(value => value + 1);
    };
    window.addEventListener("pageshow", restore);
    return () => window.removeEventListener("pageshow", restore);
  }, []);
  // Display-only clock: no polling that silently extends an idle session.
  useEffect(() => {
    if (!session?.authenticated) return;
    const timer = window.setInterval(() => setElapsed(performance.now() - observedAt), 1000);
    return () => window.clearInterval(timer);
  }, [session?.authenticated, observedAt]);
  const timing = session ? sessionTiming(session, elapsed) : { expired: true, fresh: false };
  let verificationFields: Record<string, string> | null = null;
  if (session && operation) { try { verificationFields = stepUpFields(session, operation, returnPath); } catch { /* Permission list may change on refresh. */ } }
  const reload = () => { setLoading(true); setSession(null); setActions([]); setError(null); setAttempt(value => value + 1); };
  // A buyer without back-office work has the store as home, not this workspace (G-020).
  const elsewhere = home && !loading && session?.account && !timing.expired && navigationError === null && !isStaffActor(actions) ? homeFor(actions) : null;
  useEffect(() => { if (elsewhere && elsewhere !== HOME_PATH) router.replace(elsewhere); }, [elsewhere, router]);
  const door = storeDoor(actions), personalHome = homeFor(actions);

  return <main className={styles.page}>
    <a className={styles.skip} href="#account-content">Langsung ke akun</a>
    <header className={styles.masthead}><div className={styles.mastheadInner}><Brand href="/" context="Layanan organisasi" /></div></header>
    <div className={styles.accountHeader}><div>{home ? <><h1>Pekerjaan saya</h1><p>{session?.account?.displayName ? "Halo, " + session.account.displayName + ". " : ""}Pekerjaan yang menunggu Anda dan ruang kerja sesuai penugasan.</p></>
      : <><h1>Akun saya</h1><p>Profil, verifikasi dua langkah, kontak dan keluar dari Unit Toko.</p></>}</div>
      <button type="button" className={styles.secondary} disabled={loading} onClick={reload}>Muat ulang sesi dan akses</button></div>
    <div id="account-content">
      {loading && <section className={styles.card} aria-busy="true"><p role="status">Memeriksa akun dan izin terbaru…</p></section>}
      {error !== null && <SessionAccessState error={error} returnPath={home ? HOME_PATH : "/identity/account"} onRetry={reload}
        detail={identityMessage(error) + (error instanceof IdentityFailure && error.traceId ? " Referensi bantuan: " + error.traceId : "")} />}
      {!loading && session && (!session.authenticated || timing.expired) &&
        <SessionAccessState error={null} kind={session.authenticated ? "expired" : "signin"} returnPath={home ? HOME_PATH : "/identity/account"} onRetry={reload} />}
      {!loading && session?.account && !timing.expired && <>
        {home && elsewhere && <p role="status" className={styles.help}>Membuka toko untuk akun Anda…</p>}
        {home && !elsewhere && <>
          {!isPilotRelease && navigationError === null && <WorkSummary key={"work:" + session.account.userId} actorId={session.account.userId} actions={actions} heading="Menunggu tindakan Anda" />}
          {door && navigationError === null && <section className={styles.card} aria-labelledby="store-door-heading"><h2 id="store-door-heading">{door.label}</h2><p>{door.description}</p>
            <Link href={door.href} className={styles.primary}>{door.label}</Link></section>}
          <WorkspaceDirectory actions={actions} accessState={navigationError} />
          <p className={styles.help}>Profil, verifikasi dua langkah untuk persetujuan, kontak dan keluar ada di <Link href="/identity/account#profile-heading">Akun saya</Link>.</p>
        </>}
        {!home && <>
        {navigationError === null && <p className={styles.help}>{personalHome === HOME_PATH ? <Link href={HOME_PATH}>Kembali ke Pekerjaan saya</Link>
          : personalHome === "/shop" ? <Link href="/shop">Kembali ke toko</Link> : <Link href="/">Kembali ke beranda</Link>}</p>}
        <div className={styles.accountGrid}>
          <section className={styles.card} aria-labelledby="profile-heading"><span className={styles.status}>Akun terverifikasi</span>
            <h2 id="profile-heading" tabIndex={-1}>{session.account.displayName ?? "Akun organisasi"}</h2>
            <dl className={styles.facts}><div><dt>Email terverifikasi</dt><dd>{session.account.verifiedEmail}</dd></div><div><dt>Nomor akun</dt><dd>{session.account.userId}</dd></div>
              <div><dt>Verifikasi dua langkah terakhir</dt><dd>{displayTime(session.factorCompletedAt)}</dd></div><div><dt>Batas sesi tanpa aktivitas</dt><dd>{displayTime(session.idleExpiresAt)}</dd></div>
              <div><dt>Batas akhir sesi</dt><dd>{displayTime(session.absoluteExpiresAt)}</dd></div></dl>
            <p className={styles.help}>Waktu menggunakan WIB. Memeriksa sesi memperbarui batas aktivitas; batas akhir sesi tetap berlaku.</p>
            <ProtectedNavigationForm action="/auth/logout" fields={{ expectedActorId: session.account.userId }} label="Keluar dari Unit Toko" secondary />
          </section>
          <section className={styles.card} aria-labelledby="factor-heading"><span className={timing.fresh ? styles.status : styles.neutralStatus}>{timing.fresh ? "Verifikasi masih baru" : "Verifikasi tambahan dapat diperlukan"}</span>
            <h2 id="factor-heading">Verifikasi tindakan penting</h2><p>Beberapa tindakan memerlukan verifikasi dua langkah terbaru. Pilih tindakan yang akan Anda kerjakan, lalu selesaikan verifikasi melalui layanan akun.</p>
            {availableStepUp(session).length > 0 ? <>
              <label htmlFor="step-up-operation">Tindakan<select id="step-up-operation" value={operation} onChange={event => setOperation(event.target.value)}>
                {availableStepUp(session).map(item => <option key={item} value={item}>{permissionLabel(item)}</option>)}
              </select></label>
              <ProtectedNavigationForm action="/auth/step-up" fields={verificationFields ?? {}} disabled={!verificationFields} label="Verifikasi dua langkah" />
              <p className={styles.help}>Verifikasi tidak menyetujui atau mengirim tindakan bisnis. Anda tetap perlu memeriksa dan mengirim tindakan tersebut setelah kembali.</p>
            </> : <p className={styles.help}>Belum ada tindakan dalam penugasan Anda yang memerlukan verifikasi tambahan melalui halaman ini.</p>}
          </section>
        </div>
        {!isPilotRelease && <AccountContactPanel key={"contact:" + session.account.userId} actorId={session.account.userId} />}
        <aside className={styles.support}><h2>Perlu memulihkan akses?</h2><p>Hubungi pengelola organisasi untuk memulai pemulihan melalui proses resmi. Anda sendiri yang menetapkan kembali akses di layanan akun. Pengelola tidak perlu mengetahui kata sandi atau kode verifikasi Anda.</p></aside></>}
      </>}
    </div>
    <footer className={styles.footer}>Unit Toko. Akses khusus organisasi yang disetujui.</footer>
  </main>;
}
