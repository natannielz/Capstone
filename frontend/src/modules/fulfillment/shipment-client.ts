import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseCatalogDefinition } from '../catalog/catalog-workflow';
import { parseOrderDelivery } from '../ordering/order-draft-client';

const invalid = (): never => { throw new AccessFailure(502, 'shipment_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function text(value: unknown, max: number) { return typeof value === 'string' && value.length > 0 && value.length <= max && value === value.trim() && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid(); }
export function parseShipment(value: unknown) {
  const row = record(value), delivery = parseOrderDelivery(row.delivery), numberYear = priceId(row.numberYear), dispatchedAt = text(row.dispatchedAt, 64);
  if (row.revision !== '1' || !delivery || !/^[1-9][0-9]{0,3}$(?![\s\S])/.test(numberYear) || !Number.isFinite(Date.parse(dispatchedAt)) ||
    !Array.isArray(row.lines) || row.lines.length < 1 || row.lines.length > 100) return invalid();
  const lines = row.lines.map(value => {
    const line = record(value), skuId = priceId(line.skuId), definition = parseCatalogDefinition(line.definition), packages = line.packages, baseUnits = priceId(line.baseUnits);
    if (definition.skuId !== skuId || typeof packages !== 'number' || !Number.isSafeInteger(packages) || packages < 1 || packages > 1_000_000 ||
      BigInt(baseUnits) !== BigInt(packages) * BigInt(definition.baseUnitsPerPackage)) return invalid();
    return { skuId, skuCode: text(line.skuCode, 64), productName: text(line.productName, 160), definition, packages, baseUnits };
  });
  if (new Set(lines.map(line => line.skuId)).size !== lines.length) return invalid();
  return { id: priceId(row.id), revision: '1' as const, orderId: priceId(row.orderId), suborderId: priceId(row.suborderId),
    numberEntity: priceId(row.numberEntity), numberChannel: priceId(row.numberChannel), numberYear, number: priceId(row.number), dispatchedAt,
    buyerName: text(row.buyerName, 200), delivery, courierId: priceId(row.courierId), courierName: text(row.courierName, 256), lines };
}
export type Shipment = ReturnType<typeof parseShipment>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  } return result.data;
}
export function createShipmentGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const before = beforeId === null ? null : priceId(beforeId);
      const row = record(data(await client.POST('/api/v1/fulfillment/shipments/query', { body: { purpose: 'shipment-read', beforeId: before, limit: 25 }, params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal })));
      if (!Array.isArray(row.items) || row.items.length > 25 || typeof row.hasMore !== 'boolean' || row.hasMore && row.items.length !== 25) return invalid();
      const items = row.items.map(parseShipment); let cursor = before === null ? null : BigInt(before);
      for (const item of items) { if (cursor !== null && BigInt(item.id) >= cursor) return invalid(); cursor = BigInt(item.id); }
      return { items, hasMore: row.hasMore };
    },
    async read(actorId: string, id: string, signal?: AbortSignal) {
      const row = parseShipment(data(await client.POST('/api/v1/fulfillment/shipments/{id}/query', { body: { purpose: 'shipment-read' }, params: { path: { id: priceId(id) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal })));
      if (row.id !== id) return invalid(); return row;
    },
  };
}
