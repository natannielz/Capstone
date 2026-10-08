import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { parseBuyerCodeMatch } from './catalog-buyer-client';
import { priceId } from './catalog-price-workflow';

export function createBuyerBulkGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async exact(actorId: string, codes: readonly string[], signal?: AbortSignal) {
      if (codes.length < 1 || codes.length > 100 || new Set(codes).size !== codes.length ||
          codes.some(code => !/^[A-Z0-9._-]{1,64}$(?![\s\S])/.test(code))) throw new AccessFailure(400, 'invalid_bulk_sku_query');
      const response = await client.POST('/api/v1/catalog/variants/exact-skus/query', {
        body: { codes: [...codes] }, params: { header: { 'X-Expected-Actor-Id': priceId(actorId) } }, signal,
      });
      if (!response.response.ok || response.data === undefined) {
        const problem = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
        throw new AccessFailure(response.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
      }
      if (!Array.isArray(response.data) || response.data.length !== codes.length) throw new AccessFailure(502, 'buyer_catalog_response_invalid');
      const matches = response.data.map((item, index) => parseBuyerCodeMatch(item, codes[index]));
      if (new Set(matches.map(item => item.variant.skuId)).size !== matches.length ||
          new Set(matches.map(item => item.matchedIdentifier.identifierId)).size !== matches.length)
        throw new AccessFailure(502, 'buyer_catalog_response_invalid');
      return matches;
    },
  };
}
