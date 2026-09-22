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

export type LoginPortal = "customer" | "staff";

function localDestination(raw: string | null): URL | null {
  if (!raw || raw.length > 2048 || !raw.startsWith("/") || raw.startsWith("//") || /[\\\u0000-\u001f]/.test(raw)) return null;
  try {
    const url = new URL(raw, "https://unit-toko.invalid");
    return url.origin === "https://unit-toko.invalid" ? url : null;
  } catch { return null; }
}

function isCustomerPath(pathname: string): boolean {
  return ["/", "/shop", "/cart", "/checkout", "/account", "/account/orders"].includes(pathname)
    || /^\/shop\/[a-zA-Z0-9_-]+$/.test(pathname)
    || /^\/account\/orders\/[a-zA-Z0-9_-]+$/.test(pathname);
}

/** A legacy link chooses a portal only from an allowed local destination. */
export function loginForDestination(raw: string | null): string {
  const url = localDestination(raw);
  const portal = url && isCustomerPath(url.pathname) ? "customer" : "staff";
  const destination = url && (isCustomerPath(url.pathname) || url.pathname === "/workspace")
    ? url.pathname + url.search : null;
  return `/${portal}/login${destination ? `?next=${encodeURIComponent(destination)}` : ""}`;
}

/** Customer and staff continuations are separate, same-origin allowlists. */
export function safeLoginDestination(raw: string | null, role: Role): string {
  const fallback = defaultDestination(role);
  const url = localDestination(raw);
  if (!url) return fallback;
  if (role === "customer") {
    return isCustomerPath(url.pathname) ? url.pathname + url.search : fallback;
  }
  if (url.pathname !== "/workspace") return fallback;
  const view = url.searchParams.get("view") || "dashboard";
  if (!canOpenPage(role, view)) return fallback;
  return url.pathname + url.search;
}
