import type { ReactNode } from "react";
import { CircleAlert, CircleCheck, CircleHelp, Inbox, Lock, WifiOff } from "lucide-react";

export type StateKind = "loading" | "empty" | "error" | "denied" | "unknown" | "success" | "offline";

/**
 * Which one state a data region shows (ui-standard §9). An error always wins, so a failed read never also says
 * "Belum ada …"; loading only replaces content on the first load (a refresh keeps the rows visible).
 */
export function regionState({ loading = false, error = false, empty = false, denied = false }: { loading?: boolean; error?: boolean; empty?: boolean; denied?: boolean }): StateKind | "content" {
  if (denied) return "denied";
  if (error) return "error";
  if (empty) return loading ? "loading" : "empty";
  return "content";
}

const icons = { empty: Inbox, error: CircleAlert, denied: Lock, unknown: CircleHelp, success: CircleCheck, offline: WifiOff } as const;

/**
 * The one state panel: loading (skeleton rows, the title only for screen readers), empty (inside its section, h3),
 * error / unknown (full-width tone panel, role="alert"), denied (neutral card, h2), success and offline (role="status").
 * `action` is usually a secondary <Button> ("Coba lagi", "Periksa hasil"); `detail` carries a trace code.
 */
export function StateView({ kind, title, message, detail, action, secondaryAction, rows = 3 }: {
  kind: StateKind; title: string; message?: ReactNode; detail?: ReactNode; action?: ReactNode; secondaryAction?: ReactNode; rows?: number;
}) {
  if (kind === "loading") return <div className="state-view" data-kind="loading" role="status" aria-busy="true" data-kit="">
    <span className="list-table-sr">{title}</span>
    {Array.from({ length: rows }, (_, index) => <span key={index} className="outcome-skeleton" />)}
  </div>;
  const Icon = icons[kind];
  const role = kind === "error" || kind === "unknown" ? "alert" : kind === "success" || kind === "offline" ? "status" : undefined;
  const Title = kind === "denied" ? "h2" : kind === "empty" ? "h3" : "strong";
  return <div className="state-view" data-kind={kind} role={role} data-kit="">
    <div className="state-view-head"><Icon className="state-view-icon" aria-hidden="true" /><Title className="state-view-title">{title}</Title></div>
    {message && <p>{message}</p>}
    {detail && <p className="state-view-detail">{detail}</p>}
    {(action || secondaryAction) && <div className="ui-actions">{action}{secondaryAction}</div>}
  </div>;
}
