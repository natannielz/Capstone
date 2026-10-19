import { isPilotPathAllowed, isPilotRelease } from '../../lib/release-scope';
import { identityDestinations } from './identity-workflow';
import { workspaceDirectory } from './workspace-directory';

/** Former AdministrationNavigation `current` ids → their workspace path (kept so the 33 call sites stay unchanged). */
export const NAVIGATION_HREFS = {
  "substitutions": "/ordering/substitutions",
  "delegations": "/administration/delegations",
  "branch-requests": "/ordering/requests",
  "branch-requests-incoming": "/ordering/requests/incoming",
  "source-reconciliations": "/inventory/source-reconciliations",
  "cases": "/fulfillment/cases",
  "bank-receipts": "/billing/bank-receipts",
  "bank-statements": "/billing/bank-statements",
  "receiving-accounts": "/billing/receiving-accounts",
  "payment-reviews": "/billing/payment-reviews",
  "invoices": "/billing/invoices",
  "payments": "/billing/payments",
  "shipments": "/fulfillment/shipments",
  "preparations": "/fulfillment/preparations",
  "dispatches": "/fulfillment/dispatches",
  "orders": "/ordering/orders",
  "order-reviews": "/ordering/reviews",
  "prices": "/catalog/prices",
  "source-policies": "/inventory/sources/policies",
  "source-activations": "/inventory/sources/activations",
  "source-suspensions": "/inventory/sources/suspensions",
  "source-aliases": "/inventory/sources/aliases",
  "source-revisions": "/inventory/sources/revisions",
  "sources": "/inventory/sources",
  "counts": "/inventory/counts",
  "replenishment": "/inventory/replenishment",
  "stock-releases": "/inventory/stock-releases",
  "pack-conversion-rules": "/inventory/pack-conversions",
  "pack-conversion-executions": "/inventory/pack-conversion-executions",
  "catalog": "/administration/catalog",
  "account": "/identity/account",
  "accounts": "/administration/accounts",
  "person-reviews": "/administration/person-reviews",
  "access": "/administration/access",
  "organizations": "/administration/organizations",
  "organization-addresses": "/administration/organizations/addresses",
  "history": "/administration/history",
} as const;
export type NavigationId = keyof typeof NAVIGATION_HREFS;

export type NavigationItem = { href: string; label: string; description: string; current: boolean; available: boolean };
export type NavigationGroup = { id: string; label: string; items: NavigationItem[] };

const HOME: Omit<NavigationItem, 'current' | 'available'> = { href: '/identity/account', label: 'Pekerjaan saya', description: 'Ruang kerja dan tugas yang tersedia untuk akun Anda.' };
const ACCOUNT: Omit<NavigationItem, 'current' | 'available'> = { href: '/identity/account#profile-heading', label: 'Akun saya', description: 'Sesi, verifikasi dan kontak akun Anda.' };

/**
 * Presentation only: groups the live permission discovery (identityDestinations) into the directory areas.
 * `current` is the active pathname; it stays listed (available: false) even if its permission was withdrawn.
 */
export function workspaceNavigation(actions: readonly string[], current: string | null, options: { pilot?: boolean } = {}): NavigationGroup[] {
  const pilot = options.pilot ?? isPilotRelease;
  const allowed = (href: string) => !pilot || isPilotPathAllowed(href.split('#')[0]);
  const item = (entry: Omit<NavigationItem, 'current' | 'available'>, available: boolean): NavigationItem => ({ ...entry, current: entry.href === current, available });
  const groups: NavigationGroup[] = [{ id: 'home', label: 'Beranda', items: [item(HOME, true)] }];
  for (const area of workspaceDirectory(actions)) {
    const items = area.items.filter(entry => allowed(entry.href)).map(entry => item({ href: entry.href, label: entry.label, description: entry.description }, true));
    if (items.length) groups.push({ id: area.id, label: area.label, items });
  }
  const listed = new Set(groups.flatMap(group => group.items.map(entry => entry.href)));
  if (current && current !== HOME.href && !listed.has(current) && allowed(current)) {
    const known = identityDestinations(['*']).find(entry => entry.href === current);
    groups.push({ id: 'current', label: 'Halaman ini', items: [{ href: current, label: known?.label ?? 'Halaman ini', description: 'Akses ke halaman ini sedang tidak tersedia.', current: true, available: false }] });
  }
  groups.push({ id: 'account', label: 'Akun', items: [item(ACCOUNT, true)] });
  return groups;
}

/** Click guard shared by sidebar, sheet and palette: while a command is in flight, navigation is blocked. */
export function navigationClick(locked: boolean) {
  return (event: { preventDefault(): void }) => { if (locked) event.preventDefault(); };
}
