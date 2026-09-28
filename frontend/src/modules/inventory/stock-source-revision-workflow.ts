import { AccessFailure, stableContent } from "../administration/workflow";
import type { AccessScope } from "../organizations/access-scope";
import { stockPageRequest } from "./stock-count-workflow";
import {
  parseSourceSettings, sourceActor, sourceLocationKey, sourceNumber, sourceText, sourceTime, freezeSource,
  type SourceDecision,
} from "./stock-source-workflow";

export type SourceRevisionOperation = "stock.source.revision.propose" | "stock.source.revision.approve" | "stock.source.revision.reject" | "stock.source.revision.withdraw";
type Status = "proposed" | "approved" | "rejected" | "withdrawn";
const invalid = (): never => { throw new AccessFailure(502, "stock_source_revision_response_invalid"); };
const inputInvalid = (): never => { throw new AccessFailure(400, "stock_source_revision_input_invalid"); };
const record = (value: unknown): Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value)
  ? value as Record<string, unknown> : invalid();
function status(value: unknown): Status {
  return value === "proposed" || value === "approved" || value === "rejected" || value === "withdrawn" ? value : invalid();
}
export function sourceRevisionOperation(value: unknown): SourceRevisionOperation {
  if (value !== "stock.source.revision.propose" && value !== "stock.source.revision.approve" && value !== "stock.source.revision.reject" && value !== "stock.source.revision.withdraw") return inputInvalid();
  return value;
}
export function parseCurrentSource(value: unknown) {
  try {
    const v = record(value), poolVersion = sourceNumber(v.poolVersion), settings = parseSourceSettings(v.settings);
    const revisionProposalId = v.revisionProposalId === null ? null : sourceActor(v.revisionProposalId);
    if (v.poolState !== "inactive" && v.poolState !== "active" || (poolVersion === "1") !== (revisionProposalId === null)) return invalid();
    return freezeSource({ physicalPoolId: sourceActor(v.physicalPoolId), poolVersion, poolState: v.poolState as "inactive" | "active", settings,
      approvedAt: sourceTime(v.approvedAt), creationProposalId: sourceActor(v.creationProposalId), revisionProposalId });
  } catch { return invalid(); }
}
export type CurrentSource = ReturnType<typeof parseCurrentSource>;
function requireChange(base: ReturnType<typeof parseSourceSettings>, next: ReturnType<typeof parseSourceSettings>) {
  if (base.physicalKey !== next.physicalKey || sourceLocationKey(base.location) !== sourceLocationKey(next.location) || stableContent(base) === stableContent(next)) return invalid();
}
export function parseSourceRevision(value: unknown) {
  try {
    const v = record(value), state = status(v.status), basePoolVersion = sourceNumber(v.basePoolVersion);
    if (v.proposalVersion !== "1" || BigInt(basePoolVersion) === BigInt("9223372036854775807")) return invalid();
    const baseSettings = parseSourceSettings(v.baseSettings), proposedSettings = parseSourceSettings(v.proposedSettings);
    requireChange(baseSettings, proposedSettings);
    const proposedAt = sourceTime(v.proposedAt);
    const decision = v.decision === null ? null : (() => {
      const d = record(v.decision), decidedAt = sourceTime(d.decidedAt);
      if (d.status !== state || state === "proposed" || Date.parse(decidedAt) < Date.parse(proposedAt)) return invalid();
      return { status: state as Exclude<Status, "proposed">, decidedAt, reviewReference: sourceText(d.reviewReference, 160), reason: sourceText(d.reason, 1000) };
    })();
    const appliedPoolVersion = v.appliedPoolVersion === null ? null : sourceNumber(v.appliedPoolVersion);
    if ((state === "proposed") !== (decision === null) || (state === "approved") !== (appliedPoolVersion !== null) ||
      appliedPoolVersion !== null && BigInt(appliedPoolVersion) !== BigInt(basePoolVersion) + BigInt(1)) return invalid();
    return freezeSource({ id: sourceActor(v.id), proposalVersion: "1" as const, status: state, physicalPoolId: sourceActor(v.physicalPoolId),
      basePoolVersion, baseSettings, proposedSettings, proposedAt, reason: sourceText(v.reason, 1000), decision, appliedPoolVersion });
  } catch { return invalid(); }
}
export type SourceRevision = ReturnType<typeof parseSourceRevision>;
export function prepareSourceRevision(actorId: string, current: unknown, input: unknown) {
  try {
    const selected = parseCurrentSource(current), raw = record(input), settings = parseSourceSettings(raw);
    requireChange(selected.settings, settings);
    if (BigInt(selected.poolVersion) === BigInt("9223372036854775807")) return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation: "stock.source.revision.propose" as const, current: selected,
      poolId: selected.physicalPoolId, expectedVersion: selected.poolVersion, body: { ...settings, reason: sourceText(raw.reason, 1000) } });
  } catch { return inputInvalid(); }
}
export function prepareSourceRevisionDecision(actorId: string, proposal: unknown, action: SourceDecision, reviewReference: string, reason: string) {
  try {
    const selected = parseSourceRevision(proposal);
    if (selected.status !== "proposed") return inputInvalid();
    const operation = sourceRevisionOperation("stock.source.revision." + action);
    if (operation === "stock.source.revision.propose") return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation, proposal: selected, proposalId: selected.id,
      poolId: selected.physicalPoolId, expectedVersion: selected.proposalVersion,
      body: { reviewReference: sourceText(reviewReference.trim(), 160), reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export type SourceRevisionCommand = ReturnType<typeof prepareSourceRevision> | ReturnType<typeof prepareSourceRevisionDecision>;
export function parseSourceRevisionResult(value: unknown) {
  try {
    const v = record(value), state = status(v.status);
    if (v.proposalVersion !== "1") return invalid();
    const basePoolVersion = v.basePoolVersion === null ? null : sourceNumber(v.basePoolVersion);
    const appliedPoolVersion = v.appliedPoolVersion === null ? null : sourceNumber(v.appliedPoolVersion);
    const decisionId = v.decisionId === null ? null : sourceActor(v.decisionId);
    if ((state === "proposed") !== (basePoolVersion !== null) || (state === "proposed") !== (decisionId === null) ||
      (state === "approved") !== (appliedPoolVersion !== null) || appliedPoolVersion !== null && BigInt(appliedPoolVersion) < BigInt(2) ||
      basePoolVersion !== null && BigInt(basePoolVersion) === BigInt("9223372036854775807")) return invalid();
    return { proposalId: sourceActor(v.proposalId), proposalVersion: "1" as const, status: state, physicalPoolId: sourceActor(v.physicalPoolId),
      basePoolVersion, appliedPoolVersion, decisionId };
  } catch { return invalid(); }
}
const expectedStatus = (operation: SourceRevisionOperation): Status => operation === "stock.source.revision.propose" ? "proposed"
  : operation === "stock.source.revision.approve" ? "approved" : operation === "stock.source.revision.reject" ? "rejected" : "withdrawn";
export function sourceRevisionOutcome(value: unknown, command: SourceRevisionCommand) {
  const result = parseSourceRevisionResult(value);
  if (result.status !== expectedStatus(command.operation) || result.physicalPoolId !== command.poolId) return invalid();
  if ("proposalId" in command) {
    if (result.proposalId !== command.proposalId || result.appliedPoolVersion !== null &&
      BigInt(result.appliedPoolVersion) !== BigInt(command.proposal.basePoolVersion) + BigInt(1)) return invalid();
  } else if (result.basePoolVersion !== command.expectedVersion) return invalid();
  return result;
}
export function sourceRevisionReceipt(value: unknown, operation: SourceRevisionOperation) {
  sourceRevisionOperation(operation);
  const v = record(value);
  if (v.state === "not_confirmed") return v.result === null ? null : invalid();
  if (v.state !== "committed") return invalid();
  const result = parseSourceRevisionResult(v.result);
  return result.status === expectedStatus(operation) ? result : invalid();
}
function page<T>(value: unknown, offset: number, limit: number, parse: (item: unknown) => T, id: (item: T) => string, scope: (item: T) => AccessScope, location: AccessScope) {
  try {
    stockPageRequest(offset, limit);
    const v = record(value), total = sourceNumber(v.total, true);
    if (!Array.isArray(v.items)) return invalid();
    const remaining = BigInt(total) - BigInt(offset);
    const expected = remaining <= BigInt(0) ? 0 : remaining >= BigInt(limit) ? limit : Number(remaining);
    if (v.items.length !== expected) return invalid();
    const items = v.items.map(parse);
    if (items.some((item, index) => sourceLocationKey(scope(item)) !== sourceLocationKey(location) ||
      index > 0 && BigInt(id(items[index - 1])) >= BigInt(id(item)))) return invalid();
    return { total, items };
  } catch { return invalid(); }
}
export function parseCurrentSources(value: unknown, location: AccessScope, offset = 0, limit = 25) {
  return page(value, offset, limit, parseCurrentSource, item => item.physicalPoolId, item => item.settings.location, location);
}
export function parseSourceRevisions(value: unknown, location: AccessScope, poolId: string | null = null, offset = 0, limit = 25) {
  const result = page(value, offset, limit, parseSourceRevision, item => item.id, item => item.proposedSettings.location, location);
  if (poolId !== null && result.items.some(item => item.physicalPoolId !== poolId)) return invalid();
  return result;
}
