"use client";

import { useEffect, type ReactNode } from "react";
import { X } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogTitle,
} from "@/components/ui/dialog";

type MobileNavigationProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  children: ReactNode;
  id?: string;
  /** Optional notification only; the owner decides whether navigation is allowed. */
  onNavigate?: () => void;
};

export function MobileNavigation({
  open,
  onOpenChange,
  children,
  id = "mobile-navigation",
  onNavigate,
}: MobileNavigationProps) {
  useEffect(() => {
    const desktop = window.matchMedia("(min-width: 761px)");
    const closeOnDesktop = () => {
      if (desktop.matches && open) onOpenChange(false);
    };
    closeOnDesktop();
    desktop.addEventListener("change", closeOnDesktop);
    return () => desktop.removeEventListener("change", closeOnDesktop);
  }, [open, onOpenChange]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        id={id}
        className="mobile-navigation-dialog"
        showCloseButton={false}
        aria-describedby={undefined}
      >
        <div className="mobile-navigation-header">
          <DialogTitle>Navigasi</DialogTitle>
          <DialogClose asChild>
            <Button variant="ghost" size="icon" aria-label="Tutup navigasi">
              <X aria-hidden="true" />
            </Button>
          </DialogClose>
        </div>
        <div
          className="sidebar mobile-navigation-sidebar"
          onClick={(event) => {
            if (
              !event.defaultPrevented &&
              event.target instanceof Element &&
              event.target.closest("nav a, nav button")
            ) onNavigate?.();
          }}
        >
          {children}
        </div>
      </DialogContent>
    </Dialog>
  );
}
