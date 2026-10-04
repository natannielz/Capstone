// The deployed application is the bounded pilot unless a later release is explicitly selected.
export const isPilotRelease = process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE !== "full";

const pilotPages = new Set([
  "/", "/shop", "/catalog/browse", "/ordering/requests", "/ordering/requests/incoming",
  "/identity/login", "/identity/account", "/workspace", "/administration/catalog",
]);

/** Navigation visibility only. The API independently enforces release scope and authorization. */
export function isPilotPathAllowed(path: string): boolean {
  return pilotPages.has(path) || /^\/shop\/[A-Za-z0-9_-]+$/.test(path);
}

export function isPilotApiPathAllowed(path: string): boolean {
  return ["/api/v1/system/status", "/api/v1/access/current", "/api/v1/identity/session", "/api/v1/identity/csrf"].includes(path) || [
    "/api/v1/catalog/administration", "/api/v1/catalog/variants", "/api/v1/ordering/requests",
  ].some(prefix => path === prefix || path.startsWith(prefix + "/"));
}
