import { AccessFailure } from "../administration/workflow";
import type { AccessScope } from "../organizations/access-scope";
import { stockPageRequest } from "./stock-count-workflow";
import { parseCurrentSource } from "./stock-source-revision-workflow";
import { sourceActor, sourceLocation, sourceLocationKey, sourceNumber, sourceText, sourceTime, freezeSource, type SourceDecision } from "./stock-source-workflow";

export type SourceAliasOperation = "stock.source.alias.propose" | "stock.source.alias.approve" | "stock.source.alias.reject" | "stock.source.alias.withdraw";
type Status = "proposed" | "approved" | "rejected" | "withdrawn";
const invalid = (): never => { throw new AccessFailure(502, "stock_source_alias_response_invalid"); };
const inputInvalid = (): never => { throw new AccessFailure(400, "stock_source_alias_input_invalid"); };
const maximum = BigInt("9223372036854775807");
function record(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
  const row = value as Record<string, unknown>;
  if (Object.keys(row).length !== keys.length || keys.some(key => !Object.hasOwn(row, key))) return invalid();
  return row;
}
function state(value: unknown): Status {
  return value === "proposed" || value === "approved" || value === "rejected" || value === "withdrawn" ? value : invalid();
}
function scope(value: unknown): AccessScope {
  record(value, ["kind", "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId"]);
  return sourceLocation(value);
}
function pair(owner: AccessScope, requested: AccessScope) {
  if (owner.organizationId !== requested.organizationId || owner.shopId !== requested.shopId ||
    owner.warehouseId !== requested.warehouseId || owner.channelId === requested.channelId) return invalid();
}
export function sourceAliasOperation(value: unknown): SourceAliasOperation {
  if (value !== "stock.source.alias.propose" && value !== "stock.source.alias.approve" &&
    value !== "stock.source.alias.reject" && value !== "stock.source.alias.withdraw") return inputInvalid();
  return value;
}
export function parseSourceAlias(value: unknown) {
  try {
    const v = record(value, ["id", "proposalVersion", "status", "physicalPoolId", "poolVersion", "ownerLocation", "requestedLocation",
      "physicalKey", "baseAliasVersion", "enabled", "proposedAt", "reason", "decision", "appliedAliasVersion"]);
    const status = state(v.status), poolVersion = sourceNumber(v.poolVersion), baseAliasVersion = sourceNumber(v.baseAliasVersion, true);
    const ownerLocation = scope(v.ownerLocation), requestedLocation = scope(v.requestedLocation); pair(ownerLocation, requestedLocation);
    const physicalKey = sourceText(v.physicalKey, 64), proposedAt = sourceTime(v.proposedAt);
    if (v.proposalVersion !== "1" || BigInt(baseAliasVersion) === maximum || typeof v.enabled !== "boolean" ||
      baseAliasVersion === "0" && !v.enabled || !/^[a-z][a-z0-9._-]{0,63}$(?![\s\S])/.test(physicalKey)) return invalid();
    const decision = v.decision === null ? null : (() => {
      const d = record(v.decision, ["status", "decidedAt", "reviewReference", "reason"]), decidedAt = sourceTime(d.decidedAt);
      if (d.status !== status || status === "proposed" || Date.parse(decidedAt) < Date.parse(proposedAt)) return invalid();
      return { status: status as Exclude<Status, "proposed">, decidedAt, reviewReference: sourceText(d.reviewReference, 160), reason: sourceText(d.reason, 1000) };
    })();
    const appliedAliasVersion = v.appliedAliasVersion === null ? null : sourceNumber(v.appliedAliasVersion);
    if ((status === "proposed") !== (decision === null) || (status === "approved") !== (appliedAliasVersion !== null) ||
      appliedAliasVersion !== null && BigInt(appliedAliasVersion) !== BigInt(baseAliasVersion) + BigInt(1)) return invalid();
    return freezeSource({ id: sourceActor(v.id), proposalVersion: "1" as const, status, physicalPoolId: sourceActor(v.physicalPoolId),
      poolVersion, ownerLocation, requestedLocation, physicalKey, baseAliasVersion, enabled: v.enabled, proposedAt,
      reason: sourceText(v.reason, 1000), decision, appliedAliasVersion });
  } catch { return invalid(); }
}
export type SourceAlias = ReturnType<typeof parseSourceAlias>;
export function parseCurrentAlias(value: unknown) {
  try {
    const v = record(value, ["source", "requestedLocation", "bindingState", "mapping"]);
    const rawSource = record(v.source, ["physicalPoolId", "poolVersion", "poolState", "settings", "approvedAt", "creationProposalId", "revisionProposalId"]);
    record(rawSource.settings, ["location", "physicalKey", "label", "mode", "control", "maximumAgeMilliseconds", "safetyBufferBaseUnits",
      "externalNamespace", "reservationProtocolReference", "discrepancyOwnerReference", "handoffReference"]);
    const source = parseCurrentSource(v.source), requestedLocation = scope(v.requestedLocation);
    pair(source.settings.location, requestedLocation);
    const mapping = v.mapping === null ? null : parseSourceAlias(v.mapping);
    if (mapping && (mapping.status !== "approved" || mapping.physicalPoolId !== source.physicalPoolId ||
      sourceLocationKey(mapping.ownerLocation) !== sourceLocationKey(source.settings.location) ||
      sourceLocationKey(mapping.requestedLocation) !== sourceLocationKey(requestedLocation) || mapping.physicalKey !== source.settings.physicalKey ||
      BigInt(mapping.poolVersion) > BigInt(source.poolVersion) ||
      mapping.poolVersion === source.poolVersion && Date.parse(mapping.proposedAt) < Date.parse(source.approvedAt) ||
      mapping.poolVersion !== source.poolVersion && Date.parse(mapping.decision!.decidedAt) > Date.parse(source.approvedAt))) return invalid();
    const bindingState: "absent" | "disabled" | "stale" | "current" = mapping === null ? "absent" : !mapping.enabled ? "disabled" : mapping.poolVersion === source.poolVersion ? "current" : "stale";
    if (v.bindingState !== bindingState) return invalid();
    return freezeSource({ source, requestedLocation, bindingState, mapping });
  } catch { return invalid(); }
}
export type CurrentAlias = ReturnType<typeof parseCurrentAlias>;
export function prepareSourceAlias(actorId: string, current: unknown, enabled: boolean, reason: string) {
  try {
    const selected = parseCurrentAlias(current), basis = selected.mapping?.appliedAliasVersion ?? "0";
    if (typeof enabled !== "boolean" || BigInt(basis) === maximum || (!selected.mapping ? !enabled :
      selected.mapping.enabled === enabled && selected.mapping.poolVersion === selected.source.poolVersion)) return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation: "stock.source.alias.propose" as const, current: selected,
      poolId: selected.source.physicalPoolId, expectedVersion: selected.source.poolVersion,
      body: { requestedLocation: selected.requestedLocation, expectedAliasVersion: basis, enabled, reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export function prepareSourceAliasDecision(actorId: string, proposal: unknown, action: SourceDecision, reviewReference: string, reason: string) {
  try {
    const selected = parseSourceAlias(proposal);
    if (selected.status !== "proposed") return inputInvalid();
    const operation = sourceAliasOperation("stock.source.alias." + action);
    if (operation === "stock.source.alias.propose") return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation, proposal: selected, proposalId: selected.id,
      poolId: selected.physicalPoolId, expectedVersion: selected.proposalVersion,
      body: { reviewReference: sourceText(reviewReference.trim(), 160), reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export type SourceAliasCommand = ReturnType<typeof prepareSourceAlias> | ReturnType<typeof prepareSourceAliasDecision>;
export function parseSourceAliasResult(value: unknown) {
  try {
    const v = record(value, ["proposalId", "proposalVersion", "status", "physicalPoolId", "poolVersion", "baseAliasVersion", "appliedAliasVersion", "decisionId"]);
    const status = state(v.status), baseAliasVersion = v.baseAliasVersion === null ? null : sourceNumber(v.baseAliasVersion, true);
    const appliedAliasVersion = v.appliedAliasVersion === null ? null : sourceNumber(v.appliedAliasVersion);
    const decisionId = v.decisionId === null ? null : sourceActor(v.decisionId);
    if (v.proposalVersion !== "1" || (status === "proposed") !== (baseAliasVersion !== null) || (status === "proposed") !== (decisionId === null) ||
      (status === "approved") !== (appliedAliasVersion !== null) || baseAliasVersion !== null && BigInt(baseAliasVersion) === maximum) return invalid();
    return freezeSource({ proposalId: sourceActor(v.proposalId), proposalVersion: "1" as const, status, physicalPoolId: sourceActor(v.physicalPoolId),
      poolVersion: sourceNumber(v.poolVersion), baseAliasVersion, appliedAliasVersion, decisionId });
  } catch { return invalid(); }
}
const expectedStatus = (operation: SourceAliasOperation): Status => operation === "stock.source.alias.propose" ? "proposed"
  : operation === "stock.source.alias.approve" ? "approved" : operation === "stock.source.alias.reject" ? "rejected" : "withdrawn";
export function sourceAliasOutcome(value: unknown, command: SourceAliasCommand) {
  const result = parseSourceAliasResult(value);
  if (result.status !== expectedStatus(command.operation) || result.physicalPoolId !== command.poolId) return invalid();
  if ("proposalId" in command) {
    if (result.proposalId !== command.proposalId || result.poolVersion !== command.proposal.poolVersion ||
      result.appliedAliasVersion !== null && BigInt(result.appliedAliasVersion) !== BigInt(command.proposal.baseAliasVersion) + BigInt(1)) return invalid();
  } else if (result.poolVersion !== command.expectedVersion || result.baseAliasVersion !== command.body.expectedAliasVersion) return invalid();
  return result;
}
export function sourceAliasReceipt(value: unknown, operation: SourceAliasOperation) {
  sourceAliasOperation(operation);
  const v = record(value, ["state", "result"]);
  if (v.state === "not_confirmed") return v.result === null ? null : invalid();
  if (v.state !== "committed") return invalid();
  const result = parseSourceAliasResult(v.result);
  return result.status === expectedStatus(operation) ? result : invalid();
}
export function parseSourceAliases(value: unknown, poolId: string, requestedLocation: AccessScope, offset = 0, limit = 25) {
  try {
    stockPageRequest(offset, limit); sourceActor(poolId); sourceLocation(requestedLocation);
    const v = record(value, ["total", "items"]), total = sourceNumber(v.total, true);
    const remaining = BigInt(total) - BigInt(offset), expected = remaining <= BigInt(0) ? 0 : remaining >= BigInt(limit) ? limit : Number(remaining);
    if (!Array.isArray(v.items) || v.items.length !== expected) return invalid();
    const items = v.items.map(parseSourceAlias);
    if (items.some((item, index) => item.physicalPoolId !== poolId || sourceLocationKey(item.requestedLocation) !== sourceLocationKey(requestedLocation) ||
      index > 0 && BigInt(items[index - 1].id) >= BigInt(item.id))) return invalid();
    return freezeSource({ total, items });
  } catch { return invalid(); }
}
