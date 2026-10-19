import { statusTone } from "@/lib/status/dictionary";

/**
 * Status label with its tone (colour never depends on the label text itself; unknown labels stay neutral).
 * A plain span: utility colour classes would sit in a later cascade layer and hide the tone.
 */
export function StatusBadge({ text }: { text: string }) {
  return <span data-tone={statusTone(text)} className="status-badge">{text}</span>;
}
