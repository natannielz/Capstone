import type { components } from "../../api/generated/schema";
import type { OperationReference, StockCountRecoveryReceipt } from "../administration/operation-journal";
import { AccessFailure, stableContent } from "../administration/workflow";
import { validReference, type AccessScope } from "../organizations/access-scope";

export const stockCountOperation = "inventory.stock-count.record" as const;
export type StockCountPurpose = "inventory-stock-count" | "inventory-stock-history";
const invalid = (): never => { throw new AccessFailure(502, "stock_count_response_invalid"); };
const inputInvalid = (): never => { throw new AccessFailure(400, "stock_count_input_invalid"); };
const record = (value: unknown): Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
const id = (value: unknown): string => typeof value === "string" && value === value.trim() && validReference(value) ? value : invalid();
const natural = (value: unknown): string => value === "0" ? value : id(value);
const integer = (value: unknown, minimum: number, maximum: number): number =>
  typeof value === "number" && Number.isInteger(value) && !Object.is(value, -0) && value >= minimum && value <= maximum ? value : invalid();
function canonicalText(value: unknown, maximum: number): string {
  if (typeof value !== "string" || !value.length || value.length > maximum || value.trim() !== value ||
    /[\u0000-\u001f\u007f-\u009f]/.test(value) || !value.isWellFormed()) return invalid();
  return value;
}
function inputText(value: string, maximum: number, optional = false): string | null {
  if (typeof value !== "string" || /[\u0000-\u001f\u007f-\u009f]/.test(value) || !value.isWellFormed()) return inputInvalid();
  const result = value.trim();
  if (optional && !result) return null;
  if (!result || result.length > maximum) return inputInvalid();
  return result;
}
function time(value: unknown): string {
  if (typeof value !== "string" || value.length > 40 ||
    !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$(?![\s\S])/.test(value) ||
    !Number.isFinite(Date.parse(value))) return invalid();
  const year = Number(value.slice(0, 4)), month = Number(value.slice(5, 7)), day = Number(value.slice(8, 10));
  const maximum = [31, year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  return year >= 1 && day >= 1 && day <= maximum && Number(value.slice(11, 13)) < 24 ? value : invalid();
}
function unique<T>(values: T[], key: (value: T) => string): T[] {
  return new Set(values.map(key)).size === values.length ? values : invalid();
}
function list<T>(value: unknown, maximum: number, parse: (item: unknown) => T): T[] {
  return Array.isArray(value) && value.length <= maximum ? value.map(parse) : invalid();
}
export function parseStockLocation(value: unknown): AccessScope {
  const v = record(value);
  if (v.kind !== "fulfillmentWarehouse" || v.branchId !== null || v.divisionId !== null) return invalid();
  return { kind: v.kind, organizationId: id(v.organizationId), branchId: null, divisionId: null,
    shopId: id(v.shopId), warehouseId: id(v.warehouseId), channelId: id(v.channelId) };
}
export const stockLocationKey = (value: AccessScope): string => stableContent(parseStockLocation(value));
export function parseStockWarehouses(value: unknown) {
  return unique(list(record(value).items, 256, item => {
    const v = record(item);
    return { location: parseStockLocation(v.location), organizationName: canonicalText(v.organizationName, 160),
      shopName: canonicalText(v.shopName, 160), warehouseName: canonicalText(v.warehouseName, 160), channelName: canonicalText(v.channelName, 160) };
  }), item => stockLocationKey(item.location));
}
export type StockWarehouse = ReturnType<typeof parseStockWarehouses>[number];
function packageFacts(value: unknown) {
  const v = record(value), kind = v.packageKind, code = canonicalText(v.skuCode, 64);
  if ((kind !== "loose" && kind !== "sealed" && kind !== "other") || !/^[A-Z0-9._-]+$(?![\s\S])/.test(code)) return invalid();
  return { skuId: id(v.skuId), skuCode: code, definitionId: id(v.definitionId), definitionVersion: id(v.definitionVersion),
    packageLabel: canonicalText(v.packageLabel, 160), packageKind: kind, baseUnitCode: canonicalText(v.baseUnitCode, 64),
    baseUnitsPerPackage: integer(v.baseUnitsPerPackage, 1, 2147483647) };
}
function packageChoice(value: unknown) {
  const v = record(value);
  return { ...packageFacts(v), skuRevision: id(v.skuRevision), familyName: canonicalText(v.familyName, 160) };
}
export type StockPackageChoice = ReturnType<typeof packageChoice>;
export function stockPageRequest(offset: number, limit: number) {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || !Number.isInteger(limit) || limit < 1 || limit > 100)
    return inputInvalid();
  return { offset, limit };
}
function page<T>(value: unknown, offset: number, limit: number, parse: (item: unknown) => T, key: (item: T) => string) {
  stockPageRequest(offset, limit);
  const v = record(value), total = natural(v.total), items = unique(list(v.items, limit, parse), key);
  if (items.length > 0 && BigInt(offset) + BigInt(items.length) > BigInt(total)) return invalid();
  return { total, items };
}
export function parseStockPackages(value: unknown, offset = 0, limit = 25) {
  return page(value, offset, limit, packageChoice, item => item.skuId);
}
export function stockCountQuantity(value: string, contents: number): { packages: number; baseUnits: string } {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]{0,6})$(?![\s\S])/.test(value) ||
    Number(value) > 1000000 || !Number.isInteger(contents) || contents < 1 || contents > 2147483647) return inputInvalid();
  return { packages: Number(value), baseUnits: (BigInt(value) * BigInt(contents)).toString() };
}
export function parseStockObservation(value: unknown) {
  const v = record(value);
  if (v.version !== "1" || v.baseline !== "unavailable" || v.coverage !== "single-observation") return invalid();
  const facts = packageFacts(v), packages = integer(v.packages, 0, 1000000), baseUnits = natural(v.baseUnits);
  if (BigInt(baseUnits) !== BigInt(packages) * BigInt(facts.baseUnitsPerPackage)) return invalid();
  return { id: id(v.id), version: "1" as const, location: parseStockLocation(v.location), recordedAt: time(v.recordedAt),
    ...facts, packages, baseUnits, observedLotLabel: v.observedLotLabel === null ? null : canonicalText(v.observedLotLabel, 160),
    reason: canonicalText(v.reason, 1000), baseline: "unavailable" as const, coverage: "single-observation" as const };
}
export type StockObservation = ReturnType<typeof parseStockObservation>;
export function parseStockHistory(value: unknown, location: AccessScope, offset = 0, limit = 25) {
  const result = page(value, offset, limit, parseStockObservation, item => item.id);
  if (result.items.some(item => stockLocationKey(item.location) !== stockLocationKey(location))) return invalid();
  return result;
}
export function stockCountReceipt(value: unknown, entry: OperationReference): StockCountRecoveryReceipt | null {
  if (entry.operation !== stockCountOperation) return inputInvalid();
  const v = record(value);
  if (v.state === "not_confirmed") {
    if (v.result !== null || v.recordedAt !== null) return invalid();
    return null;
  }
  if (v.state !== "committed") return invalid();
  const result = record(v.result);
  if (result.version !== "1") return invalid();
  return { state: "committed", result: { id: id(result.id), version: "1" }, recordedAt: time(v.recordedAt) };
}
export type StockCountDraft = { packages: string; reason: string; observedLotLabel: string };
export type StockCountCommand = Readonly<{
  actorId: string; operation: typeof stockCountOperation;
  body: components["schemas"]["StockCountRecordDto"]; choice: StockPackageChoice;
}>;
/** The review owns a detached, recursively frozen snapshot. Only references are persisted for recovery. */
export function prepareStockCount(actorId: string, location: AccessScope, choice: StockPackageChoice, draft: StockCountDraft): StockCountCommand {
  try {
    const actor = id(actorId), place = parseStockLocation(location), selected = packageChoice(choice);
    const quantity = stockCountQuantity(draft.packages, selected.baseUnitsPerPackage);
    const command: StockCountCommand = { actorId: actor, operation: stockCountOperation, choice: selected,
      body: { location: place, skuId: selected.skuId, expectedSkuRevision: selected.skuRevision, expectedDefinitionId: selected.definitionId,
        packages: quantity.packages, reason: inputText(draft.reason, 1000)!, observedLotLabel: inputText(draft.observedLotLabel, 160, true) } };
    Object.freeze(place); Object.freeze(selected); Object.freeze(command.body);
    return Object.freeze(command);
  } catch { return inputInvalid(); }
}
/** A success is useful only if it matches every reviewed count and immutable packaging fact. */
export function stockCountOutcome(value: unknown, command: StockCountCommand): StockObservation {
  const observation = parseStockObservation(value), body = command.body, choice = command.choice;
  if (stockLocationKey(observation.location) !== stockLocationKey(body.location) ||
    observation.skuId !== body.skuId || observation.definitionId !== body.expectedDefinitionId ||
    observation.packages !== body.packages || observation.reason !== body.reason ||
    observation.observedLotLabel !== body.observedLotLabel ||
    observation.skuCode !== choice.skuCode || observation.definitionVersion !== choice.definitionVersion ||
    observation.packageLabel !== choice.packageLabel || observation.packageKind !== choice.packageKind ||
    observation.baseUnitCode !== choice.baseUnitCode || observation.baseUnitsPerPackage !== choice.baseUnitsPerPackage) return invalid();
  return observation;
}
export function stockCountSearch(value: string): string { return inputText(value, 128, true) ?? ""; }
export function stockCountActor(value: string): string {
  if (typeof value !== "string" || value !== value.trim() || !validReference(value)) return inputInvalid();
  return value;
}
export function stockCountKey(value: string): string {
  if (typeof value !== "string" || !/^[A-Za-z0-9._:-]{1,128}$(?![\s\S])/.test(value)) return inputInvalid();
  return value;
}
