"use client";
import { useState } from "react";
import { sourceAgeSeconds, type SourceDecision } from "./stock-source-workflow";
import type { CurrentSource, SourceRevision, SourceRevisionCommand } from "./stock-source-revision-workflow";
import type { RevisionDraft } from "./stock-source-revision-editor-state";
import common from "../administration/access.module.css";
import styles from "./stock-source-revision.module.css";

type Settings = CurrentSource["settings"];
export const revisionStatus = { proposed: "Menunggu peninjauan", approved: "Disetujui · belum aktif", rejected: "Ditolak", withdrawn: "Ditarik" };
export const revisionAction = { "stock.source.revision.propose": "Kirim perubahan", "stock.source.revision.approve": "Setujui perubahan",
  "stock.source.revision.reject": "Tolak perubahan", "stock.source.revision.withdraw": "Tarik perubahan" };
const mode: Record<string, string> = { webLedger: "Catatan stok web", externalLedger: "Catatan stok sistem lain" };
const control: Record<string, string> = { exclusiveWebAllocation: "Alokasi khusus web", sharedWebLedger: "Catatan bersama pada web", externalHold: "Pemesanan ditahan di sistem lain" };
export const sourceAmount = (value: string) => new Intl.NumberFormat("id-ID").format(BigInt(value));
const fields: { key: Exclude<keyof Settings, "location">; label: string; format?: (value: string) => string }[] = [
  { key: "physicalKey", label: "Kode alokasi fisik" }, { key: "label", label: "Nama alokasi" },
  { key: "mode", label: "Cara pencatatan", format: value => mode[value] }, { key: "control", label: "Pengendalian alokasi", format: value => control[value] },
  { key: "maximumAgeMilliseconds", label: "Batas usia data", format: value => sourceAgeSeconds(value) + " detik" },
  { key: "safetyBufferBaseUnits", label: "Cadangan pengaman", format: value => sourceAmount(value) + " satuan dasar" },
  { key: "externalNamespace", label: "Kode sumber sistem lain" }, { key: "reservationProtocolReference", label: "Acuan penahanan pesanan" },
  { key: "discrepancyOwnerReference", label: "Penanggung jawab selisih" }, { key: "handoffReference", label: "Acuan serah terima" },
];
export function RevisionFacts({ settings, base }: { settings: Settings; base?: Settings }) {
  return <dl className={styles.facts}>{fields.map(field => {
    const value = settings[field.key], changed = base && base[field.key] !== value;
    return <div key={field.key} className={changed ? styles.changed : undefined}><dt>{field.label}{changed && <span>Berubah</span>}</dt>
      <dd>{value === null ? "Tidak digunakan" : field.format ? field.format(value) : value}</dd></div>;
  })}</dl>;
}
export function RevisionComparison({ base, proposed, baseVersion, current }: { base: Settings; proposed: Settings; baseVersion: string; current?: CurrentSource }) {
  const changedBase = current && current.poolVersion !== baseVersion;
  return <div className={styles.comparison}>
    <section><h3>Dasar pengajuan · versi {baseVersion}</h3><RevisionFacts settings={base} /></section>
    <section><h3>Pengaturan yang diajukan</h3><RevisionFacts settings={proposed} base={base} /></section>
    {changedBase && <section><h3>Saat ini · versi {current.poolVersion}</h3><RevisionFacts settings={current.settings} /></section>}
  </div>;
}
export function RevisionDraftForm({ draft, disabled, onChange, onReview }: { draft: RevisionDraft; disabled: boolean;
  onChange: (field: keyof RevisionDraft, value: string) => void; onReview: () => void }) {
  function input(field: keyof RevisionDraft, label: string, maxLength = 160, inputMode?: "decimal" | "numeric") {
    return <label><span id={"revision-draft-" + field}>{label}</span><input aria-labelledby={"revision-draft-" + field} required disabled={disabled} value={draft[field]} maxLength={maxLength} inputMode={inputMode}
      onChange={event => onChange(field, event.target.value)} /></label>;
  }
  return <form className={styles.form} onSubmit={event => { event.preventDefault(); onReview(); }}>
    <p className={common.help}>Kode fisik serta kepemilikan gudang dan kanal tetap. Gunakan pengaturan yang telah disepakati penanggung jawab persediaan.</p>
    {input("label", "Nama alokasi")}
    <div className={styles.formPair}><label><span id="revision-mode-label">Cara pencatatan</span><select aria-labelledby="revision-mode-label" required disabled={disabled} value={draft.mode} onChange={event => onChange("mode", event.target.value)}>
      <option value="">Pilih cara pencatatan</option><option value="webLedger">Catatan stok web</option><option value="externalLedger">Catatan stok sistem lain</option></select></label>
      <label><span id="revision-control-label">Pengendalian alokasi</span><select aria-labelledby="revision-control-label" required disabled={disabled} value={draft.control} onChange={event => onChange("control", event.target.value)}>
        <option value="">Pilih pengendalian</option>{draft.mode === "webLedger" ? <><option value="exclusiveWebAllocation">Alokasi khusus web</option>
          <option value="sharedWebLedger">Catatan bersama pada web</option></> : draft.mode === "externalLedger" ? <option value="externalHold">Pemesanan ditahan di sistem lain</option> : null}</select></label></div>
    <div className={styles.formPair}>{input("maximumAgeSeconds", "Batas usia data (detik)", 23, "decimal")}
      {input("safetyBufferBaseUnits", "Cadangan pengaman (satuan dasar)", 19, "numeric")}</div>
    <p className={common.help}>Batas usia dapat memakai titik hingga tiga angka desimal, misalnya 60.001 detik. Cadangan memakai satuan dasar bulat mulai dari 0.</p>
    {draft.mode === "externalLedger" && <>{input("externalNamespace", "Kode sumber sistem lain", 64)}{input("reservationProtocolReference", "Acuan protokol penahanan pesanan")}</>}
    {input("discrepancyOwnerReference", "Penanggung jawab selisih")}{input("handoffReference", "Acuan serah terima")}
    <label><span id="revision-reason-label">Alasan perubahan</span><textarea aria-labelledby="revision-reason-label" required disabled={disabled} maxLength={1000} rows={3} value={draft.reason} onChange={event => onChange("reason", event.target.value)} /></label>
    <button className={common.primary} disabled={disabled}>Tinjau perubahan</button>
  </form>;
}
export function RevisionDecisionForm({ proposal, disabled, fresh, approvable, canPropose, canApprove, onReview }: {
  proposal: SourceRevision; disabled: boolean; fresh: boolean; approvable: boolean; canPropose: boolean; canApprove: boolean;
  onReview: (decision: SourceDecision, reference: string, reason: string) => void;
}) {
  const [action, setAction] = useState<SourceDecision>(canApprove ? approvable ? "approve" : "reject" : "withdraw");
  const [reference, setReference] = useState(""), [reason, setReason] = useState("");
  if (proposal.status !== "proposed" || !canPropose && !canApprove) return null;
  const blocked = disabled || action !== "withdraw" && !fresh || action === "approve" && !approvable;
  return <form className={styles.form} onSubmit={event => { event.preventDefault(); onReview(action, reference, reason); }}>
    <h3>Catat keputusan</h3><p className={common.help}>Persetujuan dan penolakan memerlukan peninjau yang berbeda dari pengaju serta verifikasi dua langkah terbaru. Hanya pengaju semula dapat menarik pengajuan.</p>
    <label><span id="revision-decision-label">Tindakan</span><select aria-labelledby="revision-decision-label" disabled={disabled} value={action} onChange={event => setAction(event.target.value as SourceDecision)}>
      {canApprove && <><option value="approve" disabled={!approvable}>Setujui perubahan</option><option value="reject">Tolak perubahan</option></>}
      {canPropose && <option value="withdraw">Tarik perubahan</option>}</select></label>
    <label><span id="revision-reference-label">Acuan peninjauan</span><input aria-labelledby="revision-reference-label" required disabled={disabled} maxLength={160} value={reference} onChange={event => setReference(event.target.value)} /></label>
    <label><span id="revision-decision-reason-label">Alasan keputusan</span><textarea aria-labelledby="revision-decision-reason-label" required disabled={disabled} rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <button className={common.primary} disabled={blocked}>Tinjau keputusan</button>
  </form>;
}
export function RevisionReview({ command, current, busy, onSubmit, onCancel }: { command: SourceRevisionCommand; current: CurrentSource | null; busy: boolean;
  onSubmit: (confirmed: boolean) => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false);
  const base = "current" in command ? command.current.settings : command.proposal.baseSettings;
  const proposed = "current" in command ? command.body : command.proposal.proposedSettings;
  const version = "current" in command ? command.current.poolVersion : command.proposal.basePoolVersion;
  return <form className={styles.form} onSubmit={event => { event.preventDefault(); onSubmit(confirmed); }}>
    <p className={styles.eyebrow}>PEMERIKSAAN AKHIR</p><h2>{revisionAction[command.operation]}</h2>
    <p>Alokasi {command.poolId} · dasar versi {version}. Periksa perubahan dan alasan sebelum mengirim.</p>
    <RevisionComparison base={base} proposed={proposed} baseVersion={version} current={current ?? undefined} />
    {"proposalId" in command && <p>Acuan peninjauan: <strong>{command.body.reviewReference}</strong></p>}
    <p className={common.preserve}>{command.body.reason}</p>
    <label className={common.checkbox}><input type="checkbox" checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />
      <span>Saya telah memeriksa alokasi, versi, perubahan, dan alasan. Persetujuan menyimpan pengaturan yang belum aktif.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={!confirmed || busy}>{revisionAction[command.operation]}</button>
      <button type="button" className={common.secondary} disabled={busy} onClick={onCancel}>Kembali memeriksa</button></div>
  </form>;
}
