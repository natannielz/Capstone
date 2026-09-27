import { createUnitTokoClient as createProductionClient } from "../../src/api/client";

/** Existing domain-gateway fixtures provide a successful CSRF preflight; dedicated identity tests cover denial. */
export function createUnitTokoClient(options: Parameters<typeof createProductionClient>[0] = {}) {
  const transport = options?.fetch ?? globalThis.fetch;
  return createProductionClient({ ...options, fetch: async (input, init) => {
    if (input instanceof Request && input.method === "GET" && new URL(input.url).pathname === "/api/v1/identity/csrf")
      return Response.json({ requestToken: "fixture-request-token" });
    return transport(input, init);
  } });
}
