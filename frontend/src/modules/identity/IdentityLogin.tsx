"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { Brand } from "../../components/brand";
import { KemasanPoster } from "../../components/landing/KemasanPoster";
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

  // Reading order follows the task: brand, the form, then who the accounts are for. On wide screens the context
  // panel moves to the left column; it holds no controls, so focus order is unchanged.
  return <div className={styles.page}>
    <a className={styles.skip} href="#identity-content">Langsung ke formulir masuk</a>
    <header className={styles.top}>
      <Brand inverse context="Kebutuhan kerja & harian" />
      <Link className={styles.home} href="/">Beranda</Link>
    </header>
    <main className={styles.formSide} id="identity-content" tabIndex={-1}>
      <section className={styles.sheet} aria-labelledby="login-heading" aria-busy={loading}>
        <p className={styles.sheetBar}>Akun organisasi</p>
        <div className={styles.sheetBody}>
          <h1 id="login-heading">Masuk ke Unit Toko</h1>
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
            <ol className={styles.next} aria-label="Yang terjadi setelah Anda melanjutkan">
              <li>Anda diarahkan ke layanan akun resmi.</li>
              <li>Masuk dengan email organisasi Anda dan selesaikan verifikasi yang diminta.</li>
              <li>Anda kembali ke Unit Toko dan melanjutkan pekerjaan.</li>
            </ol>
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
          <noscript><p className={styles.alert}>Aktifkan JavaScript untuk menyiapkan perlindungan formulir masuk.</p></noscript>
        </div>
        <div className={styles.support}><h2>Perlu bantuan masuk?</h2><p>Hubungi pengelola cabang atau organisasi untuk memeriksa akses dan undangan, atau ikuti proses pemulihan akun resmi. Jangan bagikan kata sandi atau kode verifikasi Anda.</p></div>
      </section>
    </main>
    <aside className={styles.context} aria-labelledby="login-context">
      <p id="login-context" className={styles.statement}>Satu akun organisasi untuk katalog, permintaan cabang, dan tugas toko.</p>
      <dl className={styles.audiences}>
        <div><dt>PIC cabang BNI</dt><dd>Mengajukan kebutuhan cabang dan mengikuti peninjauannya.</dd></div>
        <div><dt>Akun usaha yang disetujui</dt><dd>Memesan dari katalog yang dibuka untuk organisasinya.</dd></div>
        <div><dt>Petugas toko</dt><dd>Meninjau permintaan dan mengelola katalog sesuai penugasan.</dd></div>
      </dl>
      <p className={styles.accessNote}>Akses mengikuti keanggotaan dan penugasan Anda.</p>
      <div className={styles.drawing} aria-hidden="true"><KemasanPoster /></div>
    </aside>
  </div>;
}
