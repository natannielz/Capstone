import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { catalogText } from './catalog-editor-state';
import { catalogDescription, parseCatalogDefinition } from './catalog-workflow';
import { parsePrice, priceId } from './catalog-price-workflow';
import { normalizeCatalogCategory } from './catalog-history';

/** One category chip from the server: visible packages matching the current search in that category. */
export type BuyerCategoryCount = { code: string; name: string; count: number };
export type BuyerCatalogBrowse = { category?: string; sort?: 'name' };
function parseCategories(value: unknown): BuyerCategoryCount[] {
  // Servers before category browsing send no chips; anything else must be a bounded list of distinct, positive counts.
  if (value === undefined) return [];
  if (!Array.isArray(value) || value.length > 200) return invalid();
  const result = value.map(entry => {
    const row = record(entry), code = text(row.code, 64), count = priceId(row.count);
    if (normalizeCatalogCategory(code) !== code || !Number.isSafeInteger(Number(count)) || Number(count) < 1) return invalid();
    const name = text(row.name, 160); if (!name.trim()) return invalid();
    return { code, name, count: Number(count) };
  });
  return new Set(result.map(item => item.code)).size === result.length ? result : invalid();
}

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
  // Owner-facing historical DTOs may omit this projection. Never manufacture a current primary from stable code.
  const buyerIdentity = row.buyerIdentity == null ? null : parseBuyerIdentity(row.buyerIdentity, skuId, definition.id, definition.version);
  // Construct the buyer view explicitly. No staff rationale, supplier namespace or provenance is copied.
  return { familyId: priceId(row.familyId), familyPublicId: publicId(row.familyPublicId), familyName: text(row.familyName, 160),
    description: row.description, categoryCode: text(row.categoryCode, 64), skuId, publicId: publicId(row.publicId), code, definition, currentPrice, buyerIdentity };
}
function internalCode(value: unknown) {
  return typeof value === 'string' && /^[A-Z0-9._-]{1,64}$(?![\s\S])/.test(value) ? value : invalid();
}
function parseBuyerIdentity(value: unknown, skuId: string, definitionId: string, definitionVersion: string) {
  const row = record(value), primary = record(row.currentPrimary);
  if (priceId(row.skuId) !== skuId || priceId(row.definitionId) !== definitionId || priceId(row.definitionVersion) !== definitionVersion) return invalid();
  return { skuId, skuRevision: priceId(row.skuRevision), definitionId, definitionVersion,
    currentPrimary: { identifierId: priceId(primary.identifierId), identifierRevision: priceId(primary.identifierRevision), code: internalCode(primary.code) } };
}
export type BuyerVariant = ReturnType<typeof parseBuyerVariant>;
export function buyerCurrentCode(item: BuyerVariant) { return item.buyerIdentity?.currentPrimary.code ?? item.code; }
export function parseBuyerCodeMatch(value: unknown, requestedCode: string) {
  const row = record(value), binding = record(row.matchedIdentifier), variant = parseBuyerVariant(row.variant);
  const identity = variant.buyerIdentity;
  if (row.requestedCode !== requestedCode || internalCode(binding.code) !== requestedCode || !identity ||
      binding.kind !== 'skuCode' || binding.namespace !== 'internal' || priceId(binding.skuId) !== identity.skuId ||
      priceId(binding.skuRevision) !== identity.skuRevision || priceId(binding.definitionId) !== identity.definitionId ||
      priceId(binding.definitionVersion) !== identity.definitionVersion) return invalid();
  const matchedIdentifier = { identifierId: priceId(binding.identifierId), identifierRevision: priceId(binding.identifierRevision),
    code: requestedCode, skuId: identity.skuId, skuRevision: identity.skuRevision, definitionId: identity.definitionId,
    definitionVersion: identity.definitionVersion, kind: 'skuCode' as const, namespace: 'internal' as const };
  if ((matchedIdentifier.identifierId === identity.currentPrimary.identifierId || requestedCode === identity.currentPrimary.code) &&
      (matchedIdentifier.identifierId !== identity.currentPrimary.identifierId || matchedIdentifier.identifierRevision !== identity.currentPrimary.identifierRevision ||
       requestedCode !== identity.currentPrimary.code)) return invalid();
  return { requestedCode, matchedIdentifier, variant };
}
export type BuyerCodeMatch = ReturnType<typeof parseBuyerCodeMatch>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return result.data;
}
export function createBuyerCatalogGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async query(actorId: string, search = '', offset = 0, signal?: AbortSignal, browse: BuyerCatalogBrowse = {}) {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || search.length > 128) throw new AccessFailure(400, 'catalog_query_invalid');
      const category = browse.category === undefined ? undefined : normalizeCatalogCategory(browse.category);
      if (browse.category !== undefined && category === undefined || browse.sort !== undefined && browse.sort !== 'name') throw new AccessFailure(400, 'catalog_query_invalid');
      const row = record(data(await client.POST('/api/v1/catalog/variants/query', {
        body: { search: search.trim(), offset, limit: 25, ...(category ? { category } : {}), ...(browse.sort ? { sort: browse.sort } : {}) },
        params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      })));
      const total = row.total === '0' ? '0' : priceId(row.total);
      if (!Array.isArray(row.items) || row.items.length > 25) return invalid();
      const items = row.items.map(parseBuyerVariant);
      if (items.some(item => item.buyerIdentity === null)) return invalid();
      // The added order is ascending SKU id; alphabetical pages follow names, so only distinctness applies there.
      if (BigInt(total) < BigInt(items.length) || new Set(items.map(item => item.skuId)).size !== items.length ||
        !browse.sort && items.some((item, index) => index > 0 && BigInt(item.skuId) <= BigInt(items[index - 1].skuId))) return invalid();
      return { total, items, categories: parseCategories(row.categories) };
    },
    async variant(actorId: string, id: string, signal?: AbortSignal) {
      const row = data(await client.POST('/api/v1/catalog/variants/{publicId}/query', {
        body: {}, params: { path: { publicId: publicId(id) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      }));
      if (row === null) return null;
      const variant = parseBuyerVariant(row); return variant.publicId === id && variant.buyerIdentity !== null ? variant : invalid();
    },
    /** The cart's packages as the buyer may see them now. A package missing from the answer is no longer available. */
    async bySku(actorId: string, skuIds: readonly string[], signal?: AbortSignal): Promise<Map<string, BuyerVariant>> {
      const ids = skuIds.map(priceId);
      if (ids.length === 0 || ids.length > 100 || new Set(ids).size !== ids.length) throw new AccessFailure(400, 'invalid_cart_sku_query');
      const row = record(data(await client.POST('/api/v1/catalog/variants/by-sku/query', {
        body: { skuIds: ids }, params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal })));
      if (!Array.isArray(row.items) || row.items.length > ids.length) return invalid();
      const items = row.items.map(parseBuyerVariant), asked = new Set(ids);
      if (items.some(item => item.buyerIdentity === null || !asked.has(item.skuId)) || new Set(items.map(item => item.skuId)).size !== items.length) return invalid();
      return new Map(items.map(item => [item.skuId, item]));
    },
  };
}
