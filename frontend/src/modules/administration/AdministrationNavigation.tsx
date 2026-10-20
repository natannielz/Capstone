'use client';

import Link from "next/link";
import { useEffect, useState } from "react";
import { Menu } from "lucide-react";
import { CommandPalette, PaletteButton } from "@/components/workspace-kit/command-palette";
import { HelpLink } from "@/components/workspace-kit/shell-links";
import { useWorkspaceFrame } from "@/components/workspace-kit/workspace-frame";
import { Sheet, SheetContent, SheetDescription, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { NAVIGATION_HREFS, navigationClick, workspaceNavigation, type NavigationId } from "../identity/workspace-navigation";
import { NavigationList } from "./NavigationList";

/**
 * Workspace navigation (presentation only): a grouped sidebar on wide screens and a "Menu" sheet on narrow ones.
 * Destinations come from the live permission discovery; every API still authorises independently.
 */
export function AdministrationNavigation({ current, actions, locked = false }: { current: NavigationId; actions: readonly string[]; locked?: boolean }) {
  const groups = workspaceNavigation(actions, NAVIGATION_HREFS[current]);
  const [open, setOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  // Inside the shared workspace frame the sidebar already exists; this page only reports a command in flight.
  const frame = useWorkspaceFrame();
  useEffect(() => { frame?.setLocked(locked); return () => frame?.setLocked(false); }, [frame, locked]);
  if (frame) return null;
  const currentLabel = groups.flatMap(group => group.items).find(item => item.current)?.label ?? "Ruang kerja";
  return <>
    <aside className="workspace-sidebar" aria-label="Navigasi ruang kerja">
      <Link href="/identity/account" className="workspace-sidebar-brand" aria-disabled={locked} onClick={navigationClick(locked)}>Unit Toko</Link>
      <PaletteButton locked={locked} onOpen={() => setPaletteOpen(true)} />
      <NavigationList groups={groups} locked={locked} label="Ruang kerja" />
      <HelpLink phone={process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP} />
    </aside>
    <div className="workspace-topbar">
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetTrigger asChild><button type="button" className="workspace-menu-button" aria-label="Buka menu ruang kerja"><Menu aria-hidden="true" /><span>Menu</span></button></SheetTrigger>
        <SheetContent side="left" className="workspace-sheet">
          <SheetTitle>Unit Toko</SheetTitle>
          <SheetDescription>Pilih ruang kerja sesuai penugasan akun Anda.</SheetDescription>
          <NavigationList groups={groups} locked={locked} label="Menu ruang kerja" onNavigate={() => setOpen(false)} />
          <HelpLink phone={process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP} />
        </SheetContent>
      </Sheet>
      <span className="workspace-topbar-current">{currentLabel}</span>
      <PaletteButton locked={locked} onOpen={() => setPaletteOpen(true)} />
    </div>
    <CommandPalette groups={groups} locked={locked} open={paletteOpen} onOpenChange={setPaletteOpen} />
    {/* The skip link lands here: past the header and every navigation link, at the start of the workspace content. */}
    <span id="konten-utama" tabIndex={-1} className="list-table-sr">Konten utama</span>
  </>;
}
