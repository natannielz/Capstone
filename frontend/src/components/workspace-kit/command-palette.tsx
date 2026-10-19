'use client';

import { useEffect, useId, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { Search } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import type { NavigationGroup } from "@/modules/identity/workspace-navigation";
import { isPaletteShortcut, paletteComboboxProps, paletteSearch } from "./palette-search";

/** Opens the single page-level palette; place one in the sidebar and one in the narrow-screen top bar. */
export function PaletteButton({ onOpen, locked }: { onOpen: () => void; locked: boolean }) {
  return <button type="button" className="workspace-palette-button" onClick={() => { if (!locked) onOpen(); }} aria-disabled={locked}>
    <Search aria-hidden="true" /><span>Cari ruang kerja</span><kbd>Ctrl K</kbd>
  </button>;
}

/** Quick switch between available workspaces (Ctrl/⌘+K). One instance per page; blocked while a command is in flight. */
export function CommandPalette({ groups, locked, open, onOpenChange }: { groups: NavigationGroup[]; locked: boolean; open: boolean; onOpenChange: (open: boolean) => void }) {
  const router = useRouter();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const listId = useId();
  const results = useMemo(() => paletteSearch(groups, query), [groups, query]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (!isPaletteShortcut({ key: event.key, ctrlKey: event.ctrlKey, metaKey: event.metaKey, targetTag: target?.tagName ?? "", editable: Boolean(target?.isContentEditable) })) return;
      event.preventDefault(); if (!locked) onOpenChange(true);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [locked, onOpenChange]);

  // Keep the highlighted result visible while moving with the arrow keys.
  useEffect(() => { if (open) document.getElementById(`${listId}-${active}`)?.scrollIntoView({ block: "nearest" }); }, [open, active, listId]);
  const close = (next: boolean) => { onOpenChange(next); if (!next) { setQuery(""); setActive(0); } };
  const go = (href: string) => { if (locked) return; close(false); router.push(href); };
  const onInputKey = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "ArrowDown") { event.preventDefault(); setActive(index => Math.min(index + 1, results.length - 1)); }
    else if (event.key === "ArrowUp") { event.preventDefault(); setActive(index => Math.max(index - 1, 0)); }
    else if (event.key === "Enter" && results[active]) { event.preventDefault(); go(results[active].href); }
  };

  return <Dialog open={open} onOpenChange={close}>
    <DialogContent className="workspace-palette">
      <DialogTitle>Cari ruang kerja</DialogTitle>
      <DialogDescription>Ketik nama tugas, lalu tekan Enter. Hanya ruang kerja yang tersedia untuk akun Anda.</DialogDescription>
      <input autoFocus type="search" {...paletteComboboxProps(listId, results.length, active)}
        aria-label="Cari ruang kerja" placeholder="Misalnya pembayaran, kiriman, pemasok" value={query} maxLength={120}
        onChange={event => { setQuery(event.target.value); setActive(0); }} onKeyDown={onInputKey} />
      {results.length === 0
        ? <p role="status" className="workspace-palette-empty">Tidak ada ruang kerja yang cocok.</p>
        : <ul id={listId} role="listbox" aria-label="Hasil pencarian ruang kerja">{results.map((result, index) =>
          <li key={result.href} id={`${listId}-${index}`} role="option" aria-selected={index === active}
            onMouseEnter={() => setActive(index)} onClick={() => go(result.href)}>
            <strong>{result.label}</strong><span>{result.area}</span>
          </li>)}</ul>}
    </DialogContent>
  </Dialog>;
}
