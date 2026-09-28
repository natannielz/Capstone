import type { components } from "../../api/generated/schema";
import { AccessFailure } from "../administration/workflow";
import { validReference, type AccessScope } from "../organizations/access-scope";
import { parseStockLocation, parseStockWarehouses, stockLocationKey, stockPageRequest } from "./stock-count-workflow";

export type SourceDecision = "approve" | "reject" | "withdraw";
export type SourceOperation = "stock.source.propose" | "stock.source.approve" | "stock.source.reject" | "stock.source.withdraw";
export type SourcePurpose = "inventory-source-propose" | "inventory-source-review" | "inventory-source-history";
export type SourceDraft = {
  physicalKey: string; label: string; mode: string; control: string; maximumAgeSeconds: string; safetyBufferBaseUnits: string;
  externalNamespace: string; reservationProtocolReference: string; discrepancyOwnerReference: string; handoffReference: string; reason: string;
};
const invalid = (): never => { throw new AccessFailure(502, "stock_source_response_invalid"); };
const inputInvalid = (): never => { throw new AccessFailure(400, "stock_source_input_invalid"); };
const record = (value: unknown): Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
function text(value: unknown, maximum: number): string {
  if (typeof value !== "string" || !value.length || value.length > maximum || value.trim() !== value ||
    !value.isWellFormed() || /[\u0000-\u001f\u007f-\u009f]/.test(value)) return invalid();
  return value;
}
function id(value: unknown): string {
  return typeof value === "string" && value.trim() === value && validReference(value) ? value : invalid();
}
function integer(value: unknown, zero = false, maximum = BigInt("9223372036854775807")): string {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]{0,18})$(?![\s\S])/.test(value) ||
    BigInt(value) > maximum || !zero && value === "0") return invalid();
  return value;
}
function key(value: unknown): string {
  return typeof value === "string" && /^[a-z][a-z0-9._-]{0,63}$(?![\s\S])/.test(value) ? value : invalid();
}
function time(value: unknown): string {
  if (typeof value !== "string" || value.length > 40 ||
    !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|\+00:00)$(?![\s\S])/.test(value) ||
    !Number.isFinite(Date.parse(value))) return invalid();
  const year = Number(value.slice(0, 4)), month = Number(value.slice(5, 7)), day = Number(value.slice(8, 10));
  const last = [31, year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  if (year < 1 || day < 1 || day > last || !last || Number(value.slice(11, 13)) > 23 ||
    Number(value.slice(14, 16)) > 59 || Number(value.slice(17, 19)) > 59) return invalid();
  return value;
}
function freeze<T>(value: T): T {
  if (value && typeof value === "object") { Object.values(value).forEach(freeze); Object.freeze(value); }
  return value;
}
export function parseSourceSettings(value: unknown): components["schemas"]["StockSourceSettingsDto"] {
  const v = record(value), mode = v.mode, control = v.control;
  if (mode !== "webLedger" && mode !== "externalLedger") return invalid();
  if (control !== "exclusiveWebAllocation" && control !== "sharedWebLedger" && control !== "externalHold") return invalid();
  if (mode === "webLedger" ? control === "externalHold" || v.externalNamespace !== null || v.reservationProtocolReference !== null
    : control !== "externalHold" || v.externalNamespace === null || v.reservationProtocolReference === null) return invalid();
  return {
    location: parseStockLocation(v.location), physicalKey: key(v.physicalKey), label: text(v.label, 160), mode, control,
    maximumAgeMilliseconds: integer(v.maximumAgeMilliseconds, false, BigInt("922337203685477")),
    safetyBufferBaseUnits: integer(v.safetyBufferBaseUnits, true),
    externalNamespace: v.externalNamespace === null ? null : key(v.externalNamespace),
    reservationProtocolReference: v.reservationProtocolReference === null ? null : text(v.reservationProtocolReference, 160),
    discrepancyOwnerReference: text(v.discrepancyOwnerReference, 160), handoffReference: text(v.handoffReference, 160),
  };
}
function settings(value: unknown): components["schemas"]["StockSourceSetupProposeDto"] {
  return { ...parseSourceSettings(value), reason: text(record(value).reason, 1000) };
}
export { integer as sourceNumber, text as sourceText, time as sourceTime, freeze as freezeSource };
export function sourceActor(value: unknown): string { try { return id(value); } catch { return inputInvalid(); } }
export function sourceKey(value: unknown): string {
  if (typeof value !== "string" || !/^[A-Za-z0-9._:-]{1,128}$(?![\s\S])/.test(value)) return inputInvalid();
  return value;
}
export function sourceOperation(value: unknown): SourceOperation {
  if (value !== "stock.source.propose" && value !== "stock.source.approve" && value !== "stock.source.reject" && value !== "stock.source.withdraw") return inputInvalid();
  return value;
}
export function sourcePurpose(value: unknown): SourcePurpose {
  if (value !== "inventory-source-propose" && value !== "inventory-source-review" && value !== "inventory-source-history") return inputInvalid();
  return value;
}
export function sourceWarehouses(value: unknown) {
  try { return parseStockWarehouses(value); } catch { return invalid(); }
}
export function sourceLocation(value: unknown): AccessScope {
  try { return parseStockLocation(value); } catch { return inputInvalid(); }
}
export const sourceLocationKey = (value: AccessScope) => stockLocationKey(value);
export function sourceAgeSeconds(milliseconds: string): string {
  const value = BigInt(integer(milliseconds, false, BigInt("922337203685477")));
  return (value / BigInt("1000")).toString() + (value % BigInt("1000") ? "." + (value % BigInt("1000")).toString().padStart(3, "0").replace(/0+$/, "") : "");
}
export function prepareSourceProposal(actorId: string, location: AccessScope, draft: SourceDraft) {
  try {
    const body = settings({ ...draft, location: sourceLocation(location),
      maximumAgeMilliseconds: (BigInt(integer(draft.maximumAgeSeconds, false, BigInt("922337203685"))) * BigInt("1000")).toString(),
      physicalKey: draft.physicalKey.trim(), label: draft.label.trim(), externalNamespace: draft.externalNamespace.trim() || null,
      reservationProtocolReference: draft.reservationProtocolReference.trim() || null,
      discrepancyOwnerReference: draft.discrepancyOwnerReference.trim(), handoffReference: draft.handoffReference.trim(), reason: draft.reason.trim() });
    return freeze({ actorId: id(actorId), operation: "stock.source.propose" as const, body });
  } catch { return inputInvalid(); }
}
export type SourceProposalCommand = ReturnType<typeof prepareSourceProposal>;
export function parseSourceResult(value: unknown) {
  const v = record(value), status = v.status;
  if (v.proposalVersion !== "1" || !["proposed", "approved", "rejected", "withdrawn"].includes(status as string)) return invalid();
  const decisionId = v.decisionId === null ? null : id(v.decisionId), physicalPoolId = v.physicalPoolId === null ? null : id(v.physicalPoolId);
  if (status === "proposed" ? decisionId !== null || physicalPoolId !== null : decisionId === null || (status === "approved") !== (physicalPoolId !== null)) return invalid();
  return { proposalId: id(v.proposalId), proposalVersion: "1" as const,
    status: status as "proposed" | "approved" | "rejected" | "withdrawn", decisionId, physicalPoolId };
}
export type SourceResult = ReturnType<typeof parseSourceResult>;
export function parseSourceDetail(value: unknown) {
  try {
    const v = record(value), facts = settings(v), proposedAt = time(v.proposedAt), status = v.status;
    if (v.proposalVersion !== "1" || !["proposed", "approved", "rejected", "withdrawn"].includes(status as string)) return invalid();
    const physicalPoolId = v.physicalPoolId === null ? null : id(v.physicalPoolId);
    const decision = v.decision === null ? null : (() => {
      const d = record(v.decision);
      if (d.status !== status || d.status === "proposed") return invalid();
      const decidedAt = time(d.decidedAt);
      if (Date.parse(decidedAt) < Date.parse(proposedAt)) return invalid();
      return { status: d.status as "approved" | "rejected" | "withdrawn", decidedAt, reviewReference: text(d.reviewReference, 160), reason: text(d.reason, 1000) };
    })();
    if (status === "proposed" ? decision !== null || physicalPoolId !== null : decision === null || (status === "approved") !== (physicalPoolId !== null)) return invalid();
    return { ...facts, id: id(v.id), proposalVersion: "1" as const, status: status as SourceResult["status"], proposedAt, decision, physicalPoolId };
  } catch { return invalid(); }
}
export type SourceDetail = ReturnType<typeof parseSourceDetail>;
export function parseSourceHistory(value: unknown, location: AccessScope, offset = 0, limit = 25) {
  try {
    stockPageRequest(offset, limit);
    const v = record(value), total = integer(v.total, true);
    if (!Array.isArray(v.items) || v.items.length > limit) return invalid();
    const items = v.items.map(parseSourceDetail);
    if (new Set(items.map(item => item.id)).size !== items.length ||
      items.some(item => stockLocationKey(item.location) !== stockLocationKey(location)) ||
      items.length > 0 && BigInt(offset) + BigInt(items.length) > BigInt(total)) return invalid();
    return { total, items };
  } catch { return invalid(); }
}
export function prepareSourceDecision(actorId: string, proposal: SourceDetail, decision: SourceDecision, reviewReference: string, reason: string) {
  try {
    if (!["approve", "reject", "withdraw"].includes(decision)) return inputInvalid();
    const selected = parseSourceDetail(proposal);
    if (selected.status !== "proposed") return inputInvalid();
    return freeze({ actorId: id(actorId), operation: sourceOperation("stock.source." + decision), proposal: selected,
      proposalId: selected.id, expectedVersion: selected.proposalVersion,
      body: { reviewReference: text(reviewReference.trim(), 160), reason: text(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export type SourceDecisionCommand = ReturnType<typeof prepareSourceDecision>;
export type SourceCommand = SourceProposalCommand | SourceDecisionCommand;
const statusFor = (operation: SourceOperation): SourceResult["status"] => operation === "stock.source.propose" ? "proposed"
  : operation === "stock.source.approve" ? "approved" : operation === "stock.source.reject" ? "rejected" : "withdrawn";
export function sourceOutcome(value: unknown, command: SourceCommand): SourceResult {
  const result = parseSourceResult(value);
  if (result.status !== statusFor(command.operation) || "proposalId" in command && result.proposalId !== command.proposalId) return invalid();
  return result;
}
export function sourceReceipt(value: unknown, operation: SourceOperation): SourceResult | null {
  sourceOperation(operation);
  const v = record(value);
  if (v.state === "not_confirmed") return v.result === null ? null : invalid();
  if (v.state !== "committed") return invalid();
  const result = parseSourceResult(v.result);
  return result.status === statusFor(operation) ? result : invalid();
}
