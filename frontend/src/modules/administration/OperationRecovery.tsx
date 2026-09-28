"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { createAccessGateway } from "./access-client";
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, type OperationReference, type OperationRecoveryAdapters } from "./operation-journal";
import { AccessFailure, accessMessage } from "./workflow";
import styles from "./access.module.css";
import { recoveryPage, operationLabel } from "./operation-recovery-navigation";

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

export function OperationRecovery({ entries, journal, disabled, onResolved, onFailure, recoveryAdapters, onBusyChange }: {
  entries: readonly OperationReference[]; journal: OperationJournal; disabled: boolean; onResolved: () => void; onFailure?: (failure: unknown) => void;
  recoveryAdapters?: OperationRecoveryAdapters; onBusyChange?: (busy: boolean) => void;
}) {
  const [gateway] = useState(() => createAccessGateway());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [closing, setClosing] = useState<OperationReference | null>(null);
  const [reason, setReason] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const alive = useRef(false), pending = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  async function check(entry: OperationReference, close = false) {
    if (disabled || pending.current || !alive.current) return;
    if (close && entry.operation.startsWith("person.review.") && !confirmed) return;
    const isCurrent = () => alive.current;
    pending.current = true; setBusy(true); onBusyChange?.(true); setError(null); setMessage("");
    try {
      const receipt = await reconcileOperationRecovery(entry, journal, {
        lookup: reference => gateway.receipt(reference.operation, reference.key, reference.actorId),
        close: (reference, closeReason) => gateway.resolve(reference.operation, reference.key, closeReason, reference.actorId),
      }, recoveryAdapters, close ? reason : undefined, isCurrent);
      if (!isCurrent()) return;
      if (receipt.state === "committed" || receipt.state === "closed") {
        setClosing(null); setReason(""); setConfirmed(false);
        setMessage(receipt.state === "committed"
          ? `Permintaan telah tersimpan${receipt.result ? `; referensi hasil ${receipt.result.id}, versi ${receipt.result.version}` : ""}. Periksa data terbaru sebelum mengajukan perubahan lain.`
          : "Permintaan telah ditutup tanpa menerapkan perubahan. Anda dapat memeriksa data terbaru dan membuat pengajuan baru.");
        onResolved();
      } else {
        setMessage(operationRecoveryCapabilities(entry.operation, recoveryAdapters).canClose
          ? "Hasil belum terkonfirmasi. Ini belum berarti permintaan gagal. Periksa kembali, atau selesaikan dan tutup permintaan ini sebelum membuat perubahan baru."
          : `Hasil belum terkonfirmasi. Ini belum berarti permintaan gagal. Periksa kembali dari ${recoveryPage(entry.operation).label}. Referensi tetap disimpan dan perubahan baru masih ditahan.`);
      }
    } catch (failure) { if (isCurrent()) { setError(failure); onFailure?.(failure); } }
    finally { pending.current = false; if (isCurrent()) { setBusy(false); onBusyChange?.(false); } }
  }

  if (entries.length === 0 && !message && !error) return null;
  return <section className={styles.accountSection} aria-labelledby="recovery-heading">
    <h2 id="recovery-heading">Pemulihan permintaan</h2>
    {message && <p role="status" className={styles.help}>{message}</p>}
    {error !== null && <p role="alert" className={styles.alert}>{recoveryMessage(error)}</p>}
    {entries.length > 0 && <><p>Perangkat ini menyimpan {entries.length} referensi permintaan akun Anda yang perlu diperiksa. Perubahan baru ditahan sampai hasilnya jelas.</p>
      <ul className={styles.recoveryList}>{entries.map(entry => {
        const capabilities = operationRecoveryCapabilities(entry.operation, recoveryAdapters);
        return <li key={`${entry.operation}:${entry.key}`}>
        <div><strong>{operationLabel(entry.operation)}</strong><p>{new Date(entry.createdAt).toLocaleString("id-ID")}</p>
          <details><summary>Referensi bantuan</summary><code>{entry.key}</code></details></div>
        <div className={styles.actions}><button className={styles.secondary} disabled={disabled || busy || !capabilities.canCheck} onClick={() => void check(entry)}>Periksa hasil</button>
          {capabilities.canClose && <button className={styles.secondary} disabled={disabled || busy} onClick={() => { setClosing(entry); setReason(""); setConfirmed(false); setError(null); }}>Selesaikan dan tutup</button>}</div>
        {capabilities.unavailable === "adapter" && <p className={styles.help}>Pemeriksaan hasil tersedia di <a href={recoveryPage(entry.operation).href}>{recoveryPage(entry.operation).label}</a>. Jika layanan belum tersedia, referensi tetap disimpan dan perubahan baru masih ditahan.</p>}
        {capabilities.unavailable === "operation" && <p className={styles.help}>Jenis permintaan ini belum didukung untuk pemulihan. Simpan referensi bantuan dan hubungi administrator; perubahan baru masih ditahan.</p>}
      </li>; })}</ul></>}
    {closing && operationRecoveryCapabilities(closing.operation, recoveryAdapters).canClose && <form className={styles.review} onSubmit={event => { event.preventDefault(); if (reason.trim() && (!closing.operation.startsWith("person.review.") || confirmed)) void check(closing, true); }}>
      <h3>Tutup permintaan yang belum selesai</h3>
      <p>Jika perubahan sudah tersimpan, hasilnya akan ditampilkan. Jika belum tersimpan, server menutup referensi ini agar permintaan lama tidak dapat menerapkan perubahan belakangan. Tindakan ini tidak membatalkan perubahan yang sudah tersimpan.</p>
      <label><span id="recovery-reason-label">Alasan penyelesaian</span><textarea aria-labelledby="recovery-reason-label" required rows={3} maxLength={1000} disabled={disabled || busy} value={reason} onChange={event => setReason(event.target.value)} /></label>
      {closing.operation.startsWith("person.review.") && <label className={styles.checkbox}><input type="checkbox" required disabled={disabled || busy} checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /><span>Saya memahami bahwa penutupan ini tidak membatalkan perubahan yang sudah tersimpan dan mencegah permintaan lama yang belum tersimpan berjalan kemudian.</span></label>}
      <div className={styles.actions}><button className={styles.primary} disabled={disabled || busy || !reason.trim() || closing.operation.startsWith("person.review.") && !confirmed}>Pastikan hasil dan tutup</button>
        <button type="button" className={styles.secondary} disabled={disabled || busy} onClick={() => setClosing(null)}>Kembali</button></div>
    </form>}
  </section>;
}

