'use client';

import Link from "next/link";
import { useState } from "react";
import { Menu } from "lucide-react";
import { CommandPalette, PaletteButton } from "@/components/workspace-kit/command-palette";
import { HelpLink } from "@/components/workspace-kit/shell-links";
import { Sheet, SheetContent, SheetDescription, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { NAVIGATION_HREFS, navigationClick, workspaceNavigation, type NavigationGroup, type NavigationId } from "../identity/workspace-navigation";

/**
 * Workspace navigation (presentation only): a grouped sidebar on wide screens and a "Menu" sheet on narrow ones.
 * Destinations come from the live permission discovery; every API still authorises independently.
 */
export function AdministrationNavigation({ current, actions, locked = false }: { current: NavigationId; actions: readonly string[]; locked?: boolean }) {
  const groups = workspaceNavigation(actions, NAVIGATION_HREFS[current]);
  const [open, setOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const currentLabel = groups.flatMap(group => group.items).find(item => item.current)?.label ?? "Ruang kerja";
  return <>
    <aside className="workspace-sidebar" aria-label="Navigasi ruang kerja">
      <Link href="/identity/account" className="workspace-sidebar-brand" aria-disabled={locked} onClick={navigationClick(locked)}>Unit Toko</Link>
      <PaletteButton locked={locked} onOpen={() => setPaletteOpen(true)} />
      <NavigationList groups={groups} locked={locked} />
      <HelpLink phone={process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP} />
    </aside>
    <div className="workspace-topbar">
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetTrigger asChild><button type="button" className="workspace-menu-button" aria-label="Buka menu ruang kerja"><Menu aria-hidden="true" /><span>Menu</span></button></SheetTrigger>
        <SheetContent side="left" className="workspace-sheet">
          <SheetTitle>Unit Toko</SheetTitle>
          <SheetDescription>Pilih ruang kerja sesuai penugasan akun Anda.</SheetDescription>
          <NavigationList groups={groups} locked={locked} onNavigate={() => setOpen(false)} />
          <HelpLink phone={process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP} />
        </SheetContent>
      </Sheet>
      <span className="workspace-topbar-current" aria-current="page">{currentLabel}</span>
      <PaletteButton locked={locked} onOpen={() => setPaletteOpen(true)} />
    </div>
    <CommandPalette groups={groups} locked={locked} open={paletteOpen} onOpenChange={setPaletteOpen} />
  </>;
}

function NavigationList({ groups, locked, onNavigate }: { groups: NavigationGroup[]; locked: boolean; onNavigate?: () => void }) {
  return <nav className="workspace-nav" aria-label="Ruang kerja">
    {groups.map(group => <section key={group.id} aria-labelledby={`workspace-nav-${group.id}`}>
      <h2 id={`workspace-nav-${group.id}`}>{group.label}</h2>
      <ul>{group.items.map(item => <li key={item.href}>
        <Link href={item.href} aria-current={item.current ? "page" : undefined} aria-disabled={locked || !item.available || undefined}
          title={item.available ? item.description : "Akses ke halaman ini sedang tidak tersedia."}
          onClick={event => { navigationClick(locked || !item.available)(event); if (!locked && item.available) onNavigate?.(); }}>
          {item.label}{!item.available && <span className="workspace-nav-note"> · tidak tersedia</span>}
        </Link>
      </li>)}</ul>
    </section>)}
  </nav>;
}
