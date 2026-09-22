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

export function defaultDestination(role: Role): string {
  return role === "customer" ? "/shop" : "/workspace";
}

/** Customer and staff continuations are separate, same-origin allowlists. */
export function safeLoginDestination(raw: string | null, role: Role): string {
  const fallback = defaultDestination(role);
  if (!raw || raw.length > 2048 || !raw.startsWith("/") || raw.startsWith("//") || /[\\\u0000-\u001f]/.test(raw)) return fallback;
  try {
    const url = new URL(raw, "https://unit-toko.invalid");
    if (url.origin !== "https://unit-toko.invalid") return fallback;
    if (role === "customer") {
      const permitted = ["/", "/shop", "/cart", "/checkout", "/account", "/account/orders"].includes(url.pathname)
        || /^\/shop\/[a-zA-Z0-9_-]+$/.test(url.pathname)
        || /^\/account\/orders\/[a-zA-Z0-9_-]+$/.test(url.pathname);
      return permitted ? url.pathname + url.search : fallback;
    }
    if (url.pathname !== "/workspace") return fallback;
    const view = url.searchParams.get("view") || "dashboard";
    if (!canOpenPage(role, view)) return fallback;
    return url.pathname + url.search;
  } catch {
    return fallback;
  }
}
