import { statusTone, type StatusTone } from "@/lib/status/dictionary";

/**
 * The one status badge (ui-standard §6.5): 24 px, radius 6, 12/600, a tone pair and a dot (so status is never colour only).
 * Pass `status` (a label from the status dictionary, which picks the tone) or an explicit `tone` + `label`.
 * A plain span: utility colour classes would sit in a later cascade layer and hide the tone.
 */
export function Badge(props: { status: string } | { tone: StatusTone; label: string }) {
  const [tone, label] = "status" in props ? [statusTone(props.status), props.status] : [props.tone, props.label];
  return <span data-tone={tone} className="status-badge">{label}</span>;
}
