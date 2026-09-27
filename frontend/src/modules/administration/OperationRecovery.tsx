"use client";

import { useCallback, useEffect, useState } from "react";
import { createAccessGateway } from "./access-client";
import { OperationJournal, type OperationReference } from "./operation-journal";
import { AccessFailure, accessMessage } from "./workflow";
import styles from "./access.module.css";

export function useOperationRecovery(actorId?: string) {
  const [journal] = useState(() => new OperationJournal(() => window.localStorage));
  const [snapshot, setSnapshot] = useState<{ actorId?: string; entries: OperationReference[]; error: unknown }>({ entries: [], error: null });
  const refresh = useCallback(() => {
    if (!actorId) { setSnapshot({ entries: [], error: null }); return; }
    try { setSnapshot({ actorId, entries: journal.list(actorId), error: null }); }
    catch (error) { setSnapshot({ actorId, entries: [], error }); }
  }, [actorId, journal]);
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => { if (active) refresh(); });
    const changed = (event: StorageEvent) => { if (event.key === null || event.key.startsWith("unit-toko:pending:v1:")) refresh(); };
    window.addEventListener("storage", changed);
    return () => { active = false; window.removeEventListener("storage", changed); };
  }, [refresh]);
  const ready = snapshot.actorId === actorId;
  return { journal, entries: ready ? snapshot.entries : [], error: ready ? snapshot.error : null, refresh,
    locked: !ready || snapshot.entries.length > 0 || snapshot.error !== null };
}

export function OperationRecovery({ entries, journal, disabled, onResolved }: {
  entries: readonly OperationReference[]; journal: OperationJournal; disabled: boolean; onResolved: () => void;
}) {
  const [gateway] = useState(() => createAccessGateway());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [closing, setClosing] = useState<OperationReference | null>(null);
  const [reason, setReason] = useState("");

  async function check(entry: OperationReference, close = false) {
    setBusy(true); setError(null); setMessage("");
    try {
      const receipt = close ? await gateway.resolve(entry.operation, entry.key, reason.trim(), entry.actorId) : await gateway.receipt(entry.operation, entry.key, entry.actorId);
      if (receipt.state === "committed" || receipt.state === "closed") {
        setClosing(null); setReason("");
        setMessage(receipt.state === "committed"
          ? `Permintaan telah tersimpan${receipt.result ? `; referensi hasil ${receipt.result.id}, versi ${receipt.result.version}` : ""}. Periksa data terbaru sebelum mengajukan perubahan lain.`
          : "Permintaan telah ditutup tanpa menerapkan perubahan. Anda dapat memeriksa data terbaru dan membuat pengajuan baru.");
        try { journal.confirmed(entry); }
        catch { setError(new AccessFailure(400, "recovery_cleanup_failed")); return; }
        onResolved();
      } else {
        setMessage("Hasil belum terkonfirmasi. Ini belum berarti permintaan gagal. Periksa kembali, atau selesaikan dan tutup permintaan ini sebelum membuat perubahan baru.");
      }
    } catch (failure) { setError(failure); }
    finally { setBusy(false); }
  }

  if (entries.length === 0 && !message && !error) return null;
  return <section className={styles.accountSection} aria-labelledby="recovery-heading">
    <h2 id="recovery-heading">Pemulihan permintaan</h2>
    {message && <p role="status" className={styles.help}>{message}</p>}
    {error !== null && <p role="alert" className={styles.alert}>{accessMessage(error)}</p>}
    {entries.length > 0 && <><p>Perangkat ini menyimpan {entries.length} referensi permintaan akun Anda yang perlu diperiksa. Perubahan baru ditahan sampai hasilnya jelas.</p>
      <ul className={styles.recoveryList}>{entries.map(entry => <li key={`${entry.operation}:${entry.key}`}>
        <div><strong>{operationLabel(entry.operation)}</strong><p>{new Date(entry.createdAt).toLocaleString("id-ID")}</p>
          <details><summary>Referensi bantuan</summary><code>{entry.key}</code></details></div>
        <div className={styles.actions}><button className={styles.secondary} disabled={disabled || busy} onClick={() => void check(entry)}>Periksa hasil</button>
          <button className={styles.secondary} disabled={disabled || busy} onClick={() => { setClosing(entry); setReason(""); setError(null); }}>Selesaikan dan tutup</button></div>
      </li>)}</ul></>}
    {closing && <form className={styles.review} onSubmit={event => { event.preventDefault(); if (reason.trim()) void check(closing, true); }}>
      <h3>Tutup permintaan yang belum selesai</h3>
      <p>Jika perubahan sudah tersimpan, hasilnya akan ditampilkan. Jika belum tersimpan, server menutup referensi ini agar permintaan lama tidak dapat menerapkan perubahan belakangan. Tindakan ini tidak membatalkan perubahan yang sudah tersimpan.</p>
      <label><span id="recovery-reason-label">Alasan penyelesaian</span><textarea aria-labelledby="recovery-reason-label" required rows={3} maxLength={1000} disabled={disabled || busy} value={reason} onChange={event => setReason(event.target.value)} /></label>
      <div className={styles.actions}><button className={styles.primary} disabled={disabled || busy || !reason.trim()}>Pastikan hasil dan tutup</button>
        <button type="button" className={styles.secondary} disabled={disabled || busy} onClick={() => setClosing(null)}>Kembali</button></div>
    </form>}
  </section>;
}

function operationLabel(operation: string) {
  if (operation.startsWith("organization.")) return "Perubahan struktur organisasi";
  if (operation.includes("membership")) return "Perubahan keanggotaan";
  if (operation.includes("scope")) return "Perubahan status unit";
  if (operation.includes("identity")) return "Perubahan status akun";
  return "Perubahan akses";
}
