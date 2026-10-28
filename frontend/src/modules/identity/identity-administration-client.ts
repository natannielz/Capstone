import { createUnitTokoClient } from "../../api/client";
import type { components } from "../../api/generated/schema";
import { AccessFailure } from "../administration/workflow";

type Schemas = components["schemas"];
type Client = ReturnType<typeof createUnitTokoClient>;
export type InvitationAction = "approve" | "reject" | "withdraw" | "revoke";
export const identityOperations = {
  propose: "identity.invitation.propose", approve: "identity.invitation.approve", reject: "identity.invitation.rejected",
  withdraw: "identity.invitation.withdrawn", revoke: "identity.invitation.revoked", recovery: "identity.recovery.request",
} as const;
function value<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}

/** Scoped private reads use request bodies; only the shared transport manages cookies and CSRF. */
export function createIdentityAdministrationGateway(client: Client = createUnitTokoClient()) {
  return {
    async profiles(actorId: string, term = "", offset = 0) {
      const params = { header: { "X-Expected-Actor-Id": actorId } };
      return term.trim()
        ? value(await client.POST("/api/v1/identity/administration/directory/search", { body: { purpose: "access-review", term: term.trim(), offset, limit: 25 }, params }))
        : value(await client.POST("/api/v1/identity/administration/directory/query", { body: { purpose: "access-review", offset, limit: 25 }, params }));
    },
    async invitations(actorId: string, offset = 0) {
      return value(await client.POST("/api/v1/identity/administration/invitations/query", {
        body: { purpose: "access-review", offset, limit: 25 }, params: { header: { "X-Expected-Actor-Id": actorId } },
      }));
    },
    async propose(body: Schemas["IdentityInvitationProposalDto"], key: string, actorId: string) {
      return value(await client.POST("/api/v1/identity/administration/invitations", { body,
        params: { header: { "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
    async decide(action: InvitationAction, id: string, version: string, reason: string, key: string, actorId: string) {
      const options = { body: { reason }, params: { path: { id }, header: {
        "If-Match": `"${version}"`, "Idempotency-Key": key, "X-Expected-Actor-Id": actorId,
      } } };
      switch (action) {
        case "approve": return value(await client.POST("/api/v1/identity/administration/invitations/{id}/approve", options));
        case "reject": return value(await client.POST("/api/v1/identity/administration/invitations/{id}/reject", options));
        case "withdraw": return value(await client.POST("/api/v1/identity/administration/invitations/{id}/withdraw", options));
        case "revoke": return value(await client.POST("/api/v1/identity/administration/invitations/{id}/revoke", options));
      }
    },
    async recover(userId: string, securityVersion: string, kind: "password" | "factor", reason: string, key: string, actorId: string) {
      return value(await client.POST("/api/v1/identity/administration/recovery", { body: { userId, kind, reason },
        params: { header: { "If-Match": `"${securityVersion}"`, "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
  };
}
export type IdentityAdministrationGateway = ReturnType<typeof createIdentityAdministrationGateway>;