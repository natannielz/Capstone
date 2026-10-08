import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { parseCatalogDefinition } from '../catalog/catalog-workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseOrderDelivery } from '../ordering/order-draft-client';
import {validateScanSelections, type OperationalScanSelection} from '../inventory/scan-client';

const invalid = (): never => { throw new AccessFailure(502, 'shipment_preparation_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function text(value: unknown, max: number) { return typeof value === 'string' && value.length > 0 && value.length <= max && value === value.trim() && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid(); }
function quantity(value: unknown, min = 1) { return typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= 1_000_000 ? value : invalid(); }
function time(value: unknown) { const result = text(value, 64); return Number.isFinite(Date.parse(result)) ? result : invalid(); }
function hash(value: unknown) { return typeof value === 'string' && /^[a-f0-9]{64}$(?![\s\S])/.test(value) ? value : invalid(); }
export function parseFulfillmentOrder(value: unknown) {
  const row = record(value), delivery = parseOrderDelivery(row.delivery);
  if (!delivery || typeof row.canPrepare !== 'boolean' || !Array.isArray(row.lines) || row.lines.length < 1 || row.lines.length > 100) return invalid();
  const lines = row.lines.map(value => {
    const item = record(value), skuId = priceId(item.skuId), definition = parseCatalogDefinition(item.definition), orderedPackages = quantity(item.orderedPackages), remainingPackages = quantity(item.remainingPackages, 0);
    const publicId = text(item.publicId, 36);
    if (definition.skuId !== skuId || remainingPackages > orderedPackages || !/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$(?![\s\S])/.test(publicId)) return invalid();
    return { skuId, publicId, definition, skuCode: text(item.skuCode, 64), productName: text(item.productName, 160), orderedPackages, remainingPackages };
  });
  if (new Set(lines.map(line => line.skuId)).size !== lines.length || row.canPrepare && !lines.some(line => line.remainingPackages > 0)) return invalid();
  return { orderId: priceId(row.orderId), suborderId: priceId(row.suborderId), revision: priceId(row.revision), review: hash(row.review),
    buyerName: text(row.buyerName, 200), delivery, lines, canPrepare: row.canPrepare, orderedAt: time(row.orderedAt) };
}
export type FulfillmentOrder = ReturnType<typeof parseFulfillmentOrder>;
export type PackageSelection = Readonly<{ skuId: string; packages: number }>;
function selections(value: unknown, source: FulfillmentOrder) {
  if (!Array.isArray(value) || value.length < 1 || value.length > 100) return invalid();
  const lines = value.map(value => { const row = record(value), skuId = priceId(row.skuId), packages = quantity(row.packages), accepted = source.lines.find(line => line.skuId === skuId);
    if (!accepted || packages > accepted.remainingPackages) return invalid(); return { skuId, packages }; });
  if (new Set(lines.map(line => line.skuId)).size !== lines.length) return invalid(); return lines;
}
export function parsePreparation(value: unknown) {
  if (value === null) return null;
  const row = record(value), source = parseFulfillmentOrder(row.acceptedSource), recordedAt = time(row.recordedAt);
  if (!source.canPrepare || typeof row.currentSourceMatches !== 'boolean' || Date.parse(recordedAt) < Date.parse(source.orderedAt)) return invalid();
  return { id: priceId(row.id), revision: priceId(row.revision), recordedAt, acceptedSource: source, lines: selections(row.lines, source), currentSourceMatches: row.currentSourceMatches };
}
export type Preparation = NonNullable<ReturnType<typeof parsePreparation>>;
export function parsePreparationResult(value: unknown) { const row = record(value); return { preparationId: priceId(row.preparationId), versionId: priceId(row.versionId), revision: priceId(row.revision),
  orderId: priceId(row.orderId), suborderId: priceId(row.suborderId), recordedAt: time(row.recordedAt) }; }
export type PreparationResult = ReturnType<typeof parsePreparationResult>;
export function prepareShipmentCommand(actorId: string, source: FulfillmentOrder, expectedRevision: string, lines: readonly PackageSelection[], reason: string,
  scanSelections?: readonly OperationalScanSelection[]) {
  try {
    const accepted = parseFulfillmentOrder(source);
    const scans = validateScanSelections(scanSelections, accepted.lines.filter(line => lines.some(item => item.skuId === line.skuId))
      .map(line => ({skuId: line.skuId, definitionId: line.definition.id, definitionVersion: line.definition.version})));
    if (!accepted.canPrepare || (expectedRevision !== '0' && priceId(expectedRevision) === '9223372036854775807')) return invalid();
    return Object.freeze({ actorId: priceId(actorId), orderId: accepted.orderId, suborderId: accepted.suborderId, expectedRevision,
      review: accepted.review, lines: Object.freeze(selections(lines, accepted).map(line => Object.freeze(line))), reason: text(reason, 1000), ...(scans ? {scanSelections: scans} : {}) });
  } catch { throw new AccessFailure(400, 'invalid_shipment_preparation'); }
}
export type PreparationCommand = ReturnType<typeof prepareShipmentCommand>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) { const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
export function createPreparationGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const before = beforeId === null ? null : priceId(beforeId);
      const row = record(data(await client.POST('/api/v1/fulfillment/preparations/sources/query', { body: { purpose: 'shipment-preparation', beforeId: before, limit: 25 }, params: { header: headers(actorId) }, signal })));
      if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean' || row.hasMore && row.items.length !== 25) return invalid();
      const items = row.items.map(parseFulfillmentOrder); let cursor = before === null ? null : BigInt(before);
      for (const item of items) { const id = BigInt(item.suborderId); if (!item.canPrepare || cursor !== null && id >= cursor) return invalid(); cursor = id; }
      return { items, hasMore: row.hasMore };
    },
    async source(actorId: string, suborderId: string, signal?: AbortSignal) {
      const result = parseFulfillmentOrder(data(await client.POST('/api/v1/fulfillment/preparations/sources/{suborderId}/query', { body: { purpose: 'shipment-preparation' }, params: { path: { suborderId: priceId(suborderId) }, header: headers(actorId) }, signal })));
      if (result.suborderId !== suborderId) return invalid(); return result;
    },
    async read(actorId: string, suborderId: string, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/fulfillment/preparations/{suborderId}/query', { body: { purpose: 'shipment-preparation' }, params: { path: { suborderId: priceId(suborderId) }, header: headers(actorId) }, signal })));
      const result = parsePreparation(row.preparation); if (result && result.acceptedSource.suborderId !== suborderId) return invalid(); return result;
    },
    async save(command: PreparationCommand, requestKey: string, isCurrent: () => boolean) {
      if (!isCurrent()) throw new RequestNotSentError(409, 'shipment_view_stale');
      const result = parsePreparationResult(data(await client.POST('/api/v1/fulfillment/preparations/{suborderId}', {
        body: { review: command.review, lines: command.lines.map(line => ({ ...line })), reason: command.reason, ...(command.scanSelections ? {scanSelections: [...command.scanSelections]} : {}) },
        params: { path: { suborderId: priceId(command.suborderId) }, header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"' + command.expectedRevision + '"' } } })));
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.orderId !== command.orderId || result.suborderId !== command.suborderId || BigInt(result.revision) !== BigInt(command.expectedRevision) + BigInt(1)) return invalid(); return result;
    },
    async recover(actorId: string, requestKey: string, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/fulfillment/preparations/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) }, signal })));
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed') return invalid(); return { state: 'committed' as const, result: parsePreparationResult(row.result) };
    },
  };
}