function recoveryMessage(error: unknown): string {
  if (error instanceof AccessFailure) {
    if (error.code === "recovery_adapter_unavailable") return "Pemeriksaan hasil belum tersedia di halaman ini. Buka halaman asal permintaan; referensi tetap disimpan dan perubahan baru masih ditahan.";
    if (error.code === "recovery_operation_unsupported") return "Jenis permintaan ini belum didukung untuk pemulihan. Simpan referensi bantuan dan hubungi administrator; perubahan baru masih ditahan.";
    if (error.code === "recovery_close_unavailable") return "Penutupan memerlukan izin tindakan semula dan verifikasi dua langkah terbaru. Hasil tetap dapat diperiksa; referensi tetap disimpan.";
    if (error.code === "recovery_reason_invalid") return "Isi alasan penyelesaian hingga 1.000 karakter.";
    if (error.code === "recovery_request_stale") return "Sesi atau halaman berubah sebelum hasil dapat diperiksa. Referensi tetap disimpan untuk akun semula.";
    if (error.code === "recovery_close_unsupported") return "Permintaan ini belum dapat ditutup melalui pemulihan. Periksa hasilnya kembali; referensi tetap disimpan.";
    if (error.code === "recovery_reference_invalid" || error.code === "recovery_receipt_invalid") return "Hasil belum dapat dipastikan karena referensi atau respons tidak sesuai. Simpan referensi bantuan dan hubungi administrator; perubahan baru masih ditahan.";
  }
  return accessMessage(error);
}
