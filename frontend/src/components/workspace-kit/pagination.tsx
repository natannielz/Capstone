import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "./button";

/**
 * Pager under a DataTable or DataList (ui-standard §6.2): "1–25 dari 120" and previous/next icon buttons, right-aligned.
 * Renders nothing when everything fits on one page (no previous and no next page), so an empty list never shows a pager.
 */
export function Pagination({ label, start, end, total, hasPrevious, hasNext, onPrevious, onNext, disabled = false, compact = false }: {
  label: string; start: number; end: number; total?: number; hasPrevious: boolean; hasNext: boolean;
  onPrevious: () => void; onNext: () => void; disabled?: boolean; compact?: boolean;
}) {
  if (!hasPrevious && !hasNext) return null;
  const size = compact ? "icon-compact" : "icon";
  return <nav aria-label={label} className="list-table-pagination" data-kit="">
    <span>{start}–{end}{total !== undefined ? ` dari ${total}` : ""}</span>
    <Button size={size} aria-label="Sebelumnya" disabled={disabled || !hasPrevious} onClick={onPrevious}><ChevronLeft aria-hidden="true" /></Button>
    <Button size={size} aria-label="Berikutnya" disabled={disabled || !hasNext} onClick={onNext}><ChevronRight aria-hidden="true" /></Button>
  </nav>;
}
