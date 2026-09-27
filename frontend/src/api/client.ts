import createClient from "openapi-fetch";
import type { paths } from "./generated/schema";

type ClientOptions = {
  baseUrl?: string;
  fetch?: typeof globalThis.fetch;
};

/** Same-origin application cookies; provider tokens never enter browser storage. */
export function createUnitTokoClient(options: ClientOptions = {}) {
  return createClient<paths>({
    baseUrl: options.baseUrl,
    fetch: options.fetch,
    credentials: "same-origin",
    cache: "no-store",
  });
}
