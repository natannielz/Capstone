import { createUnitTokoClient } from "../../api/client";
import type { components } from "../../api/generated/schema";
import { AccessFailure } from "./workflow";

type Schemas = components["schemas"];
type Client = ReturnType<typeof createUnitTokoClient>;
export type ChangeAction = "approveGrant" | "approveMembership" | "revokeGrant" | "suspendMembership";

function value<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}

/** The generated client owns the contract and same-origin cookie policy. No automatic mutation retries. */
export function createAccessGateway(client: Client = createUnitTokoClient()) {
  return {
    async current() { return value(await client.GET("/api/v1/access/current")); },
    async catalog() { return value(await client.GET("/api/v1/access/catalog")); },
    async subject(userId: string) { return value(await client.GET("/api/v1/access/subjects/{userId}", { params: { path: { userId } } })); },
    async proposals(offset = 0) { return value(await client.GET("/api/v1/access/proposals", { params: { query: { offset, limit: 25 } } })); },
    async preview(body: Schemas["GrantRequestDto"]) {
      return value(await client.POST("/api/v1/access/grants/preview", { body }));
    },
    async proposeGrant(body: Schemas["GrantProposalDto"], key: string) {
      return value(await client.POST("/api/v1/access/grants/proposals", { body, params: { header: { "Idempotency-Key": key } } }));
    },
    async proposeMembership(body: Schemas["MembershipProposalDto"], key: string) {
      return value(await client.POST("/api/v1/access/memberships/proposals", { body, params: { header: { "Idempotency-Key": key } } }));
    },
    async change(action: ChangeAction, id: string, version: string, reason: string, key: string) {
      const options = { body: { reason }, params: { path: { id }, header: { "Idempotency-Key": key, "If-Match": `"${version}"` } } };
      switch (action) {
        case "approveGrant": return value(await client.POST("/api/v1/access/grants/proposals/{id}/approve", options));
        case "approveMembership": return value(await client.POST("/api/v1/access/memberships/proposals/{id}/approve", options));
        case "revokeGrant": return value(await client.POST("/api/v1/access/grants/{id}/revoke", options));
        case "suspendMembership": return value(await client.POST("/api/v1/access/memberships/{id}/suspend", options));
      }
    },
  };
}

export type AccessGateway = ReturnType<typeof createAccessGateway>;
