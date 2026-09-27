"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { createIdentityGateway } from "./identity-client";
import { IdentityFailure, identityMessage, identityReturnPath, loginFields, type IdentitySession } from "./identity-workflow";
import { ProtectedNavigationForm } from "./ProtectedNavigationForm";
import styles from "./identity.module.css";

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

  return <main className={styles.page}>
    <a className={styles.skip} href="#identity-content">Langsung ke akun</a>
    <header className={styles.masthead}><span className={styles.brand}>Unit Toko</span><span className={styles.kicker}>Layanan organisasi</span></header>
    <div className={styles.loginGrid} id="identity-content">
      <section className={styles.introduction} aria-labelledby="login-heading">
        <span className={styles.eyebrow}>AKSES TERDAFTAR</span>
        <h1 id="login-heading">Satu akun untuk kebutuhan organisasi Anda.</h1>
        <p>Akses Unit Toko untuk cabang BNI dan mitra usaha yang telah disetujui.</p>
        <ol className={styles.steps}>
          <li><span>01</span><div><h2>Gunakan email terdaftar</h2><p>Masuk dengan alamat email yang telah diverifikasi.</p></div></li>
          <li><span>02</span><div><h2>Selesaikan verifikasi dua langkah</h2><p>Verifikasi tambahan membantu melindungi akun dan setiap tindakan penting.</p></div></li>
          <li><span>03</span><div><h2>Bekerja sesuai penugasan</h2><p>Halaman dan tindakan tersedia berdasarkan keanggotaan serta izin yang disetujui.</p></div></li>
        </ol>
      </section>
      <section className={styles.card} aria-labelledby="signin-heading" aria-busy={loading}>
        <span className={styles.cardAccent} aria-hidden="true" />
        <h2 id="signin-heading">Masuk ke Unit Toko</h2>
        {loggedOut && session && !session.authenticated && <p className={styles.success} role="status">Tidak ada sesi Unit Toko aktif di peramban ini.</p>}
        {loading && <p role="status">Memeriksa layanan akun…</p>}
        {error !== null && <div className={styles.alert} role="alert"><p>{identityMessage(error)}</p>
          {error instanceof IdentityFailure && error.traceId && <small>Referensi bantuan: {error.traceId}</small>}
          <button type="button" className={styles.secondary} onClick={() => { setLoading(true); setError(null); setAttempt(value => value + 1); }}>Periksa kembali</button>
        </div>}
        {!loading && session?.authenticated && <><p>Anda sudah masuk sebagai <strong>{session.account?.displayName ?? session.account?.verifiedEmail}</strong>.</p>
          <Link href="/identity/account" className={styles.primary}>Buka akun saya</Link>
          <p className={styles.help}>Untuk menggunakan akun lain, keluar terlebih dahulu melalui halaman akun.</p></>}
        {!loading && session && !session.authenticated && <>
          <p>Masuk melalui layanan akun resmi. Siapkan perangkat verifikasi Anda.</p>
          <label className={styles.checkbox}><input type="checkbox" checked={invited} onChange={event => {
            setInvited(event.target.checked); setInvitationId(""); setInvitationSecret("");
          }} />Saya menerima undangan akun baru</label>
          {invited && <div className={styles.invitationFields}>
            <p className={styles.help} id="invite-help">Masukkan nomor dan kode dari undangan yang disetujui pengelola. Setelah itu, masuk dengan email penerima undangan.</p>
            <label htmlFor="invitation-id">Nomor undangan<input id="invitation-id" value={invitationId} onChange={event => setInvitationId(event.target.value)} inputMode="numeric" maxLength={19} autoComplete="off" aria-describedby="invite-help" /></label>
            <label htmlFor="invitation-secret">Kode undangan<input id="invitation-secret" type="password" value={invitationSecret} onChange={event => setInvitationSecret(event.target.value)} maxLength={64} autoComplete="off" spellCheck={false} aria-describedby="invite-secret-help" /></label>
            <p className={styles.help} id="invite-secret-help">Kode bersifat pribadi dan hanya digunakan untuk mengaktifkan undangan ini.</p>
            {(!fields && (invitationId || invitationSecret)) && <p className={styles.help}>Lengkapi nomor undangan dan kode 64 karakter yang Anda terima.</p>}
          </div>}
          <ProtectedNavigationForm action="/auth/login" fields={fields ?? { returnPath }} disabled={!fields} label={invited ? "Terima undangan dan masuk" : "Lanjutkan ke layanan akun"} />
        </>}
        <div className={styles.support}><h3>Belum memiliki akses atau sulit masuk?</h3><p>Hubungi pengelola cabang atau organisasi Anda untuk memeriksa undangan dan keanggotaan. Untuk pemulihan akun, ikuti proses resmi dari pengelola; jangan membagikan kata sandi atau kode verifikasi.</p></div>
        <noscript><p className={styles.alert}>Aktifkan JavaScript untuk menyiapkan perlindungan formulir masuk.</p></noscript>
      </section>
    </div>
    <footer className={styles.footer}>Unit Toko · Akses khusus organisasi yang disetujui</footer>
  </main>;
}
