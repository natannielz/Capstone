import { outcomeCopy, type OutcomeKind } from "./outcome-copy";

/**
 * One rendering for workspace states (legacy API, markup and roles unchanged: rehearsals wait on these roles and texts).
 * "loading" shows skeleton rows; "unknown" offers "Periksa hasil", which the caller must wire to the existing recovery read
 * with the ORIGINAL idempotency key (never a resend). The look is shared with StateView (foundation.css); new pages use StateView.
 */
export function OutcomeState({ kind, onAction, detail, rows = 3 }: { kind: OutcomeKind; onAction?: () => void; detail?: string; rows?: number }) {
  const copy = outcomeCopy(kind);
  if (kind === "loading") return <div className="outcome-state outcome-loading" role="status" aria-busy="true" aria-label={copy.title}>
    {Array.from({ length: rows }, (_, index) => <span key={index} className="outcome-skeleton" />)}
  </div>;
  const alert = kind === "unknown" || kind === "conflict" || kind === "error";
  return <div className="outcome-state" data-kind={kind} role={alert ? "alert" : "status"}>
    <strong>{copy.title}</strong>
    <p>{copy.body}</p>
    {detail && <p className="outcome-detail">{detail}</p>}
    {copy.action && onAction && <button type="button" className="outcome-action" onClick={onAction}>{copy.action}</button>}
  </div>;
}
