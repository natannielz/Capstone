import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseFulfillmentOrder, type FulfillmentOrder, type PackageSelection } from './preparation-client';
import {validateScanSelections} from '../inventory/scan-client';

const invalid = (): never => { throw new AccessFailure(502, 'shipment_dispatch_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function text(value: unknown, max: number) { return typeof value === 'string' && value.length > 0 && value.length <= max && value === value.trim() && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid(); }
function quantity(value: unknown, min = 1) { return typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= 1_000_000 ? value : invalid(); }
function hash(value: unknown) { return typeof value === 'string' && /^[a-f0-9]{64}$(?![\s\S])/.test(value) ? value : invalid(); }
function time(value: unknown) { const result = text(value, 64); return Number.isFinite(Date.parse(result)) ? result : invalid(); }
function packages(value: unknown, source: FulfillmentOrder, current: boolean): PackageSelection[] {
  if (!Array.isArray(value) || value.length < 1 || value.length > 100) return invalid();
  const result = value.map(item => { const row = record(item), skuId = priceId(row.skuId), amount = quantity(row.packages), line = source.lines.find(line => line.skuId === skuId);
    if (!line || amount > (current ? line.remainingPackages : line.orderedPackages)) return invalid(); return { skuId, packages: amount }; });
  if (new Set(result.map(line => line.skuId)).size !== result.length) return invalid(); return result;
}
export function parseDispatchQueueItem(value: unknown) {
  const row = record(value), source = parseFulfillmentOrder(row.source);
  if (typeof row.canDispatch !== 'boolean' || row.canDispatch && !source.canPrepare) return invalid();
  return { preparationId: priceId(row.preparationId), preparationRevision: priceId(row.preparationRevision), source,
    lines: packages(row.lines, source, row.canDispatch), canDispatch: row.canDispatch };
}
export type DispatchQueueItem = ReturnType<typeof parseDispatchQueueItem>;
export function parseDispatchPreparation(value: unknown) {
  const row = record(value), source = parseFulfillmentOrder(row.source), lines = packages(row.lines, source, true);
  if (!source.canPrepare || !Array.isArray(row.lots) || row.lots.length < 1 || row.lots.length > 100) return invalid();
  const lots = row.lots.map(value => {
    const item = record(value), skuId = priceId(item.skuId), definitionId = priceId(item.definitionId), line = source.lines.find(line => line.skuId === skuId);
    if (!line || line.definition.id !== definitionId) return invalid();
    const expiresOn = item.expiresOn === null ? null : text(item.expiresOn, 10);
    if (expiresOn !== null && (!/^\d{4}-\d{2}-\d{2}$(?![\s\S])/.test(expiresOn) || !Number.isFinite(Date.parse(expiresOn)) || new Date(expiresOn).toISOString().slice(0, 10) !== expiresOn)) return invalid();
    return { reservationId: priceId(item.reservationId), skuId, definitionId, lotId: priceId(item.lotId), remainingPackages: quantity(item.remainingPackages, 0), expiresOn };
  });
  if (new Set(lots.map(lot => lot.reservationId)).size !== lots.length || source.lines.some(line => lots.filter(lot => lot.skuId === line.skuId).reduce((sum, lot) => sum + lot.remainingPackages, 0) !== line.remainingPackages)) return invalid();
  return { preparationId: priceId(row.preparationId), preparationRevision: priceId(row.preparationRevision), source, lines, lots };
}
export type DispatchPreparation = ReturnType<typeof parseDispatchPreparation>;
export type LotSelection = Readonly<{ reservationId: string; packages: number }>;
function selectedLots(value: unknown, prepared: DispatchPreparation): LotSelection[] {
  if (!Array.isArray(value) || value.length < 1 || value.length > 100) return invalid();
  const selected = value.map(value => { const row = record(value), reservationId = priceId(row.reservationId), amount = quantity(row.packages);
    const lot = prepared.lots.find(lot => lot.reservationId === reservationId);
    if (!lot || amount > lot.remainingPackages || !prepared.lines.some(line => line.skuId === lot.skuId)) return invalid(); return { reservationId, packages: amount }; });
  if (new Set(selected.map(lot => lot.reservationId)).size !== selected.length || prepared.lines.some(line => selected.filter(lot =>
    prepared.lots.find(item => item.reservationId === lot.reservationId)?.skuId === line.skuId).reduce((sum, lot) => sum + lot.packages, 0) !== line.packages)) return invalid();
  return selected;
}
export function parseDispatchReview(value: unknown) {
  const row = record(value), preparation = parseDispatchPreparation(row.preparation);
  return { preparation, courierId: priceId(row.courierId), courierName: text(row.courierName, 256), selected: selectedLots(row.selected, preparation), review: hash(row.review) };
}
export type DispatchReview = ReturnType<typeof parseDispatchReview>;
export function parseDispatchResult(value: unknown) {
  const row = record(value), revision = priceId(row.revision), numberYear = priceId(row.numberYear);
  if (revision !== '1' || !/^[1-9][0-9]{0,3}$(?![\s\S])/.test(numberYear)) return invalid();
  return { shipmentId: priceId(row.shipmentId), revision, orderId: priceId(row.orderId), suborderId: priceId(row.suborderId), recordedAt: time(row.recordedAt),
    numberEntity: priceId(row.numberEntity), numberChannel: priceId(row.numberChannel), numberYear, number: priceId(row.number) };
}
export type DispatchResult = ReturnType<typeof parseDispatchResult>;
export function prepareDispatchCommand(actorId: string, review: DispatchReview, reason: string, scanSelections?: readonly import('../inventory/scan-client').OperationalScanSelection[]) {
  try {
    const accepted = parseDispatchReview(review);
    const scans = validateScanSelections(scanSelections, accepted.preparation.source.lines.filter(line => accepted.selected.some(selected =>
      accepted.preparation.lots.some(lot => lot.reservationId === selected.reservationId && lot.skuId === line.skuId)))
      .map(line => ({skuId: line.skuId, definitionId: line.definition.id, definitionVersion: line.definition.version})));
    return Object.freeze({ actorId: priceId(actorId), orderId: accepted.preparation.source.orderId, suborderId: accepted.preparation.source.suborderId,
      selection: Object.freeze({ preparationId: accepted.preparation.preparationId, preparationRevision: accepted.preparation.preparationRevision,
        courierId: accepted.courierId, lots: Object.freeze(accepted.selected.map(lot => Object.freeze({ ...lot }))) }), review: accepted.review, reason: text(reason, 1000), ...(scans ? {scanSelections: scans} : {}) });
  } catch { throw new AccessFailure(400, 'invalid_shipment_dispatch_request'); }
}
export type DispatchCommand = ReturnType<typeof prepareDispatchCommand>;
export type CourierChoice = { userId: string; displayName: string };
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) { const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
function page<T>(value: unknown, beforeId: string | null, parse: (item: unknown) => T, id: (item: T) => string) {
  const row = record(value); if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean' || row.hasMore && row.items.length !== 25) return invalid();
  const items = row.items.map(parse); let cursor = beforeId === null ? null : BigInt(priceId(beforeId));
  for (const item of items) { const next = BigInt(id(item)); if (cursor !== null && next >= cursor) return invalid(); cursor = next; } return { items, hasMore: row.hasMore };
}
export function createDispatchGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      return page(data(await client.POST('/api/v1/fulfillment/dispatches/query', { body: { purpose: 'shipment-dispatch', beforeId, limit: 25 }, params: { header: headers(actorId) }, signal })), beforeId, parseDispatchQueueItem, item => item.preparationId);
    },
    async read(actorId: string, suborderId: string, signal?: AbortSignal) {
      const row = parseDispatchPreparation(data(await client.POST('/api/v1/fulfillment/dispatches/{suborderId}/query', { body: { purpose: 'shipment-dispatch' }, params: { path: { suborderId: priceId(suborderId) }, header: headers(actorId) }, signal })));
      if (row.source.suborderId !== suborderId) return invalid(); return row;
    },
    async couriers(actorId: string, suborderId: string, beforeId: string | null = null, signal?: AbortSignal) {
      return page(data(await client.POST('/api/v1/fulfillment/dispatches/{suborderId}/couriers/query', { body: { purpose: 'shipment-dispatch', beforeId, limit: 25 }, params: { path: { suborderId: priceId(suborderId) }, header: headers(actorId) }, signal })), beforeId,
        value => { const row = record(value); return { userId: priceId(row.userId), displayName: text(row.displayName, 256) }; }, item => item.userId);
    },
    async preview(actorId: string, prepared: DispatchPreparation, courierId: string, lots: readonly LotSelection[]) {
      let selection; try { selection = { preparationId: priceId(prepared.preparationId), preparationRevision: priceId(prepared.preparationRevision), courierId: priceId(courierId), lots: selectedLots(lots, prepared) }; }
      catch { throw new AccessFailure(400, 'shipment_picked_lots_changed'); }
      const result = parseDispatchReview(data(await client.POST('/api/v1/fulfillment/dispatches/{suborderId}/preview/query', { body: { purpose: 'shipment-dispatch', selection }, params: { path: { suborderId: prepared.source.suborderId }, header: headers(actorId) } })));
      if (result.preparation.preparationId !== selection.preparationId || result.preparation.preparationRevision !== selection.preparationRevision || result.courierId !== courierId ||
        result.preparation.source.orderId !== prepared.source.orderId || result.preparation.source.suborderId !== prepared.source.suborderId || result.preparation.source.review !== prepared.source.review ||
        result.selected.length !== lots.length || result.selected.some(lot => !lots.some(chosen => chosen.reservationId === lot.reservationId && chosen.packages === lot.packages))) return invalid();
      return result;
    },
    async dispatch(command: DispatchCommand, requestKey: string, isCurrent: () => boolean) {
      if (!isCurrent()) throw new RequestNotSentError(409, 'shipment_view_stale');
      const result = parseDispatchResult(data(await client.POST('/api/v1/fulfillment/dispatches/{suborderId}', { body: { selection: { ...command.selection, lots: command.selection.lots.map(lot => ({ ...lot })) }, review: command.review, reason: command.reason, ...(command.scanSelections ? {scanSelections: [...command.scanSelections]} : {}) },
        params: { path: { suborderId: command.suborderId }, header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"' + command.selection.preparationRevision + '"' } } })));
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.orderId !== command.orderId || result.suborderId !== command.suborderId) return invalid(); return result;
    },
    async recover(actorId: string, requestKey: string, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/fulfillment/dispatches/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) }, signal })));
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed') return invalid(); return { state: 'committed' as const, result: parseDispatchResult(row.result) };
    },
  };
}
