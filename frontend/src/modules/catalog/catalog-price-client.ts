import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure, stableContent } from '../administration/workflow';
import type { OperationReference } from '../administration/operation-journal';
import { parsePricePreview, parsePriceProposal, parsePrice, parsePriceChoice, priceItems, priceId, priceRecord, priceInvalid, priceOutcome, priceAction,
  preparePriceProposal, preparePriceDecision, type PriceAccess, type PriceCommand } from './catalog-price-workflow';

function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed', typeof problem.traceId === 'string' ? problem.traceId : undefined);
  } return result.data;
}
const keyValue = (key: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(key) ? key : (() => { throw new AccessFailure(400, 'catalog_price_input_invalid'); })();
const headerFor = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const read = (access: PriceAccess) => ({ purpose: 'catalog-pricing', access });
export function createCatalogPriceGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async choices(actorId: string, access: PriceAccess, search = '', afterId: string | null = null, signal?: AbortSignal) {
      const rows = priceItems(data(await client.POST('/api/v1/catalog/prices/skus/query', { body: { ...read(access), search: search.trim(), afterId, limit: 25 }, params: { header: headerFor(actorId) }, signal })), parsePriceChoice, row => row.sku.id);
      if (rows.some((row, index) => BigInt(row.sku.id) <= BigInt(index ? rows[index - 1].sku.id : afterId ?? '0'))) return priceInvalid(); return rows;
    },
    async preview(actorId: string, skuId: string, access: PriceAccess, signal?: AbortSignal) {
      const result = parsePricePreview(data(await client.POST('/api/v1/catalog/prices/skus/{id}/preview/query', { body: read(access), params: { path: { id: priceId(skuId) }, header: headerFor(actorId) }, signal })));
      return result.sku.id === skuId ? result : priceInvalid();
    },
    async proposals(actorId: string, skuId: string, access: PriceAccess, beforeId: string | null = null, signal?: AbortSignal) {
      const rows = priceItems(data(await client.POST('/api/v1/catalog/prices/skus/{id}/proposals/query', { body: { ...read(access), beforeId, limit: 25 }, params: { path: { id: priceId(skuId) }, header: headerFor(actorId) }, signal })), parsePriceProposal, row => row.proposalId);
      if (rows.some((row, index) => row.skuId !== skuId || (index || beforeId !== null) && BigInt(row.proposalId) >= BigInt(index ? rows[index - 1].proposalId : beforeId!))) return priceInvalid(); return rows;
    },
    async history(actorId: string, skuId: string, access: PriceAccess, beforeVersion: string | null = null, signal?: AbortSignal) {
      const rows = priceItems(data(await client.POST('/api/v1/catalog/prices/skus/{id}/history/query', { body: { ...read(access), beforeVersion, limit: 25 }, params: { path: { id: priceId(skuId) }, header: headerFor(actorId) }, signal })), parsePrice, row => row.version);
      if (rows.some((row, index) => row.skuId !== skuId || (index || beforeVersion !== null) && BigInt(row.version) >= BigInt(index ? rows[index - 1].version : beforeVersion!))) return priceInvalid(); return rows;
    },
    async submit(command: PriceCommand, key: string, isCurrent: () => boolean = () => true) {
      const frozen = command.action === 'propose' ? preparePriceProposal(command.actorId, command.preview, command.review.currency, command.review.unitPrice, command.review.reason)
        : preparePriceDecision(command.actorId, command.proposal, command.action, command.review.reference, command.review.reason);
      if (stableContent(frozen) !== stableContent(command)) throw new AccessFailure(400, 'catalog_price_input_invalid');
      const header = { ...headerFor(frozen.actorId), 'Idempotency-Key': keyValue(key), 'If-Match': '"' + (frozen.action === 'propose' ? frozen.preview.expected.skuVersion : frozen.proposal.revision) + '"' };
      if (!isCurrent()) throw new RequestNotSentError(409, 'catalog_price_view_stale');
      let result: unknown;
      if (frozen.action === 'propose') result = data(await client.POST('/api/v1/catalog/prices/skus/{id}/proposals', { body: frozen.body, params: { path: { id: frozen.skuId }, header } }));
      else {
        const routes = { approve: '/api/v1/catalog/prices/proposals/{id}/approve', reject: '/api/v1/catalog/prices/proposals/{id}/reject', withdraw: '/api/v1/catalog/prices/proposals/{id}/withdraw' } as const;
        result = data(await client.POST(routes[frozen.action], { body: frozen.body, params: { path: { id: frozen.proposalId }, header } }));
      }
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      return priceOutcome(result, frozen.action, frozen.skuId, frozen.action === 'propose' ? undefined : frozen.proposalId);
    },
    async receipt(entry: OperationReference, skuId: string, signal?: AbortSignal) {
      const action = priceAction(entry.operation);
      const v = priceRecord(data(await client.POST('/api/v1/catalog/prices/skus/{id}/operations/query', { body: { purpose: 'operation-recovery', action, key: keyValue(entry.key) }, params: { path: { id: priceId(skuId) }, header: headerFor(entry.actorId) }, signal })));
      if (v.state === 'not_confirmed' && v.result === null) return null;
      if (v.state !== 'committed') return priceInvalid();
      return { state: 'committed' as const, result: priceOutcome(v.result, action, skuId) };
    },
  };
}
export type CatalogPriceGateway = ReturnType<typeof createCatalogPriceGateway>;
