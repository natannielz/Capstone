'use client';

import { whatsappLink } from "@/lib/whatsapp";

/**
 * Moves focus past the header and navigation: to the workspace content marker (#konten-utama, rendered after the workspace
 * navigation), or to the page's <main> where there is no workspace navigation (landing, login). Focusable without joining the tab order.
 */
export function focusMain(root: Pick<Document, "querySelector" | "getElementById">): boolean {
  const main = root.getElementById("konten-utama") ?? root.querySelector("main");
  if (!main) return false;
  if (!main.hasAttribute("tabindex")) main.setAttribute("tabindex", "-1");
  main.focus(); main.scrollIntoView();
  return true;
}

/** First focusable element on every page (WCAG 2.4.1). */
export function SkipLink() {
  return <a href="#konten-utama" className="skip-link" onClick={event => { if (focusMain(document)) event.preventDefault(); }}>Lewati ke konten utama</a>;
}

/** Help in the same place on every workspace page (WCAG 3.2.6); absent when no help number is configured. */
export function HelpLink({ phone }: { phone: string | undefined }) {
  const href = whatsappLink(phone, "Halo, saya perlu bantuan menggunakan Unit Toko.");
  return href ? <a className="workspace-help" href={href} target="_blank" rel="noopener noreferrer">Bantuan via WhatsApp</a> : null;
}
