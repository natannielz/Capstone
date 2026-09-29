import { AccessFailure } from "../administration/workflow";
import type { AccessScope } from "../organizations/access-scope";
import { stockPageRequest } from "./stock-count-workflow";
import { sourceActor, sourceLocation, sourceLocationKey, sourceNumber, sourceText, sourceTime, freezeSource } from "./stock-source-workflow";

const invalid = (): never => { throw new AccessFailure(502, "stock_source_suspension_response_invalid"); };
const inputInvalid = (): never => { throw new AccessFailure(400, "stock_source_suspension_input_invalid"); };
function record(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
  const row = value as Record<string, unknown>;
  if (Object.keys(row).length !== keys.length || keys.some(key => !Object.hasOwn(row, key))) return invalid();
  return row;
}
function scope(value: unknown): AccessScope {
  record(value, ["kind", "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId"]);
  return sourceLocation(value);
}
function locations(value: unknown) {
  if (!Array.isArray(value) || value.length === 0) return invalid();
  const items = value.map(scope), first = items[0];
  if (items.some((item, index) => item.organizationId !== first.organizationId || item.shopId !== first.shopId ||
    item.warehouseId !== first.warehouseId || index > 0 && BigInt(items[index - 1].channelId!) >= BigInt(item.channelId!))) return invalid();
  return items;
}
export function parseSuspensionPreview(value: unknown) {
  try {
    const v = record(value, ["physicalPoolId", "poolVersion", "suspensionVersion", "active", "configurationHash", "affectedLocations"]);
    const suspensionVersion = sourceNumber(v.suspensionVersion, true);
    if (typeof v.active !== "boolean" || typeof v.configurationHash !== "string" ||
      !/^[0-9a-f]{64}$(?![\s\S])/.test(v.configurationHash) || suspensionVersion !== "0" && v.active) return invalid();
    return freezeSource({ physicalPoolId: sourceActor(v.physicalPoolId), poolVersion: sourceNumber(v.poolVersion), suspensionVersion,
      active: v.active, configurationHash: v.configurationHash, affectedLocations: locations(v.affectedLocations) });
  } catch { return invalid(); }
}
export type SuspensionPreview = ReturnType<typeof parseSuspensionPreview>;
export function prepareSourceSuspension(actorId: string, preview: unknown, reason: string) {
  try {
    const selected = parseSuspensionPreview(preview);
    if (selected.suspensionVersion === "9223372036854775807") return inputInvalid();
    return freezeSource({ actorId: sourceActor(actorId), operation: "stock.source.suspend" as const, preview: selected,
      poolId: selected.physicalPoolId, expectedVersion: selected.poolVersion,
      body: { expectedSuspensionVersion: selected.suspensionVersion, configurationHash: selected.configurationHash, reason: sourceText(reason.trim(), 1000) } });
  } catch { return inputInvalid(); }
}
export type SourceSuspensionCommand = ReturnType<typeof prepareSourceSuspension>;
export function parseSourceSuspension(value: unknown) {
  try {
    const v = record(value, ["id", "physicalPoolId", "poolVersion", "suspensionVersion", "physicalKey", "ownerLocation", "affectedLocations", "wasActive", "suspendedAt", "reason"]);
    const ownerLocation = scope(v.ownerLocation), affectedLocations = locations(v.affectedLocations), physicalKey = sourceText(v.physicalKey, 64);
    if (typeof v.wasActive !== "boolean" || !/^[a-z][a-z0-9._-]{0,63}$(?![\s\S])/.test(physicalKey) ||
      !affectedLocations.some(item => sourceLocationKey(item) === sourceLocationKey(ownerLocation))) return invalid();
    return freezeSource({ id: sourceActor(v.id), physicalPoolId: sourceActor(v.physicalPoolId), poolVersion: sourceNumber(v.poolVersion),
      suspensionVersion: sourceNumber(v.suspensionVersion), physicalKey, ownerLocation, affectedLocations, wasActive: v.wasActive,
      suspendedAt: sourceTime(v.suspendedAt), reason: sourceText(v.reason, 1000) });
  } catch { return invalid(); }
}
export type SourceSuspension = ReturnType<typeof parseSourceSuspension>;
export function parseSuspensionResult(value: unknown) {
  try {
    const v = record(value, ["suspensionId", "physicalPoolId", "poolVersion", "suspensionVersion", "status"]);
    if (v.status !== "suspended") return invalid();
    return freezeSource({ suspensionId: sourceActor(v.suspensionId), physicalPoolId: sourceActor(v.physicalPoolId),
      poolVersion: sourceNumber(v.poolVersion), suspensionVersion: sourceNumber(v.suspensionVersion), status: "suspended" as const });
  } catch { return invalid(); }
}
export function suspensionOutcome(value: unknown, command: SourceSuspensionCommand) {
  const result = parseSuspensionResult(value);
  if (result.physicalPoolId !== command.poolId || result.poolVersion !== command.expectedVersion ||
    BigInt(result.suspensionVersion) !== BigInt(command.body.expectedSuspensionVersion) + BigInt(1)) return invalid();
  return result;
}
export function suspensionReceipt(value: unknown) {
  const v = record(value, ["state", "result"]);
  if (v.state === "not_confirmed") return v.result === null ? null : invalid();
  return v.state === "committed" ? parseSuspensionResult(v.result) : invalid();
}
export function parseSourceSuspensions(value: unknown, poolId: string, offset = 0, limit = 25) {
  try {
    stockPageRequest(offset, limit); sourceActor(poolId);
    const v = record(value, ["total", "items"]), total = sourceNumber(v.total, true);
    const remaining = BigInt(total) - BigInt(offset), expected = remaining <= BigInt(0) ? 0 : remaining >= BigInt(limit) ? limit : Number(remaining);
    if (!Array.isArray(v.items) || v.items.length !== expected) return invalid();
    const items = v.items.map(parseSourceSuspension), ids = new Set<string>();
    for (const [index, item] of items.entries()) {
      if (item.physicalPoolId !== poolId || ids.has(item.id) || BigInt(item.suspensionVersion) !== BigInt(total) - BigInt(offset + index)) return invalid();
      ids.add(item.id);
      if (index > 0) {
        const newer = items[index - 1];
        if (BigInt(newer.poolVersion) < BigInt(item.poolVersion) || Date.parse(newer.suspendedAt) < Date.parse(item.suspendedAt) ||
          newer.physicalKey !== item.physicalKey || sourceLocationKey(newer.ownerLocation) !== sourceLocationKey(item.ownerLocation) ||
          item.affectedLocations.some(old => !newer.affectedLocations.some(current => sourceLocationKey(old) === sourceLocationKey(current)))) return invalid();
      }
    }
    return freezeSource({ total, items });
  } catch { return invalid(); }
}
