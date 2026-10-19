export type ReviewItem = { label: string; value: string; changeHref?: string };

/**
 * The review is the confirmation: what will be decided is listed, the button names the action, and nothing else asks
 * "Are you sure?". Self-approval and stale reviews are explained and block confirmation (the API still enforces both).
 */
export function ReviewPanel({ title, items, note, actionLabel, onConfirm, onCancel, busy = false, selfApprovalBlocked = false, stale = false, tone = "default" }: {
  title: string; items: ReviewItem[]; note?: string; actionLabel: string; onConfirm: () => void; onCancel: () => void;
  busy?: boolean; selfApprovalBlocked?: boolean; stale?: boolean; tone?: "default" | "danger";
}) {
  const blocked = busy || selfApprovalBlocked || stale;
  return <section className="review-panel" aria-label={title} data-tone={tone}>
    <h3>{title}</h3>
    <dl>{items.map(item => <div key={item.label}><dt>{item.label}</dt><dd>{item.value}{item.changeHref && <> <a href={item.changeHref}>Ubah<span className="list-table-sr"> {item.label}</span></a></>}</dd></div>)}</dl>
    {note && <p className="review-note">{note}</p>}
    {selfApprovalBlocked && <p role="status" className="review-block">Anda tidak dapat menyetujui pengajuan Anda sendiri. Pengajuan ini perlu diputuskan oleh petugas lain.</p>}
    {stale && <p role="alert" className="review-block">Data berubah sejak Anda membuka tinjauan ini. Muat ulang sebelum memutuskan.</p>}
    <div className="review-actions">
      <button type="button" className="review-confirm" data-tone={tone} disabled={blocked} aria-busy={busy || undefined} onClick={onConfirm}>{actionLabel}</button>
      <button type="button" className="review-cancel" disabled={busy} onClick={onCancel}>Batal</button>
    </div>
  </section>;
}
