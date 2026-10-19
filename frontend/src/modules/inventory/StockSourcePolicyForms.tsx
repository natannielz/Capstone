"use client";
import { useState } from "react";
import type { components } from "../../api/generated/schema";
import type { PolicyDraft } from "./stock-source-policy-editor-state";
import type { SourcePolicyCommand } from "./stock-source-policy-workflow";
import { sourceAgeSeconds } from "./stock-source-workflow";
import { RevisionFacts } from "./StockSourceRevisionForms";
import common from "../administration/access.module.css";
import styles from "./stock-source-policy.module.css";
import { formatDateTime } from "@/lib/format";

type Wire = components["schemas"];
export const policyStatus = { proposed: "Menunggu peninjauan", approved: "Disetujui", rejected: "Ditolak", withdrawn: "Ditarik" };
export const policyActionLabel = { "stock.source.policy.propose": "Kirim pengajuan", "stock.source.policy.approve": "Setujui kebijakan",
  "stock.source.policy.reject": "Tolak pengajuan", "stock.source.policy.withdraw": "Tarik pengajuan saya" };
const time = (value: string) => formatDateTime(value);

export function PolicyConfigurationFacts({ configuration }: { configuration: Wire["StockSourcePolicyConfigurationDto"] }) {
  const owner = configuration.settings.location;
  return <section>
    <h3>{configuration.settings.label}</h3>
    <p className={common.help}>Pengaturan sumber saat pengajuan · belum aktif</p>
    <RevisionFacts settings={configuration.settings} />
    <h4>Kanal pemilik alokasi</h4>
    <p>Kanal {owner.channelId} · Organisasi {owner.organizationId} · Toko {owner.shopId} · Gudang {owner.warehouseId}</p>
    <h4>Pemetaan kanal yang tercatat</h4>
    {configuration.mappings.length ? <ul className={styles.locations}>{configuration.mappings.map(mapping => <li key={mapping.aliasId}>
      <strong>Kanal {mapping.location.channelId} · {mapping.enabled ? "Pemetaan diizinkan" : "Pemetaan dinonaktifkan"}</strong>
      <span>Organisasi {mapping.location.organizationId} · Toko {mapping.location.shopId} · Gudang {mapping.location.warehouseId}</span>
      <span>Pemetaan {mapping.aliasId} · versi {mapping.aliasVersion}</span>
    </li>)}</ul> : <p>Belum ada pemetaan kanal lain pada catatan ini.</p>}
    <p className={common.help}>Cadangan pengaman berlaku untuk alokasi fisik bersama. Pemetaan yang dinonaktifkan tetap perlu diperiksa kewajibannya.</p>
  </section>;
}
export function PolicyInputFacts({ input }: { input: Wire["StockSourcePolicyInputDto"] }) {
  return <dl className={styles.facts}>
    <div><dt>Pencatat stok utama</dt><dd>{input.writer === "unitToko" ? "Unit Toko" : "Sistem eksternal"}</dd></div>
    <div><dt>Akun organisasi di sistem eksternal</dt><dd>{input.externalTenant ?? "Tidak digunakan"}</dd></div>
    <div><dt>Interval pembaruan dari sistem eksternal</dt><dd>{input.feedIntervalMilliseconds === "0" ? "Tidak digunakan" : sourceAgeSeconds(input.feedIntervalMilliseconds) + " detik"}</dd></div>
    <div><dt>Acuan prosedur rekonsiliasi</dt><dd>{input.reconciliationProcedureReference}</dd></div>
    <div><dt>Acuan prosedur gangguan</dt><dd>{input.outageProcedureReference}</dd></div>
  </dl>;
}
export function PolicyProposalFacts({ proposal }: { proposal: Wire["StockSourcePolicyDetailDto"] }) {
  return <>
    <p>Pengajuan #{proposal.id} · {policyStatus[proposal.status]} · {time(proposal.proposedAt)}</p>
    <PolicyInputFacts input={proposal.input} />
    <h4>Alasan pengajuan</h4><p className={common.preserve}>{proposal.reason}</p>
    <PolicyConfigurationFacts configuration={proposal.configuration} />
    <details><summary>Referensi dan versi saat pengajuan</summary><dl className={styles.facts}>
      <div><dt>Alokasi fisik</dt><dd>{proposal.physicalPoolId}</dd></div>
      <div><dt>Versi pengaturan</dt><dd>{proposal.poolVersion}</dd></div>
      <div><dt>Versi penghentian</dt><dd>{proposal.suspensionVersion}</dd></div>
      <div><dt>Dasar versi kebijakan</dt><dd>{proposal.basePolicyVersion}</dd></div>
    </dl></details>
    {proposal.decision && <section><h4>Keputusan tercatat</h4><p>{time(proposal.decision.decidedAt)}</p>
      <p>Acuan peninjauan: <strong>{proposal.decision.reviewReference}</strong></p><p className={common.preserve}>{proposal.decision.reason}</p>
      {proposal.policyVersion && <p>Kebijakan {proposal.policyId} · versi {proposal.policyVersion}</p>}
    </section>}
  </>;
}
export function PolicyDraftForm({ preview, draft, disabled, onChange, onReview }: {
  preview: Wire["StockSourcePolicyPreviewDto"]; draft: PolicyDraft; disabled: boolean;
  onChange: (change: Partial<PolicyDraft>) => void; onReview: () => void;
}) {
  const external = preview.configuration.settings.mode === "externalLedger";
  return <form onSubmit={event => { event.preventDefault(); onReview(); }}>
    <fieldset className={common.fields} disabled={disabled}><legend>Ajukan kebijakan operasional</legend>
      <label><span id="policy-writer-label">Pencatat stok utama</span><select aria-labelledby="policy-writer-label" required value={draft.writer} onChange={event => onChange({
        writer: event.target.value, externalTenant: external ? draft.externalTenant ?? "" : null,
        feedIntervalMilliseconds: external ? draft.feedIntervalMilliseconds : "0",
      })}><option value="">Pilih pencatat sesuai pengaturan</option>
        <option value={external ? "externalSystem" : "unitToko"}>{external ? "Sistem eksternal" : "Unit Toko"}</option>
      </select></label>
      <p className={common.help}>Satu pencatat utama mengikuti pengaturan sumber yang sudah disetujui.</p>
      {external && <>
        <label>Akun organisasi di sistem eksternal<input required maxLength={128} value={draft.externalTenant ?? ""}
          onChange={event => onChange({ externalTenant: event.target.value })} /></label>
        <label>Interval pembaruan (milidetik)<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={15}
          value={draft.feedIntervalMilliseconds} onChange={event => onChange({ feedIntervalMilliseconds: event.target.value })} /></label>
        <p className={common.help}>Gunakan interval yang disepakati dengan penyedia. 1.000 milidetik sama dengan 1 detik.</p>
      </>}
      <label>Acuan prosedur rekonsiliasi<input required maxLength={160} value={draft.reconciliationProcedureReference}
        onChange={event => onChange({ reconciliationProcedureReference: event.target.value })} /></label>
      <label>Acuan prosedur gangguan<input required maxLength={160} value={draft.outageProcedureReference}
        onChange={event => onChange({ outageProcedureReference: event.target.value })} /></label>
      <label>Alasan pengajuan<textarea required rows={3} maxLength={1000} value={draft.reason}
        onChange={event => onChange({ reason: event.target.value })} /></label>
      <button className={common.primary}>Tinjau pengajuan</button>
    </fieldset>
  </form>;
}
export function PolicyFinalReview({ command, busy, onSubmit, onCancel }: {
  command: SourcePolicyCommand; busy: boolean; onSubmit: (confirmed: boolean) => void; onCancel: () => void;
}) {
  const [confirmed, setConfirmed] = useState(false);
  return <form onSubmit={event => { event.preventDefault(); onSubmit(confirmed); }} aria-labelledby="policy-final-heading">
    <p className={styles.eyebrow}>PEMERIKSAAN AKHIR</p><h2 id="policy-final-heading">{policyActionLabel[command.operation]}</h2>
    {"proposalId" in command ? <>
      {!command.review.bindingCurrent && <p className={common.alert}>Pengaturan dasar sudah berubah. Tindakan ini hanya menyelesaikan pengajuan lama.</p>}
      <PolicyProposalFacts proposal={command.review.proposal} />
      <h3>Acuan dan alasan keputusan</h3><p>{command.body.reviewReference}</p><p className={common.preserve}>{command.body.reason}</p>
    </> : <>
      <p>Alokasi {command.poolId} · versi pengaturan {command.preview.poolVersion} · dasar kebijakan {command.preview.policyVersion}</p>
      <PolicyInputFacts input={command.body.input} /><h3>Alasan pengajuan</h3><p className={common.preserve}>{command.body.reason}</p>
      <PolicyConfigurationFacts configuration={command.preview.configuration} />
    </>}
    <label className={common.checkbox}><input type="checkbox" required checked={confirmed} disabled={busy}
      onChange={event => setConfirmed(event.target.checked)} />
      <span>Saya telah memeriksa alokasi, kanal, pencatat stok, prosedur, dan alasan di atas. Persetujuan kebijakan belum mengaktifkan sumber.</span></label>
    <div className={common.actions}><button className={common.primary} disabled={busy || !confirmed}>{policyActionLabel[command.operation]}</button>
      <button type="button" className={common.secondary} disabled={busy} onClick={onCancel}>Kembali memeriksa</button></div>
  </form>;
}