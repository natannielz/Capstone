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
    async query(actorId: string, search = '', offset = 0, signal?: AbortSignal) {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || search.length > 128) throw new AccessFailure(400, 'catalog_query_invalid');
      const row = record(data(await client.POST('/api/v1/catalog/variants/query', {
        body: { search: search.trim(), offset, limit: 25 }, params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      })));
      const total = row.total === '0' ? '0' : priceId(row.total);
      if (!Array.isArray(row.items) || row.items.length > 25) return invalid();
      const items = row.items.map(parseBuyerVariant);
      if (items.some(item => item.buyerIdentity === null)) return invalid();
      if (BigInt(total) < BigInt(items.length) || new Set(items.map(item => item.skuId)).size !== items.length ||
        items.some((item, index) => index > 0 && BigInt(item.skuId) <= BigInt(items[index - 1].skuId))) return invalid();
      return { total, items };
    },
    async variant(actorId: string, id: string, signal?: AbortSignal) {
      const row = data(await client.POST('/api/v1/catalog/variants/{publicId}/query', {
        body: {}, params: { path: { publicId: publicId(id) }, header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      }));
      if (row === null) return null;
      const variant = parseBuyerVariant(row); return variant.publicId === id && variant.buyerIdentity !== null ? variant : invalid();
    },
  };
}
