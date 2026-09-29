import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { parseCatalogDefinition } from '../catalog/catalog-workflow';
import { parsePrice, priceId } from '../catalog/catalog-price-workflow';

const invalid = (): never => { throw new AccessFailure(502, 'order_quote_response_invalid'); };
function record(value: unknown): Record<string, unknown> { return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid(); }
function text(value: unknown, limit: number): string { return typeof value === 'string' && value.trim() === value && value.length > 0 && value.length <= limit && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid(); }
function money(value: unknown): string { return typeof value === 'string' && /^(0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) ? value : invalid(); }
const cents = (value: string) => BigInt(value.replace('.', ''));
export function parseOrderContext(value: unknown) {
  const row = record(value); return { organizationId: priceId(row.organizationId), branchId: priceId(row.branchId), divisionId: row.divisionId === null ? null : priceId(row.divisionId) };
}
export type OrderContext = ReturnType<typeof parseOrderContext>;
export type OrderContextOption = { context: OrderContext; organizationName: string | null; branchName: string | null; divisionName: string | null };
export type OrderSelection = { skuId: string; definitionId: string; definitionVersion: string; priceId: string; priceVersion: string; packages: number };
export const orderContextKey = (context: OrderContext) => [context.organizationId, context.branchId, context.divisionId ?? ''].join(':');
export function orderQuantity(value: string, minimum: number, increment: number) {
  if (!/^[1-9][0-9]{0,6}$(?![\s\S])/.test(value)) throw new AccessFailure(400, 'order_package_quantity_invalid');
  const quantity = Number(value);
  if (quantity > 1_000_000 || quantity < minimum || quantity % increment !== 0) throw new AccessFailure(400, 'order_package_quantity_invalid');
  return quantity;
}
function selection(value: OrderSelection) {
  const packages = orderQuantity(String(value.packages), 1, 1);
  return { skuId: priceId(value.skuId), definitionId: priceId(value.definitionId), definitionVersion: priceId(value.definitionVersion),
    priceId: priceId(value.priceId), priceVersion: priceId(value.priceVersion), packages };
}
export function parseOrderQuote(value: unknown, context: OrderContext, selected: OrderSelection[]) {
  const row = record(value), actualContext = parseOrderContext(row.context);
  if (orderContextKey(actualContext) !== orderContextKey(context) || !Array.isArray(row.lines) || row.lines.length !== selected.length || selected.length === 0) return invalid();
  const currency = text(row.currency, 3), catalogSubtotal = money(row.catalogSubtotal), calculatedAt = text(row.calculatedAt, 64);
  if (!/^[A-Z]{3}$/.test(currency) || !Number.isFinite(Date.parse(calculatedAt))) return invalid();
  const lines = row.lines.map((value, index) => {
    const line = record(value), expected = selected[index], definition = parseCatalogDefinition(line.definition), price = parsePrice(line.price);
    if (definition.skuId !== expected.skuId || definition.id !== expected.definitionId || definition.version !== expected.definitionVersion ||
      price.skuId !== expected.skuId || price.id !== expected.priceId || price.version !== expected.priceVersion || price.currency !== currency || line.packages !== expected.packages) return invalid();
    const baseUnits = priceId(line.baseUnits), subtotal = money(line.catalogSubtotal);
    if (BigInt(baseUnits) !== BigInt(expected.packages) * BigInt(definition.baseUnitsPerPackage) || cents(subtotal) !== cents(price.unitPrice) * BigInt(expected.packages)) return invalid();
    return { familyId: priceId(line.familyId), familyName: text(line.familyName, 160), skuCode: text(line.skuCode, 64), definition, price,
      packages: expected.packages, baseUnits, catalogSubtotal: subtotal };
  });
  if (new Set(lines.map(line => line.definition.skuId)).size !== lines.length || lines.reduce((sum, line) => sum + cents(line.catalogSubtotal), BigInt(0)) !== cents(catalogSubtotal)) return invalid();
  return { context: actualContext, calculatedAt, currency, catalogSubtotal, lines };
}
export type OrderQuote = ReturnType<typeof parseOrderQuote>;
export function orderAmount(currency: string, value: string) { const [whole, fraction] = money(value).split('.'); return currency + ' ' + BigInt(whole).toLocaleString('id-ID') + ',' + fraction; }
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  } return result.data;
}
export function createOrderQuoteGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async contexts(actorId: string, signal?: AbortSignal): Promise<OrderContextOption[]> {
      const result = record(data(await client.POST('/api/v1/ordering/contexts/query', { body: { purpose: 'order-preparation' },
        params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal })));
      if (!Array.isArray(result.items) || result.items.length > 256) return invalid();
      const items = result.items.map(value => { const row = record(value); return { context: parseOrderContext(row.context),
        organizationName: row.organizationName === null ? null : text(row.organizationName, 160), branchName: row.branchName === null ? null : text(row.branchName, 160),
        divisionName: row.divisionName === null ? null : text(row.divisionName, 160) }; });
      if (new Set(items.map(item => orderContextKey(item.context))).size !== items.length) return invalid(); return items;
    },
    async preview(actorId: string, context: OrderContext, selections: OrderSelection[], signal?: AbortSignal) {
      if (selections.length === 0 || selections.length > 100 || new Set(selections.map(line => line.skuId)).size !== selections.length) throw new AccessFailure(400, 'invalid_order_quote');
      const input = selections.map(selection), branch = parseOrderContext(context);
      return parseOrderQuote(data(await client.POST('/api/v1/ordering/quote/query', { body: { purpose: 'order-preparation', context: branch, selections: input },
        params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal })), branch, input);
    },
  };
}
