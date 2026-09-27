"use client";

import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "./ui/button";

export function CatalogPagination({page, pages, start, end, total, onChange}: {
  page: number; pages: number; start: number; end: number; total: number;
  onChange: (page: number) => void;
}) {
  if (pages <= 1) return null;
  return <nav aria-label="Halaman produk" className="flex flex-wrap items-center justify-between gap-3 border-t py-6 mt-6">
    <p className="text-sm text-muted-foreground" aria-live="polite">{start}–{end} dari {total} produk · Halaman {page} dari {pages}</p>
    <div className="flex gap-2">
      <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onChange(page - 1)}><ChevronLeft aria-hidden="true" />Sebelumnya</Button>
      <Button variant="outline" size="sm" disabled={page >= pages} onClick={() => onChange(page + 1)}>Berikutnya<ChevronRight aria-hidden="true" /></Button>
    </div>
  </nav>;
}
