import { createUnitTokoClient, RequestNotSentError } from "../../api/client";
import { AccessFailure, stableContent } from "../administration/workflow";
import type { OperationReference } from "../administration/operation-journal";
import { sourceActor, sourceKey, type SourceDecision } from "./stock-source-workflow";
import { parsePolicyPreview, parsePolicyReview, parsePolicyLatest, parsePolicyHistory, parseSourcePolicy, preparePolicyProposal,
  preparePolicyDecision, policyOutcome, policyReceipt, sourcePolicyOperation, policyAction, type SourcePolicyCommand } from "./stock-source-policy-workflow";

const invalid = (): never => { throw new AccessFailure(400, "stock_source_policy_input_invalid"); };
const badResponse = (): never => { throw new AccessFailure(502, "stock_source_policy_response_invalid"); };
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}
function reviewed(command: SourcePolicyCommand): SourcePolicyCommand {
  try {
    const copy = "proposalId" in command
      ? preparePolicyDecision(command.actorId, command.review, command.body.reviewReference, command.body.reason)
      : preparePolicyProposal(command.actorId, command.preview, command.body.input, command.body.reason);
    if (stableContent(copy) !== stableContent(command)) return invalid();
    return copy;
  } catch { return invalid(); }
}
/** One attempt per reviewed command; unknown outcomes retain the original reference for receipt lookup. */
export function createStockSourcePolicyGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async preview(actorId: string, poolId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId);
      const result = parsePolicyPreview(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/policy-preview/query", {
        body: { purpose: "inventory-source-policy-preview" }, params: { path: { poolId: pool }, header }, signal,
      })));
      return result.physicalPoolId === pool ? result : badResponse();
    },
    async review(actorId: string, poolId: string, proposalId: string, action: SourceDecision, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId), id = sourceActor(proposalId), selected = policyAction(action);
      const result = parsePolicyReview(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/policy-proposals/{id}/review/query", {
        body: { purpose: "inventory-source-policy-review", action: selected }, params: { path: { poolId: pool, id }, header }, signal,
      })));
      return result.proposal.physicalPoolId === pool && result.proposal.id === id && result.action === selected ? result : badResponse();
    },
    async latest(actorId: string, poolId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId);
      const result = parsePolicyLatest(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/policy/query", {
        body: { purpose: "inventory-source-policy-history" }, params: { path: { poolId: pool }, header }, signal,
      })));
      return result.policy === null || result.policy.physicalPoolId === pool ? result : badResponse();
    },
    async history(actorId: string, poolId: string, offset = 0, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId);
      if (!Number.isInteger(offset) || offset < 0 || offset > 10000) return invalid();
      return parsePolicyHistory(data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/policy-proposals/query", {
        body: { purpose: "inventory-source-policy-history", offset, limit: 25 }, params: { path: { poolId: pool }, header }, signal,
      })), pool, offset, 25);
    },
    async detail(actorId: string, poolId: string, proposalId: string, signal?: AbortSignal) {
      const header = { "X-Expected-Actor-Id": sourceActor(actorId) }, pool = sourceActor(poolId), id = sourceActor(proposalId);
      const value = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/policy-proposals/{id}/query", {
        body: { purpose: "inventory-source-policy-history" }, params: { path: { poolId: pool, id }, header }, signal,
      }));
      if (value === null) return null;
      const result = parseSourcePolicy(value);
      return result.physicalPoolId === pool && result.id === id ? result : badResponse();
    },
    async submit(command: SourcePolicyCommand, key: string, isCurrent: () => boolean = () => true) {
      const snapshot = reviewed(command), header = { "X-Expected-Actor-Id": sourceActor(snapshot.actorId),
        "Idempotency-Key": sourceKey(key), "If-Match": '"' + snapshot.expectedVersion + '"' };
      if (!isCurrent()) throw new RequestNotSentError(409, "stock_source_policy_view_stale");
      let result: unknown;
      if ("proposalId" in snapshot) {
        const route = {
          "stock.source.policy.approve": "/api/v1/inventory/sources/policy-proposals/{id}/approve",
          "stock.source.policy.reject": "/api/v1/inventory/sources/policy-proposals/{id}/reject",
          "stock.source.policy.withdraw": "/api/v1/inventory/sources/policy-proposals/{id}/withdraw",
        } as const;
        result = data(await client.POST(route[snapshot.operation], { body: snapshot.body, params: { path: { id: snapshot.proposalId }, header } }));
      } else result = data(await client.POST("/api/v1/inventory/sources/pools/{poolId}/policy-proposals", {
        body: snapshot.body, params: { path: { poolId: snapshot.poolId }, header },
      }));
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return policyOutcome(result, snapshot);
    },
    async receipt(entry: OperationReference, poolId: string, signal?: AbortSignal) {
      const operation = sourcePolicyOperation(entry.operation), header = { "X-Expected-Actor-Id": sourceActor(entry.actorId) }, pool = sourceActor(poolId);
      const result = policyReceipt(data(await client.POST("/api/v1/inventory/sources/policy-operations/query", {
        body: { purpose: "operation-recovery", physicalPoolId: pool, operation, key: sourceKey(entry.key) }, params: { header }, signal,
      })), operation);
      return result === null || result.physicalPoolId === pool ? result : badResponse();
    },
  };
}
export type StockSourcePolicyGateway = ReturnType<typeof createStockSourcePolicyGateway>;