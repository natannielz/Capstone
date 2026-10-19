import { Badge } from "@/components/ui/badge";
import { statusTone } from "@/lib/status/dictionary";

/** Status label with its tone (colour never depends on the label text itself; unknown labels stay neutral). */
export function StatusBadge({ text }: { text: string }) {
  return <Badge variant="secondary" data-tone={statusTone(text)} className="status-badge">{text}</Badge>;
}
