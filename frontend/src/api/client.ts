import createClient from "openapi-fetch";
import type { paths } from "./generated/schema";

// Only this transport owns the generated required CSRF header; callers still supply every business header.
type RequiredKeys<T> = { [K in keyof T]-?: Record<string, never> extends Pick<T, K> ? never : K }[keyof T];
type ManagedParameters<P> = P extends { header: infer H }
  ? Omit<P, "header"> & (RequiredKeys<Omit<H, "X-CSRF-Token">> extends never
    ? { header?: Omit<H, "X-CSRF-Token"> } : { header: Omit<H, "X-CSRF-Token"> }) : P;
type ManagedOperation<O> = O extends { parameters: infer P } ? Omit<O, "parameters"> & { parameters: ManagedParameters<P> } : O;
type BrowserPaths = { [Path in keyof paths]: { [Method in keyof paths[Path]]: ManagedOperation<paths[Path][Method]> } };
type ClientOptions = { baseUrl?: string; fetch?: typeof globalThis.fetch };

/** The business request was never dispatched. Unlike a lost write response, this is a known failure. */
export class RequestNotSentError extends Error {
  constructor(readonly status: number, readonly code: string, readonly traceId?: string) {
    super("request_not_sent"); this.name = "RequestNotSentError";
  }
}

/** A fresh request token stays in memory only, and can only be sent back to this application's origin. */
export async function fetchCsrfToken(url = "/api/v1/identity/csrf", fetcher: typeof globalThis.fetch = globalThis.fetch,
  signal?: AbortSignal): Promise<string> {
  try {
    if (typeof window !== "undefined" && new URL(url, window.location.origin).origin !== window.location.origin)
      throw new RequestNotSentError(400, "invalid_origin");
    const response = await fetcher(new Request(url, { credentials: "same-origin", cache: "no-store", redirect: "error", signal,
      headers: { Accept: "application/json" } }));
    const data: unknown = await response.json();
    const record = data && typeof data === "object" && !Array.isArray(data) ? data as Record<string, unknown> : {};
    if (!response.ok) throw new RequestNotSentError(response.status, typeof record.code === "string" ? record.code : "csrf_unavailable",
      typeof record.traceId === "string" ? record.traceId : undefined);
    if (typeof record.requestToken !== "string" || record.requestToken.length < 1 || record.requestToken.length > 8192 || /[\r\n]/.test(record.requestToken))
      throw new RequestNotSentError(502, "invalid_csrf_response");
    return record.requestToken;
  } catch (error) {
    if (error instanceof RequestNotSentError) throw error;
    throw new RequestNotSentError(503, "csrf_unavailable");
  }
}

/** Same-origin cookies; no provider tokens in browser storage and no automatic command retries. */
export function createUnitTokoClient(options: ClientOptions = {}) {
  const client = createClient<BrowserPaths>({ baseUrl: options.baseUrl, fetch: options.fetch,
    credentials: "same-origin", cache: "no-store", redirect: "error" });
  client.use({ async onRequest({ request, options: requestOptions }) {
    if (["GET", "HEAD", "OPTIONS"].includes(request.method)) return;
    const csrfUrl = new URL("/api/v1/identity/csrf", request.url);
    const token = await fetchCsrfToken(csrfUrl.href, requestOptions.fetch, request.signal);
    request.headers.set("X-CSRF-Token", token);
    return request;
  } });
  return client;
}
