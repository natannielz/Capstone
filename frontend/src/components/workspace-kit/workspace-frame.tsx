'use client';

import { usePathname } from "next/navigation";
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { Menu } from "lucide-react";
import { Brand } from "@/components/brand";
import { CommandPalette, PaletteButton } from "@/components/workspace-kit/command-palette";
import { HelpLink } from "@/components/workspace-kit/shell-links";
import { Sheet, SheetContent, SheetDescription, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { createAccessGateway } from "@/modules/administration/access-client";
import { NavigationList } from "@/modules/administration/NavigationList";
import { createIdentityGateway } from "@/modules/identity/identity-client";
import { currentNavigationHref, workspaceNavigation } from "@/modules/identity/workspace-navigation";

/** Inside the frame, a page's own navigation only reports whether a command is in flight (navigation is then blocked). */
type Frame = { setLocked(locked: boolean): void };
const FrameContext = createContext<Frame | null>(null);
export function useWorkspaceFrame() { return useContext(FrameContext); }

type Viewer = { state: "loading" } | { state: "signed-out" } | { state: "ready"; name: string; actions: readonly string[] };

/**
 * The one frame around every staff page: a grouped sidebar on wide screens, a top bar with a menu sheet on narrow ones,
 * the Ctrl+K workspace search and help in the same place everywhere. It is mounted by the workspace route layouts, so it is
 * there on every page and in every page state (loading, denied, error). Destinations come from the live permission
 * discovery; every API still authorises independently.
 */
export function WorkspaceFrame({ children }: { children: React.ReactNode }) {
  const pathname = usePathname() ?? "/";
  const [viewer, setViewer] = useState<Viewer>({ state: "loading" });
  const [locked, setLockedState] = useState(false), [open, setOpen] = useState(false), [paletteOpen, setPaletteOpen] = useState(false);
  useEffect(() => {
    let alive = true;
    void (async () => {
      try {
        const session = await createIdentityGateway().session();
        if (!session.authenticated || !session.account) { if (alive) setViewer({ state: "signed-out" }); return; }
        const scope = await createAccessGateway().current();
        if (alive) setViewer({ state: "ready", name: session.account.displayName ?? "Akun Anda", actions: scope.allowedActions });
      } catch { if (alive) setViewer({ state: "signed-out" }); }
    })();
    return () => { alive = false; };
  }, []);
  const setLocked = useCallback((value: boolean) => setLockedState(value), []);
  const frame = useMemo(() => ({ setLocked }), [setLocked]);
  const groups = workspaceNavigation(viewer.state === "ready" ? viewer.actions : [], currentNavigationHref(pathname));
  const currentLabel = groups.flatMap(group => group.items).find(item => item.current)?.label ?? "Ruang kerja";
  const who = viewer.state === "ready" ? viewer.name : viewer.state === "loading" ? "Memeriksa akun…" : "Belum masuk";
  return <>
    <aside className="workspace-sidebar" aria-label="Navigasi ruang kerja">
      <Brand compact href="/" context="Ruang kerja" className="workspace-sidebar-brand" />
      <PaletteButton locked={locked} onOpen={() => setPaletteOpen(true)} />
      <NavigationList groups={groups} locked={locked} label="Ruang kerja" />
      <div className="workspace-sidebar-foot">
        <p className="workspace-viewer"><span>Masuk sebagai</span>{who}</p>
        <HelpLink phone={process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP} />
      </div>
    </aside>
    <div className="workspace-topbar">
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetTrigger asChild><button type="button" className="workspace-menu-button" aria-label="Buka menu ruang kerja"><Menu aria-hidden="true" /><span>Menu</span></button></SheetTrigger>
        <SheetContent side="left" className="workspace-sheet">
          <SheetTitle>Unit Toko</SheetTitle>
          <SheetDescription>{who}. Pilih ruang kerja sesuai penugasan akun Anda.</SheetDescription>
          <NavigationList groups={groups} locked={locked} label="Menu ruang kerja" onNavigate={() => setOpen(false)} />
          <HelpLink phone={process.env.NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP} />
        </SheetContent>
      </Sheet>
      <span className="workspace-topbar-current">{currentLabel}</span>
      <PaletteButton locked={locked} onOpen={() => setPaletteOpen(true)} />
    </div>
    <CommandPalette groups={groups} locked={locked} open={paletteOpen} onOpenChange={setPaletteOpen} />
    {/* The skip link lands here: past the header and every navigation link, at the start of the workspace content.
        An empty marker: text here would sit outside every landmark (axe "region", U-020); the page's <main> follows it. */}
    <span id="konten-utama" tabIndex={-1} />
    <FrameContext.Provider value={frame}><div data-workspace-frame="">{children}</div></FrameContext.Provider>
  </>;
}
