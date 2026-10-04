"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { ArrowUpRight, ShieldCheck } from "lucide-react";
import { Art } from "../../components/art";
import { PilotSiteFooter, PilotSiteHeader } from "../../components/pilot-site-chrome";
import { createIdentityGateway } from "./identity-client";
import { IdentityFailure, identityMessage, identityReturnPath, loginFields, type IdentitySession } from "./identity-workflow";
import { ProtectedNavigationForm } from "./ProtectedNavigationForm";
import styles from "./identity-login.module.css";
import { isPilotRelease } from '../../lib/release-scope';

export function IdentityLogin() {
  const [gateway] = useState(() => createIdentityGateway());
  const [session, setSession] = useState<IdentitySession | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [attempt, setAttempt] = useState(0);
  const [invited, setInvited] = useState(false);
  const [invitationId, setInvitationId] = useState("");
  const [invitationSecret, setInvitationSecret] = useState("");
  const [returnPath, setReturnPath] = useState("/identity/account");
  const [loggedOut, setLoggedOut] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    void gateway.session(controller.signal).then(value => {
      if (!controller.signal.aborted) {
        const params = new URL(window.location.href).searchParams;
        setReturnPath(identityReturnPath(params.get("returnPath"))); setLoggedOut(params.get("loggedOut") === "1");
        setSession(value); setError(null); setLoading(false);
      }
    }, failure => {
      if (!controller.signal.aborted) { setSession(null); setError(failure); setLoading(false); }
    });
    return () => controller.abort();
  }, [gateway, attempt]);
  useEffect(() => {
    const restore = (event: PageTransitionEvent) => {
      if (!event.persisted) return;
      setInvitationId(""); setInvitationSecret(""); setSession(null); setLoading(true); setError(null);
      setAttempt(value => value + 1);
    };
    window.addEventListener("pageshow", restore);
    return () => window.removeEventListener("pageshow", restore);
  }, []);
  let fields: Record<string, string> | null = null;
  try { fields = loginFields(returnPath, invited ? invitationId.trim() : "", invited ? invitationSecret.trim() : "", invited); } catch { /* Incomplete form remains disabled. */ }

  return <div className={styles.page}>
    <a className={styles.skip} href="#identity-content">Langsung ke akun</a>
    <PilotSiteHeader active="account" />
    <main className={styles.loginGrid} id="identity-content" tabIndex={-1}>
      <section className={styles.introduction} aria-labelledby="login-heading">
        <span className={styles.eyebrow}>RUANG KERJA UNIT TOKO</span>
        <h1 id="login-heading">Kebutuhan cabang.<br /><span>Lebih tertata.</span></h1>
        <p>Mulai dari pilihan kemasan hingga permintaan cabang. Terhubung dengan kebutuhan organisasi Anda, dalam satu tempat.</p>
        <figure className={styles.art}>
          <Art src="/images/editorial/hero-still-life-v3.png" alt="Tumbler, tas kanvas, kopi, dan perlengkapan untuk keseharian" loading="eager" fetchPriority="high" sizes="(max-width: 760px) 100vw, 50vw" />
          <figcaption><span>Untuk keseharian yang lebih tertata.</span><span aria-hidden="true">UNIT TOKO</span></figcaption>
        </figure>
      </section>
      <div className={styles.formSide}>
      <section className={styles.card} aria-labelledby="signin-heading" aria-busy={loading}>
        <header className={styles.cardHeader}>
          <span className={styles.symbol}><ShieldCheck size={25} aria-hidden="true" /></span>
          <span className={styles.eyebrow}>AKUN ORGANISASI ANDA</span>
          <h2 id="signin-heading">Masuk ke Unit Toko</h2>
          <p>Untuk cabang BNI dan mitra usaha yang telah disetujui.</p>
        </header>
        {loggedOut && session && !session.authenticated && <p className={styles.success} role="status">Tidak ada sesi Unit Toko aktif di peramban ini.</p>}
        {loading && <p className={styles.loading} role="status"><span aria-hidden="true" />Memeriksa layanan akun…</p>}
        {error !== null && <div className={styles.alert} role="alert"><p>{identityMessage(error)}</p>
          {error instanceof IdentityFailure && error.traceId && <small>Referensi bantuan: {error.traceId}</small>}
          <button type="button" className={styles.secondary} onClick={() => { setLoading(true); setError(null); setAttempt(value => value + 1); }}>Periksa kembali</button>
        </div>}
        {!loading && session?.authenticated && <><p>Anda sudah masuk sebagai <strong>{session.account?.displayName ?? session.account?.verifiedEmail}</strong>.</p>
          <Link href="/identity/account" className={styles.primary}>Buka akun saya</Link>
          <p className={styles.help}>Untuk menggunakan akun lain, keluar terlebih dahulu melalui halaman akun.</p></>}
        {!loading && session && !session.authenticated && <>
          <p className={styles.signinCopy}>Anda akan diarahkan ke layanan akun untuk masuk dan menyelesaikan verifikasi.</p>
          {!isPilotRelease && <label className={styles.checkbox}><input type="checkbox" checked={invited} onChange={event => {
            setInvited(event.target.checked); setInvitationId(""); setInvitationSecret("");
          }} />Saya menerima undangan akun baru</label>}
          {invited && <div className={styles.invitationFields}>
            <p className={styles.help} id="invite-help">Masukkan nomor dan kode dari undangan yang disetujui pengelola. Setelah itu, masuk dengan email penerima undangan.</p>
            <label htmlFor="invitation-id">Nomor undangan<input id="invitation-id" value={invitationId} onChange={event => setInvitationId(event.target.value)} inputMode="numeric" maxLength={19} autoComplete="off" aria-describedby="invite-help" /></label>
            <label htmlFor="invitation-secret">Kode undangan<input id="invitation-secret" type="password" value={invitationSecret} onChange={event => setInvitationSecret(event.target.value)} maxLength={64} autoComplete="off" spellCheck={false} aria-describedby="invite-secret-help" /></label>
            <p className={styles.help} id="invite-secret-help">Kode bersifat pribadi dan hanya digunakan untuk mengaktifkan undangan ini.</p>
            {(!fields && (invitationId || invitationSecret)) && <p className={styles.help}>Lengkapi nomor undangan dan kode 64 karakter yang Anda terima.</p>}
          </div>}
          <div className={styles.action}><ProtectedNavigationForm action="/auth/login" fields={fields ?? { returnPath }} disabled={!fields} label={invited ? "Terima undangan dan masuk" : "Lanjutkan ke layanan akun"} /></div>
        </>}
        <div className={styles.support}><h3>Perlu bantuan masuk?</h3><p>Hubungi pengelola cabang atau organisasi untuk memeriksa akses dan undangan, atau ikuti proses pemulihan akun resmi. Jangan bagikan kata sandi atau kode verifikasi Anda.</p></div>
        <noscript><p className={styles.alert}>Aktifkan JavaScript untuk menyiapkan perlindungan formulir masuk.</p></noscript>
      </section>
      <p className={styles.accessNote}><ShieldCheck size={16} aria-hidden="true" />Akses mengikuti keanggotaan dan penugasan Anda.</p>
      <Link className={styles.backLink} href="/">Kembali ke beranda<ArrowUpRight size={16} aria-hidden="true" /></Link>
      </div>
    </main>
    <PilotSiteFooter />
  </div>;
}
