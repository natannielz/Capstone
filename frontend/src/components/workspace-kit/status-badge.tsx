import { Badge } from "./badge";

/** Status label with its tone from the status dictionary (unknown labels stay neutral). Kept for existing callers; renders <Badge>. */
export function StatusBadge({ text }: { text: string }) {
  return <Badge status={text} />;
}
