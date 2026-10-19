import type { ReactNode } from "react";

/**
 * Three distinct empty states: nothing yet (first use, with the next step), nothing matches (change the search),
 * and nothing left (work finished — announced politely so it is not mistaken for a loading failure).
 */
export function EmptyState({ kind, title, body, action }: { kind: "first-use" | "no-results" | "done"; title: string; body: string; action?: ReactNode }) {
  return <div className="empty-state" data-kind={kind} role={kind === "done" ? "status" : undefined}>
    <h3>{title}</h3><p>{body}</p>{action && <div className="empty-state-action">{action}</div>}
  </div>;
}
