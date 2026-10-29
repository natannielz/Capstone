"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { describeScope } from "../organizations/access-scope";
import { permissionLabel } from "../administration/permission-labels";
import { jakartaTime, type IdentityDecision } from "./IdentityAdministrationPanels";
import styles from "../administration/access.module.css";
import { Button } from '@/components/workspace-kit/button';

export function IdentityDecisionReview({ decision, locked, onCancel, onConfirm }: {
  decision: IdentityDecision; locked: boolean; onCancel: () => void;
  onConfirm: (reason: string, kind: "password" | "factor") => Promise<void>;
}) {
  const [reason, setReason] = useState("");
  const [kind, setKind] = useState<"password" | "factor">("password");
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { heading.current?.focus(); heading.current?.scrollIntoView({ block: "nearest", behavior: "instant" }); }, []);
  async function submit(event: FormEvent) { event.preventDefault(); if (!locked && reason.trim()) await onConfirm(reason.trim(), kind); }
  const invitation = decision.kind === "invitation" ? decision.value : null;
  return <section className="ui-section" aria-labelledby="identity-review-title">
    <h2 id="identity-review-title" ref={heading} tabIndex={-1}>{invitation ? "Periksa keputusan undangan" : "Periksa pemulihan akun"}</h2>
    {invitation ? <><p className={styles.preserve}><strong>{invitation.intendedEmail}</strong></p>
      <dl className={styles.facts}><div><dt>Lingkup</dt><dd>{describeScope(invitation.scope)}</dd></div><div><dt>Versi yang diperiksa</dt><dd>{invitation.version}</dd></div>
        <div><dt>Pengaju</dt><dd>Akun {invitation.requestedBy}</dd></div><div><dt>Undangan berlaku sampai</dt><dd>{jakartaTime(invitation.expiresAt)}</dd></div>
        <div><dt>Akses berlaku sampai</dt><dd>{invitation.accessExpiresAt ? jakartaTime(invitation.accessExpiresAt) : "Tanpa batas akhir yang ditetapkan"}</dd></div>
        <div><dt>Peran</dt><dd>{invitation.role ? `${invitation.role.templateId} · versi ${invitation.role.templateVersion}` : "Keanggotaan saja"}</dd></div></dl>
      <p className={styles.preserve}>Alasan pengajuan: {invitation.reason}</p>
      <h3>Izin yang diajukan</h3><ul>{invitation.role?.permissions.map(permission => <li key={permission}>{permissionLabel(permission)}</li>)}</ul>
      <h3>Izin yang boleh didelegasikan</h3>{invitation.role?.delegablePermissions.length ? <ul>{invitation.role.delegablePermissions.map(permission => <li key={permission}>{permissionLabel(permission)}</li>)}</ul> : <p>Tidak ada.</p>}
      <p className={styles.help}>Keputusan hanya berlaku untuk versi ini. Persetujuan belum berarti undangan telah dikirim atau akun sudah aktif.</p>
    </> : decision.kind === "recovery" && <><p><strong>{decision.value.displayName ?? "Nama belum ditetapkan"}</strong></p><p className={styles.preserve}>{decision.value.verifiedEmail}</p>
      <p className={styles.alert}>Semua sesi aplikasi akun ini akan dicabut. Permintaan pemulihan akan tertahan sampai proses penyedia tersedia. Kata sandi atau verifikasi dua langkah belum diganti oleh tindakan ini.</p>
      <p className={styles.help}>Versi keamanan yang diperiksa: {decision.value.securityVersion}. Pengelola tidak dapat memilih kata sandi atau faktor baru.</p></>}
    <form onSubmit={submit} className="ui-form"><fieldset disabled={locked}>
      {decision.kind === "recovery" && <label>Jenis pemulihan<select value={kind} onChange={event => setKind(event.target.value as "password" | "factor")}><option value="password">Kata sandi</option><option value="factor">Verifikasi dua langkah</option></select></label>}
      <label>Alasan keputusan<textarea aria-label="Alasan keputusan" required maxLength={1000} rows={3} value={reason} onChange={event => setReason(event.target.value)} /></label>
      <div className="ui-actions"><Button variant="primary" type="submit">{decision.kind === "recovery" ? "Cabut sesi dan catat pemulihan" : ({ approve: "Konfirmasi persetujuan", reject: "Konfirmasi penolakan", withdraw: "Konfirmasi penarikan", revoke: "Konfirmasi pencabutan" })[decision.action]}</Button>
        <Button variant="secondary" type="button" onClick={onCancel}>Batal</Button></div>
    </fieldset></form>
  </section>;
}