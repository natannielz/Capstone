// The deployed application is the bounded pilot unless a later release is explicitly selected.
export const isPilotRelease = process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE !== "full";

const pilotPages = new Set([
  "/", "/shop", "/catalog/browse", "/ordering/requests", "/ordering/requests/incoming",
  "/identity/login", "/identity/account", "/workspace", "/katalog", "/administration/catalog", "/operations/health",
]);

/** Navigation visibility only. The API independently enforces release scope and authorization. */
export function isPilotPathAllowed(path: string): boolean {
  return pilotPages.has(path) || /^\/(shop|katalog)\/[A-Za-z0-9_-]+$/.test(path);
}

/** These pages only redirect to maintained journeys; no legacy API is admitted. */
export function isMaintainedEntryAlias(path: string): boolean {
  return ["/login", "/customer/login", "/staff/login", "/account", "/account/orders", "/cart", "/checkout"].includes(path) ||
    /^\/account\/orders\/[0-9]{1,19}$/.test(path);
}

export function isPilotApiPathAllowed(path: string): boolean {
  return ["/api/v1/system/status", "/api/v1/access/current", "/api/v1/identity/session", "/api/v1/identity/csrf"].includes(path) || [
    "/api/v1/catalog/administration", "/api/v1/catalog/variants", "/api/v1/ordering/requests", "/api/v1/operations/health", "/api/v1/public/catalog",
  ].some(prefix => path === prefix || path.startsWith(prefix + "/"));
}
