import { createUnitTokoClient } from "../../api/client";
import { AccessFailure } from "../administration/workflow";
import type { OperationReference } from "../administration/operation-journal";
import { parsePersonReviewPage, parsePersonReviewTargets, parsePersonReviewDetail, parsePersonReviewPreview, personReviewReceipt, validatePersonReviewOutcome,
  type PersonReviewCommand, type PersonReviewDraft } from "./person-review-workflow";

type Client = ReturnType<typeof createUnitTokoClient>;
function value<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === "object" ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === "string" ? problem.code : "request_failed",
      typeof problem.traceId === "string" ? problem.traceId : undefined);
  }
  return result.data;
}

/** Private POST reads and writes share the generated same-origin transport; commands are never retried here. */
export function createPersonReviewGateway(client: Client = createUnitTokoClient()) {
  return {
    async list(actorId: string, status = "pending", offset = 0, signal?: AbortSignal) {
      return parsePersonReviewPage(value(await client.POST("/api/v1/identity/administration/person-reviews/proposals/query", {
        body: { purpose: "access-review", status: status || null, offset, limit: 25 }, params: { header: { "X-Expected-Actor-Id": actorId } }, signal,
      })));
    },
    async targets(actorId: string, term: string, offset = 0, signal?: AbortSignal) {
      return parsePersonReviewTargets(value(await client.POST("/api/v1/identity/administration/person-reviews/targets/search", {
        body: { purpose: "access-review", term: term.trim(), offset, limit: 25 }, params: { header: { "X-Expected-Actor-Id": actorId } }, signal,
      })));
    },
    async detail(actorId: string, id: string, signal?: AbortSignal) {
      const result = parsePersonReviewDetail(value(await client.POST("/api/v1/identity/administration/person-reviews/proposals/{id}/query", {
        body: { purpose: "access-review" }, params: { path: { id }, header: { "X-Expected-Actor-Id": actorId } }, signal,
      })));
      if (result.summary.result.proposalId !== id) throw new AccessFailure(502, "person_response_invalid");
      return result;
    },
    async preview(draft: PersonReviewDraft, signal?: AbortSignal) {
      if (draft.action !== "propose" && draft.action !== "approve") throw new AccessFailure(400, "person_review_invalid");
      const options = { body: { purpose: "access-review" }, params: { path: { id: draft.action === "propose" ? draft.groupId : draft.proposalId! },
        header: { "X-Expected-Actor-Id": draft.actorId, "If-Match": `"${draft.action === "propose" ? draft.groupVersion : draft.proposalVersion}"` } }, signal };
      return parsePersonReviewPreview(value(draft.action === "propose"
        ? await client.POST("/api/v1/identity/administration/person-reviews/targets/{id}/preview", options)
        : await client.POST("/api/v1/identity/administration/person-reviews/proposals/{id}/preview", options)));
    },
    async submit(command: PersonReviewCommand, key: string, isCurrent: () => boolean = () => true) {
      const headers = { "X-Expected-Actor-Id": command.actorId, "Idempotency-Key": key };
      const decision = { params: { path: { id: command.proposalId! }, header: { ...headers, "If-Match": `"${command.proposalVersion}"` } }, body: { reason: command.reason } };
      let result: unknown;
      switch (command.action) {
        case "propose": result = value(await client.POST("/api/v1/identity/administration/person-reviews/proposals", {
          body: { previewReference: command.previewReference!, reason: command.reason }, params: { header: headers },
        })); break;
        case "approve": result = value(await client.POST("/api/v1/identity/administration/person-reviews/proposals/{id}/approve", {
          ...decision, body: { previewReference: command.previewReference!, reason: command.reason },
        })); break;
        case "reject": result = value(await client.POST("/api/v1/identity/administration/person-reviews/proposals/{id}/reject", decision)); break;
        case "withdraw": result = value(await client.POST("/api/v1/identity/administration/person-reviews/proposals/{id}/withdraw", decision)); break;
      }
      if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
      return validatePersonReviewOutcome(result, command.action, command);
    },
    async resolve(entry: OperationReference, reason: string, expected?: Pick<PersonReviewCommand, "proposalId" | "groupId">) {
      return personReviewReceipt(value(await client.POST("/api/v1/identity/administration/person-reviews/operations/resolve", {
        body: { operation: entry.operation, reason: reason.trim() }, params: { header: { "X-Expected-Actor-Id": entry.actorId, "Idempotency-Key": entry.key } },
      })), entry, expected);
    },
    async receipt(entry: OperationReference, expected?: Pick<PersonReviewCommand, "proposalId" | "groupId">) {
      return personReviewReceipt(value(await client.POST("/api/v1/identity/administration/person-reviews/operations/query", {
        body: { purpose: "operation-recovery", operation: entry.operation, key: entry.key }, params: { header: { "X-Expected-Actor-Id": entry.actorId } },
      })), entry, expected);
    },
  };
}
export type PersonReviewGateway = ReturnType<typeof createPersonReviewGateway>;