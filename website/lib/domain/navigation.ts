import type { Role } from "./accounts";

export const PAGE_ROLES = {
  dashboard: ["pic", "kepala", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin"],
  catalog: ["pic", "kepala"],
  orders: ["pic", "kepala", "staf", "laporan"],
  deliveries: ["pic", "kepala", "staf", "kurir"],
  stock: ["kepala", "staf", "laporan"],
  billing: ["pic", "kepala", "penagihan", "pimpinan", "akuntansi", "laporan"],
  payments: ["pic", "penagihan", "pimpinan", "akuntansi"],
  procurement: ["kepala", "staf", "penagihan", "pimpinan", "akuntansi"],
  reports: ["kepala", "laporan", "penagihan", "pimpinan", "akuntansi"],
  periods: ["laporan", "penagihan", "pimpinan", "akuntansi"],
  profile: ["pic", "kepala", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin"],
  admin: ["admin"],
} satisfies Record<string, Role[]>;

export type WorkspacePage = keyof typeof PAGE_ROLES;

export function canOpenPage(role: Role, page: string): page is WorkspacePage {
  return Object.hasOwn(PAGE_ROLES, page) && (PAGE_ROLES[page as WorkspacePage] as Role[]).includes(role);
}

/** A login continuation may only point into this portal and a page available to the actor. */
export function safeLoginDestination(raw: string | null, role: Role): string {
  if (!raw || raw.length > 2048 || !raw.startsWith("/workspace")) return "/workspace";
  try {
    const url = new URL(raw, "https://unit-toko.invalid");
    if (url.origin !== "https://unit-toko.invalid" || url.pathname !== "/workspace") return "/workspace";
    const view = url.searchParams.get("view") || "dashboard";
    if (!canOpenPage(role, view)) return "/workspace";
    return url.pathname + url.search;
  } catch {
    return "/workspace";
  }
}
