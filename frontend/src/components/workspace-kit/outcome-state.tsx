import { outcomeCopy, type OutcomeKind } from "./outcome-copy";
import { StateView, type StateKind } from "./state-view";

const kinds: Record<OutcomeKind, StateKind> = {
  loading: "loading", "empty-first": "empty", "empty-filtered": "empty", denied: "denied", offline: "offline", conflict: "unknown", unknown: "unknown", error: "error",
};

/**
 * Workspace states with their fixed copy, rendered through the one StateView. "unknown" offers "Periksa hasil", which the
 * caller must wire to the existing recovery read with the ORIGINAL idempotency key (never a resend).
 */
export function OutcomeState({ kind, onAction, detail, rows = 3 }: { kind: OutcomeKind; onAction?: () => void; detail?: string; rows?: number }) {
  const copy = outcomeCopy(kind);
  return <StateView kind={kinds[kind]} title={copy.title} message={copy.body} detail={detail} rows={rows}
    action={copy.action && onAction ? <button type="button" className="outcome-action" onClick={onAction}>{copy.action}</button> : undefined} />;
}
