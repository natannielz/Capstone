import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { catalogText } from './catalog-editor-state';
import { catalogDescription, parseCatalogDefinition } from './catalog-workflow';
import { parsePrice, priceId } from './catalog-price-workflow';

const invalid = (): never => { throw new AccessFailure(502, 'buyer_catalog_response_invalid'); };
function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
}
function text(value: unknown, limit: number) {
  if (typeof value !== 'string') return invalid();
  try { return catalogText(value, limit) === value ? value : invalid(); } catch { return invalid(); }
}
function publicId(value: unknown) {
  return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/.test(value) ? value : invalid();
}
export function parseBuyerVariant(value: unknown) {
  const row = record(value), skuId = priceId(row.skuId), definition = parseCatalogDefinition(row.definition);
  const currentPrice = row.currentPrice === null ? null : parsePrice(row.currentPrice);
  if (definition.skuId !== skuId || currentPrice && currentPrice.skuId !== skuId) return invalid();
  if (typeof row.description !== 'string' || catalogDescription(row.description) !== row.description) return invalid();
  const code = text(row.code, 64); if (!/^[A-Z0-9._-]+$(?![\s\S])/.test(code)) return invalid();
  // Construct the buyer view explicitly. No staff rationale, source or identity fields are copied.
  return { familyId: priceId(row.familyId), familyPublicId: publicId(row.familyPublicId), familyName: text(row.familyName, 160),
    description: row.description, categoryCode: text(row.categoryCode, 64), skuId, publicId: publicId(row.publicId), code, definition, currentPrice };
}
export type BuyerVariant = ReturnType<typeof parseBuyerVariant>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return result.data;
}
export function createBuyerCatalogGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async query(actorId: string, search = '', offset = 0, signal?: AbortSignal) {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || search.length > 128) throw new AccessFailure(400, 'catalog_query_invalid');
      const row = record(data(await client.POST('/api/v1/catalog/variants/query', {
        body: { search: search.trim(), offset, limit: 25 }, params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      })));
      const total = row.total === '0' ? '0' : priceId(row.total);
      if (!Array.isArray(row.items) || row.items.length > 25) return invalid();
      const items = row.items.map(parseBuyerVariant);
      if (BigInt(total) < BigInt(items.length) || new Set(items.map(item => item.skuId)).size !== items.length ||
        items.some((item, index) => index > 0 && BigInt(item.skuId) <= BigInt(items[index - 1].skuId))) return invalid();
      return { total, items };
    },
    async variant(actorId: string, id: string, signal?: AbortSignal) {
      const row = data(await client.POST('/api/v1/catalog/variants/{publicId}/query', {
        body: {}, params: { path: { publicId: publicId(id) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      }));
      if (row === null) return null;
      const variant = parseBuyerVariant(row); return variant.publicId === id ? variant : invalid();
    },
  };
}
