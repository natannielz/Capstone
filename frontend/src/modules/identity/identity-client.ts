import { fetchCsrfToken } from "../../api/client";
import { IdentityFailure, parseIdentitySession } from "./identity-workflow";

type Options = { baseUrl?: string; fetch?: typeof globalThis.fetch };

/** Authentication challenges use native navigation; this gateway only reads the local application. */
export function createIdentityGateway(options: Options = {}) {
  const fetcher = options.fetch ?? globalThis.fetch;
  const url = (path: string) => options.baseUrl ? new URL(path, options.baseUrl).href : path;
  return {
    async session(signal?: AbortSignal) {
      const endpoint = url("/api/v1/identity/session");
      if (typeof window !== "undefined" && new URL(endpoint, window.location.origin).origin !== window.location.origin)
        throw new IdentityFailure(400, "invalid_origin");
      const response = await fetcher(new Request(endpoint, { credentials: "same-origin", cache: "no-store", redirect: "error", signal,
        headers: { Accept: "application/json" } }));
      const data: unknown = await response.json();
      if (!response.ok) {
        const record = data && typeof data === "object" ? data as Record<string, unknown> : {};
        throw new IdentityFailure(response.status, typeof record.code === "string" ? record.code : "identity_unavailable",
          typeof record.traceId === "string" ? record.traceId : undefined);
      }
      return parseIdentitySession(data);
    },
    csrf(signal?: AbortSignal) { return fetchCsrfToken(url("/api/v1/identity/csrf"), fetcher, signal); },
  };
}

export type IdentityGateway = ReturnType<typeof createIdentityGateway>;
