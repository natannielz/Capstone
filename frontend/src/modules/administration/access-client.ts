import { createUnitTokoClient } from "../../api/client";
import type { components } from "../../api/generated/schema";
import { AccessFailure } from "./workflow";

type Schemas = components["schemas"];
type Client = ReturnType<typeof createUnitTokoClient>;
export type ChangeAction = "approveGrant" | "approveMembership" | "revokeGrant" | "suspendMembership" | "rejectProposal" | "withdrawProposal";
export const commandOperations = {
  "grant-proposal": "access.grant.propose", "historical-grant-proposal": "access.grant.propose", "membership-proposal": "access.membership.propose",
  approveGrant: "access.grant.approve", approveMembership: "access.membership.approve",
  revokeGrant: "access.grant.revoke", suspendMembership: "access.membership.suspend",
  rejectProposal: "access.proposal.reject", withdrawProposal: "access.proposal.withdraw",
} as const;

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
    async catalog(actorId?: string) { return value(await client.GET("/api/v1/access/catalog", { params: { header: { "X-Expected-Actor-Id": actorId } } })); },
    async subject(userId: string, actorId?: string) { return value(await client.GET("/api/v1/access/subjects/{userId}", { params: { path: { userId }, header: { "X-Expected-Actor-Id": actorId } } })); },
    async proposals(offset = 0, actorId?: string) { return value(await client.GET("/api/v1/access/proposals", { params: { query: { offset, limit: 25 }, header: { "X-Expected-Actor-Id": actorId } } })); },
    async history(body: Schemas["AccessHistoryQueryDto"], actorId: string) { return value(await client.POST("/api/v1/access/history/query", { body, params: { header: { "X-Expected-Actor-Id": actorId } } })); },
    async receipt(operation: string, key: string, actorId: string) {
      return value(await client.POST("/api/v1/access/operations/lookup", { body: { operation, key, purpose: "operation-recovery" }, params: { header: { "X-Expected-Actor-Id": actorId } } }));
    },
    async resolve(operation: string, key: string, reason: string, actorId: string) {
      return value(await client.POST("/api/v1/access/operations/resolve", { body: { operation, reason }, params: { header: { "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
    async preview(body: Schemas["GrantRequestDto"], actorId?: string) {
      return value(await client.POST("/api/v1/access/grants/preview", { body, params: { header: { "X-Expected-Actor-Id": actorId } } }));
    },
    async proposeGrant(body: Schemas["GrantProposalDto"], key: string, actorId: string) {
      return value(await client.POST("/api/v1/access/grants/proposals", { body, params: { header: { "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
    async previewHistoricalGrant(body: Schemas["HistoricalGrantRequestDto"], actorId: string) {
      return value(await client.POST("/api/v1/access/historical-grants/preview", { body, params: { header: { "X-Expected-Actor-Id": actorId } } }));
    },
    async historicalTargets(body: Schemas["HistoricalTargetQueryDto"], actorId: string) {
      return value(await client.POST("/api/v1/access/historical-targets/query", { body, params: { header: { "X-Expected-Actor-Id": actorId } } }));
    },
    async proposeHistoricalGrant(body: Schemas["HistoricalGrantProposalDto"], key: string, actorId: string) {
      return value(await client.POST("/api/v1/access/historical-grants/proposals", { body, params: { header: { "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
    async proposeMembership(body: Schemas["MembershipProposalDto"], key: string, actorId: string) {
      return value(await client.POST("/api/v1/access/memberships/proposals", { body, params: { header: { "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
    async change(action: ChangeAction, id: string, version: string, reason: string, key: string, actorId: string) {
      const options = { body: { reason }, params: { path: { id }, header: { "Idempotency-Key": key, "If-Match": `"${version}"`, "X-Expected-Actor-Id": actorId } } };
      switch (action) {
        case "approveGrant": return value(await client.POST("/api/v1/access/grants/proposals/{id}/approve", options));
        case "approveMembership": return value(await client.POST("/api/v1/access/memberships/proposals/{id}/approve", options));
        case "revokeGrant": return value(await client.POST("/api/v1/access/grants/{id}/revoke", options));
        case "suspendMembership": return value(await client.POST("/api/v1/access/memberships/{id}/suspend", options));
        case "rejectProposal": return value(await client.POST("/api/v1/access/proposals/{id}/reject", options));
        case "withdrawProposal": return value(await client.POST("/api/v1/access/proposals/{id}/withdraw", options));
      }
    },
  };
}

export type AccessGateway = ReturnType<typeof createAccessGateway>;
