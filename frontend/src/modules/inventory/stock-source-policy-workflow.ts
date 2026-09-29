import type { components } from "../../api/generated/schema";
import { AccessFailure } from "../administration/workflow";
import { stockPageRequest } from "./stock-count-workflow";
import { freezeSource, parseSourceSettings, sourceActor, sourceLocation, sourceNumber, sourceText, sourceTime, type SourceDecision } from "./stock-source-workflow";

type Wire = components["schemas"];
export type SourcePolicyOperation = "stock.source.policy.propose" | "stock.source.policy.approve" | "stock.source.policy.reject" | "stock.source.policy.withdraw";
const invalid = (): never => { throw new AccessFailure(502, "stock_source_policy_response_invalid"); };
const inputInvalid = (): never => { throw new AccessFailure(400, "stock_source_policy_input_invalid"); };
function record(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
  const row = value as Record<string, unknown>;
  if (Object.keys(row).length !== keys.length || keys.some(key => !Object.hasOwn(row, key))) return invalid();
  return row;
}
const hash = (value: unknown) => typeof value === "string" && /^[0-9a-f]{64}$(?![\s\S])/.test(value) ? value : invalid();
const scopeKeys = ["kind", "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId"];
function scope(value: unknown) { record(value, scopeKeys); return sourceLocation(value); }
const optionalId = (value: unknown) => value === null ? null : sourceActor(value);
const optionalNumber = (value: unknown, zero = false) => value === null ? null : sourceNumber(value, zero);
function policyInput(value: unknown, mode: "webLedger" | "externalLedger"): Wire["StockSourcePolicyInputDto"] {
  const row = record(value, ["writer", "externalTenant", "feedIntervalMilliseconds", "reconciliationProcedureReference", "outageProcedureReference"]);
  const feed = sourceNumber(row.feedIntervalMilliseconds, true, BigInt("922337203685477"));
  const tenant = row.externalTenant === null ? null : sourceText(row.externalTenant, 128);
  if (mode === "webLedger" ? row.writer !== "unitToko" || tenant !== null || feed !== "0"
    : row.writer !== "externalSystem" || tenant === null || feed === "0") return invalid();
  return { writer: row.writer as "unitToko" | "externalSystem", externalTenant: tenant, feedIntervalMilliseconds: feed,
    reconciliationProcedureReference: sourceText(row.reconciliationProcedureReference, 160), outageProcedureReference: sourceText(row.outageProcedureReference, 160) };
}
function configuration(value: unknown, poolVersion: string): Wire["StockSourcePolicyConfigurationDto"] {
  const row = record(value, ["settings", "active", "sourceApprovedAt", "creationProposalId", "revisionProposalId", "mappings"]);
  const raw = record(row.settings, ["location", "physicalKey", "label", "mode", "control", "maximumAgeMilliseconds", "safetyBufferBaseUnits",
    "externalNamespace", "reservationProtocolReference", "discrepancyOwnerReference", "handoffReference"]);
  scope(raw.location);
  const settings = parseSourceSettings(raw), revisionProposalId = optionalId(row.revisionProposalId);
  if (row.active !== false || (poolVersion === "1") !== (revisionProposalId === null) || !Array.isArray(row.mappings)) return invalid();
  const mappings = row.mappings.map((value): Wire["StockSourcePolicyMappingDto"] => {
    const item = record(value, ["aliasId", "aliasVersion", "poolVersion", "location", "enabled", "proposalId", "decisionId"]);
    const location = scope(item.location), version = sourceNumber(item.poolVersion);
    if (typeof item.enabled !== "boolean" || BigInt(version) > BigInt(poolVersion) || item.enabled && version !== poolVersion ||
      location.organizationId !== settings.location.organizationId || location.shopId !== settings.location.shopId ||
      location.warehouseId !== settings.location.warehouseId || location.channelId === settings.location.channelId) return invalid();
    return { aliasId: sourceActor(item.aliasId), aliasVersion: sourceNumber(item.aliasVersion), poolVersion: version, location,
      enabled: item.enabled, proposalId: sourceActor(item.proposalId), decisionId: sourceActor(item.decisionId) };
  });
  if (new Set(mappings.map(item => item.aliasId)).size !== mappings.length ||
    mappings.some((item, index) => index > 0 && BigInt(mappings[index - 1].location.channelId!) >= BigInt(item.location.channelId!))) return invalid();
  return { settings, active: false, sourceApprovedAt: sourceTime(row.sourceApprovedAt), creationProposalId: sourceActor(row.creationProposalId), revisionProposalId, mappings };
}
export function sourcePolicyOperation(value: unknown): SourcePolicyOperation {
  return value === "stock.source.policy.propose" || value === "stock.source.policy.approve" ||
    value === "stock.source.policy.reject" || value === "stock.source.policy.withdraw" ? value : inputInvalid();
}
export function parsePolicyPreview(value: unknown): Wire["StockSourcePolicyPreviewDto"] {
  try {
    const row = record(value, ["physicalPoolId", "poolVersion", "suspensionVersion", "policyVersion", "configurationHash", "configuration"]);
    const poolVersion = sourceNumber(row.poolVersion), policyVersion = sourceNumber(row.policyVersion, true);
    if (policyVersion === "9223372036854775807") return invalid();
    return freezeSource({ physicalPoolId: sourceActor(row.physicalPoolId), poolVersion, suspensionVersion: sourceNumber(row.suspensionVersion, true),
      policyVersion, configurationHash: hash(row.configurationHash), configuration: configuration(row.configuration, poolVersion) });
  } catch { return invalid(); }
}
type Status = Wire["StockSourcePolicyDetailDto"]["status"];
function status(value: unknown): Status { return value === "proposed" || value === "approved" || value === "rejected" || value === "withdrawn" ? value : invalid(); }
export function parseSourcePolicy(value: unknown): Wire["StockSourcePolicyDetailDto"] {
  try {
    const row = record(value, ["id", "proposalVersion", "status", "physicalPoolId", "poolVersion", "suspensionVersion", "basePolicyVersion",
      "configurationHash", "configuration", "input", "proposedAt", "reason", "decision", "decisionId", "policyId", "policyVersion"]);
    const state = status(row.status), poolVersion = sourceNumber(row.poolVersion), basePolicyVersion = sourceNumber(row.basePolicyVersion, true);
    const config = configuration(row.configuration, poolVersion), proposedAt = sourceTime(row.proposedAt);
    const decisionId = optionalId(row.decisionId), policyId = optionalId(row.policyId), policyVersion = optionalNumber(row.policyVersion);
    const decision = row.decision === null ? null : (() => {
      const d = record(row.decision, ["status", "decidedAt", "reviewReference", "reason"]), decidedAt = sourceTime(d.decidedAt);
      if (d.status !== state || state === "proposed" || Date.parse(decidedAt) < Date.parse(proposedAt)) return invalid();
      return { status: state, decidedAt, reviewReference: sourceText(d.reviewReference, 160), reason: sourceText(d.reason, 1000) };
    })();
    if (row.proposalVersion !== "1" || basePolicyVersion === "9223372036854775807" ||
      Date.parse(config.sourceApprovedAt) > Date.parse(proposedAt) || (state === "proposed") !== (decision === null) ||
      (decision === null) !== (decisionId === null) || (state === "approved") !== (policyId !== null) ||
      (state === "approved") !== (policyVersion !== null) || policyVersion !== null && BigInt(policyVersion) !== BigInt(basePolicyVersion) + BigInt(1)) return invalid();
    return freezeSource({ id: sourceActor(row.id), proposalVersion: "1", status: state, physicalPoolId: sourceActor(row.physicalPoolId),
      poolVersion, suspensionVersion: sourceNumber(row.suspensionVersion, true), basePolicyVersion, configurationHash: hash(row.configurationHash),
      configuration: config, input: policyInput(row.input, config.settings.mode), proposedAt, reason: sourceText(row.reason, 1000), decision, decisionId, policyId, policyVersion });
  } catch { return invalid(); }
}
export function parsePolicyReview(value: unknown): Wire["StockSourcePolicyReviewDto"] {
  try {
    const row = record(value, ["action", "bindingCurrent", "current", "proposal"]);
    if (row.action !== "approve" && row.action !== "reject" && row.action !== "withdraw" || typeof row.bindingCurrent !== "boolean") return invalid();
    const proposal = parseSourcePolicy(row.proposal), c = record(row.current, ["poolVersion", "suspensionVersion", "policyVersion", "configurationHash"]);
    const current = { poolVersion: sourceNumber(c.poolVersion), suspensionVersion: sourceNumber(c.suspensionVersion, true),
      policyVersion: sourceNumber(c.policyVersion, true), configurationHash: hash(c.configurationHash) };
    if (proposal.status !== "proposed" || row.action === "approve" && !row.bindingCurrent ||
      BigInt(current.poolVersion) < BigInt(proposal.poolVersion) || BigInt(current.suspensionVersion) < BigInt(proposal.suspensionVersion) ||
      BigInt(current.policyVersion) < BigInt(proposal.basePolicyVersion) || row.bindingCurrent &&
      (current.poolVersion !== proposal.poolVersion || current.suspensionVersion !== proposal.suspensionVersion ||
      current.policyVersion !== proposal.basePolicyVersion || current.configurationHash !== proposal.configurationHash)) return invalid();
    return freezeSource({ action: row.action, bindingCurrent: row.bindingCurrent, current, proposal });
  } catch { return invalid(); }
}
export function parsePolicyLatest(value: unknown): Wire["StockSourcePolicyLatestDto"] {
  try {
    const row = record(value, ["bindingCurrent", "policy"]), policy = row.policy === null ? null : parseSourcePolicy(row.policy);
    if (typeof row.bindingCurrent !== "boolean" || policy === null && row.bindingCurrent || policy !== null && policy.status !== "approved") return invalid();
    return freezeSource({ bindingCurrent: row.bindingCurrent, policy });
  } catch { return invalid(); }
}
export function parsePolicyHistory(value: unknown, poolId: string, offset = 0, limit = 25): Wire["StockSourcePolicyPageDto"] {
  try {
    stockPageRequest(offset, limit); sourceActor(poolId);
    const row = record(value, ["total", "items"]), total = sourceNumber(row.total, true), remaining = BigInt(total) - BigInt(offset);
    const expected = remaining <= BigInt(0) ? 0 : remaining >= BigInt(limit) ? limit : Number(remaining);
    if (!Array.isArray(row.items) || row.items.length !== expected) return invalid();
    const items = row.items.map(parseSourcePolicy);
    if (items.some((item, index) => item.physicalPoolId !== poolId || index > 0 && BigInt(items[index - 1].id) <= BigInt(item.id))) return invalid();
    return freezeSource({ total, items });
  } catch { return invalid(); }
}
export function preparePolicyProposal(actorId: string, preview: unknown, input: unknown, reason: string) {
  try {
    const selected = parsePolicyPreview(preview);
    return freezeSource({ actorId: sourceActor(actorId), operation: "stock.source.policy.propose" as const, preview: selected,
      poolId: selected.physicalPoolId, expectedVersion: selected.poolVersion,
      body: { expectedSuspensionVersion: selected.suspensionVersion, expectedPolicyVersion: selected.policyVersion,
        configurationHash: selected.configurationHash, input: policyInput(input, selected.configuration.settings.mode), reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export function preparePolicyDecision(actorId: string, review: unknown, reviewReference: string, reason: string) {
  try {
    const selected = parsePolicyReview(review), operation = sourcePolicyOperation("stock.source.policy." + selected.action);
    if (operation === "stock.source.policy.propose") return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation, review: selected, poolId: selected.proposal.physicalPoolId,
      proposalId: selected.proposal.id, expectedVersion: selected.proposal.proposalVersion,
      body: { reviewReference: sourceText(reviewReference.trim(), 160), reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export type SourcePolicyCommand = ReturnType<typeof preparePolicyProposal> | ReturnType<typeof preparePolicyDecision>;
export function parsePolicyResult(value: unknown): Wire["StockSourcePolicyCommandDto"] {
  try {
    const row = record(value, ["proposalId", "proposalVersion", "status", "physicalPoolId", "basePolicyVersion", "decisionId", "policyId", "policyVersion"]);
    const state = status(row.status), basePolicyVersion = optionalNumber(row.basePolicyVersion, true),
      policyVersion = optionalNumber(row.policyVersion), decisionId = optionalId(row.decisionId), policyId = optionalId(row.policyId);
    if (row.proposalVersion !== "1" || (state === "proposed") !== (basePolicyVersion !== null) ||
      (state === "proposed") !== (decisionId === null) || (state === "approved") !== (policyId !== null) ||
      (state === "approved") !== (policyVersion !== null) || basePolicyVersion === "9223372036854775807") return invalid();
    return freezeSource({ proposalId: sourceActor(row.proposalId), proposalVersion: "1", status: state, physicalPoolId: sourceActor(row.physicalPoolId),
      basePolicyVersion, decisionId, policyId, policyVersion });
  } catch { return invalid(); }
}
const expectedStatus = (operation: SourcePolicyOperation): Status => operation === "stock.source.policy.propose" ? "proposed"
  : operation === "stock.source.policy.approve" ? "approved" : operation === "stock.source.policy.reject" ? "rejected" : "withdrawn";
export function policyOutcome(value: unknown, command: SourcePolicyCommand) {
  const result = parsePolicyResult(value);
  if (result.physicalPoolId !== command.poolId || result.status !== expectedStatus(command.operation)) return invalid();
  if ("proposalId" in command) {
    if (result.proposalId !== command.proposalId || result.policyVersion !== null &&
      BigInt(result.policyVersion) !== BigInt(command.review.proposal.basePolicyVersion) + BigInt(1)) return invalid();
  } else if (result.basePolicyVersion !== command.body.expectedPolicyVersion) return invalid();
  return result;
}
export function policyReceipt(value: unknown, operation: SourcePolicyOperation) {
  sourcePolicyOperation(operation);
  const row = record(value, ["state", "result"]);
  if (row.state === "not_confirmed") return row.result === null ? null : invalid();
  if (row.state !== "committed") return invalid();
  const result = parsePolicyResult(row.result);
  return result.status === expectedStatus(operation) ? result : invalid();
}
export function policyAction(value: unknown): SourceDecision {
  return value === "approve" || value === "reject" || value === "withdraw" ? value : inputInvalid();
}