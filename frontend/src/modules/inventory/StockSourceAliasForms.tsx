"use client";
import { useState } from "react";
import type { SourceDecision } from "./stock-source-workflow";
import type { CurrentAlias, SourceAlias, SourceAliasCommand } from "./stock-source-alias-workflow";
import common from "../administration/access.module.css";
import styles from "./stock-source-alias.module.css";
import { formatDateTime, numberFormatter } from "@/lib/format";

export const aliasStatus = { proposed: "Menunggu peninjauan", approved: "Disetujui", rejected: "Ditolak", withdrawn: "Ditarik" };
export const aliasAction = { "stock.source.alias.propose": "Kirim pemetaan", "stock.source.alias.approve": "Setujui pemetaan",
  "stock.source.alias.reject": "Tolak pemetaan", "stock.source.alias.withdraw": "Tarik pemetaan" };
export const bindingLabel = { absent: "Belum dipetakan", disabled: "Pemetaan dinonaktifkan", stale: "Perlu peninjauan ulang", current: "Sesuai pengaturan saat ini" };
export const aliasAmount = (value: string) => numberFormatter.format(BigInt(value));
const enabledLabel = (value: boolean) => value ? "Izinkan kanal memakai alokasi ini" : "Nonaktifkan pemetaan kanal";
export function AliasFacts({ proposal }: { proposal: SourceAlias }) {
  return <dl className={styles.facts}>
    <div><dt>Pengajuan</dt><dd>{proposal.id} · {aliasStatus[proposal.status]}</dd></div>
    <div><dt>Perubahan yang diajukan</dt><dd>{enabledLabel(proposal.enabled)}</dd></div>
    <div><dt>Alokasi fisik</dt><dd>{proposal.physicalKey} · {proposal.physicalPoolId}</dd></div>
    <div><dt>Versi pengaturan sumber yang ditinjau</dt><dd>{proposal.poolVersion}</dd></div>
    <div><dt>Dasar pemetaan</dt><dd>{proposal.baseAliasVersion === "0" ? "Belum ada pemetaan" : "Versi " + proposal.baseAliasVersion}</dd></div>
    {proposal.appliedAliasVersion && <div><dt>Versi pemetaan yang disimpan</dt><dd>{proposal.appliedAliasVersion}</dd></div>}
    <div><dt>Diajukan</dt><dd>{formatDateTime(proposal.proposedAt)}</dd></div>
    <div><dt>Alasan pengajuan</dt><dd className={common.preserve}>{proposal.reason}</dd></div>
  </dl>;
}
export function AliasDraftForm({ enabled, reason, disabled, onChange, onReview }: { enabled: boolean; reason: string; disabled: boolean;
  onChange: (enabled: boolean, reason: string) => void; onReview: () => void }) {
  return <form className={styles.form} onSubmit={event => { event.preventDefault(); onReview(); }}>
    <h3>Ajukan pemetaan</h3><p className={common.help}>Kedua kanal tetap memakai satu alokasi fisik. Pemetaan tidak membuat saldo baru atau memindahkan barang.</p>
    <label><span id="alias-enabled-label">Penggunaan alokasi oleh kanal tujuan</span><select aria-labelledby="alias-enabled-label" disabled={disabled}
      value={enabled ? "enabled" : "disabled"} onChange={event => onChange(event.target.value === "enabled", reason)}>
      <option value="enabled">Izinkan penggunaan alokasi</option><option value="disabled">Nonaktifkan pemetaan</option></select></label>
    <label><span id="alias-reason-label">Alasan pemetaan</span><textarea aria-labelledby="alias-reason-label" required disabled={disabled}
      maxLength={1000} rows={3} value={reason} onChange={event => onChange(enabled, event.target.value)} /></label>
    <button className={common.primary} disabled={disabled}>Tinjau pemetaan</button>
  </form>;
}
export function AliasDecisionForm({ proposal, disabled, fresh, approvable, canPropose, canApprove, onReview }: {
  proposal: SourceAlias; disabled: boolean; fresh: boolean; approvable: boolean; canPropose: boolean; canApprove: boolean;
  onReview: (decision: SourceDecision, reference: string, reason: string) => void;
}) {
  const [action, setAction] = useState<SourceDecision>(canApprove ? approvable ? "approve" : "reject" : "withdraw");
  const [reference, setReference] = useState(""), [reason, setReason] = useState("");
  if (proposal.status !== "proposed" || !canPropose && !canApprove) return null;
  const blocked = disabled || action !== "withdraw" && !fresh || action === "approve" && !approvable;
  return <form className={styles.form} onSubmit={event => { event.preventDefault(); onReview(action, reference, reason); }}>
    <h3>Catat keputusan</h3><p className={common.help}>Persetujuan dan penolakan memerlukan peninjau yang berbeda dari pengaju serta verifikasi dua langkah terbaru. Hanya pengaju semula dapat menarik pengajuan.</p>
    <label><span id="alias-decision-label">Tindakan</span><select aria-labelledby="alias-decision-label" disabled={disabled} value={action} onChange={event => setAction(event.target.value as SourceDecision)}>
      {canApprove && <><option value="approve" disabled={!approvable}>Setujui pemetaan</option><option value="reject">Tolak pemetaan</option></>}
      {canPropose && <option value="withdraw">Tarik pemetaan</option>}</select></label>
    <label><span id="alias-reference-label">Acuan peninjauan</span><input aria-labelledby="alias-reference-label" required disabled={disabled} maxLength={160} value={reference} onChange={event => setReference(event.target.value)} /></label>
    <label><span id="alias-decision-reason-label">Alasan keputusan</span><textarea aria-labelledby="alias-decision-reason-label" required disabled={disabled} rows={3} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <button className={common.primary} disabled={blocked}>Tinjau keputusan</button>
  </form>;
}
export function AliasReview({ command, current, ownerLabel, requestedLabel, busy, onSubmit, onCancel }: { command: SourceAliasCommand;
  current: CurrentAlias | null; ownerLabel: string; requestedLabel: string; busy: boolean; onSubmit: (confirmed: boolean) => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false), proposal = "proposal" in command ? command.proposal : null;
  const version = proposal ? proposal.poolVersion : command.expectedVersion;
  const base = proposal ? proposal.baseAliasVersion : "expectedAliasVersion" in command.body ? command.body.expectedAliasVersion : "";
  const enabled = proposal ? proposal.enabled : "enabled" in command.body && command.body.enabled;
  return <form className={styles.form} onSubmit={event => { event.preventDefault(); onSubmit(confirmed); }}>
    <p className={styles.eyebrow}>Pemeriksaan akhir</p><h2>{aliasAction[command.operation]}</h2>
    <div className={styles.comparison}>
      <section><h3>Alokasi yang sama</h3><p><strong>{ownerLabel}</strong></p><p>Alokasi {command.poolId}</p>
        <p>Pengaturan yang ditinjau: versi {version}</p><p>Dasar pemetaan: {base === "0" ? "belum ada" : "versi " + base}</p></section>
      <section><h3>Kanal tujuan</h3><p><strong>{requestedLabel}</strong></p><p>{enabledLabel(!!enabled)}</p>
        {current && <p>Saat ini: {bindingLabel[current.bindingState]}. Pengaturan sumber versi {current.source.poolVersion};
          pemetaan {current.mapping?.appliedAliasVersion ? "versi " + current.mapping.appliedAliasVersion : "belum ada"}.</p>}</section>
    </div>
    {proposal && <p>Pengajuan {proposal.id}. Alasan awal: <span className={common.preserve}>{proposal.reason}</span></p>}
    {"proposalId" in command && <p>Acuan peninjauan: <strong>{command.body.reviewReference}</strong></p>}
    <p className={common.preserve}>{command.body.reason}</p>
    <label className={common.checkbox}><input type="checkbox" checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />
      <span>Saya telah memeriksa alokasi, kedua kanal, versi, dan alasan. Pemetaan ini tidak mengubah jumlah barang.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={!confirmed || busy}>{aliasAction[command.operation]}</button>
      <button type="button" className={common.secondary} disabled={busy} onClick={onCancel}>Kembali memeriksa</button></div>
  </form>;
}
