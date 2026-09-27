import { createUnitTokoClient } from "../../api/client";
import type { components } from "../../api/generated/schema";
import { AccessFailure } from "../administration/workflow";

type Schemas = components["schemas"];
type Client = ReturnType<typeof createUnitTokoClient>;
export type OrganizationDecision = "approve" | "reject" | "withdraw";
function value<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === "string" ? error.code : "request_failed",
      typeof error.traceId === "string" ? error.traceId : undefined);
  }
  return result.data;
}

export function createOrganizationGateway(client: Client = createUnitTokoClient()) {
  return {
    async list(actorId: string, offset = 0) { return value(await client.GET("/api/v1/organizations", { params: { query: { offset, limit: 25 }, header: { "X-Expected-Actor-Id": actorId } } })); },
    async read(id: string, actorId: string) { return value(await client.GET("/api/v1/organizations/{id}", { params: { path: { id }, header: { "X-Expected-Actor-Id": actorId } } })); },
    async proposals(actorId: string, offset = 0) { return value(await client.GET("/api/v1/organizations/proposals", { params: { query: { offset, limit: 25 }, header: { "X-Expected-Actor-Id": actorId } } })); },
    async proposal(id: string, actorId: string) { return value(await client.GET("/api/v1/organizations/proposals/{id}", { params: { path: { id }, header: { "X-Expected-Actor-Id": actorId } } })); },
    async propose(body: Schemas["OrganizationProposalRequestDto"], key: string, actorId: string) {
      return value(await client.POST("/api/v1/organizations/proposals", { body,
        params: { header: { "Idempotency-Key": key, "X-Expected-Actor-Id": actorId } } }));
    },
    async decide(action: OrganizationDecision, id: string, version: string, reason: string, key: string, actorId: string) {
      const options = { body: { reason }, params: { path: { id }, header: { "Idempotency-Key": key, "If-Match": `"${version}"`, "X-Expected-Actor-Id": actorId } } };
      switch (action) {
        case "approve": return value(await client.POST("/api/v1/organizations/proposals/{id}/approve", options));
        case "reject": return value(await client.POST("/api/v1/organizations/proposals/{id}/reject", options));
        case "withdraw": return value(await client.POST("/api/v1/organizations/proposals/{id}/withdraw", options));
      }
    },
    async suspend(id: string, version: string, reason: string, key: string, actorId: string) {
      return value(await client.POST("/api/v1/access/scopes/{id}/suspend", { body: { reason },
        params: { path: { id }, header: { "Idempotency-Key": key, "If-Match": `"${version}"`, "X-Expected-Actor-Id": actorId } } }));
    },
  };
}

export type OrganizationGateway = ReturnType<typeof createOrganizationGateway>;
