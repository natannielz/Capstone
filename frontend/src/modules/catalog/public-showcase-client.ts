import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';

export type PublicShowcase = {
  assortmentId: string; ownerLabel: string; listedProducts: number; isPublic: boolean; revision: string;
  pending: { proposalId: string; makePublic: boolean; proposedBy: string; proposedAt: string; reason: string } | null;
};

const invalid = (): never => { throw new AccessFailure(502, 'catalog_public_response_invalid'); };
const id = (value: unknown) => typeof value === 'string' && /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) ? value : invalid();
const version = (value: unknown) => typeof value === 'string' && /^(0|[1-9][0-9]{0,18})$(?![\s\S])/.test(value) ? value : invalid();
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
const text = (value: unknown, limit: number) => typeof value === 'string' && value.trim() && value.length <= limit ? value : invalid();
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return result.data;
}

export function parsePublicShowcase(value: unknown): PublicShowcase {
  const row = record(value), owner = record(row.owner);
  const kind = owner.kind === 'fulfillmentShop' || owner.kind === 'fulfillmentWarehouse' ? owner.kind : invalid();
  const shop = owner.shopId === null || owner.shopId === undefined ? null : id(owner.shopId);
  const warehouse = owner.warehouseId === null || owner.warehouseId === undefined ? null : id(owner.warehouseId);
  const pending = row.pending === null || row.pending === undefined ? null : record(row.pending);
  const listed = Number(version(row.listedProducts));
  return { assortmentId: id(row.assortmentId), listedProducts: listed, isPublic: typeof row.isPublic === 'boolean' ? row.isPublic : invalid(),
    ownerLabel: row.ownerName === null || row.ownerName === undefined
      ? (shop ? 'Toko ' + shop : 'Organisasi ' + id(owner.organizationId)) + (kind === 'fulfillmentWarehouse' && warehouse ? ' · gudang ' + warehouse : '') + ' (nama belum tersedia)'
      : text(row.ownerName, 410),
    revision: version(row.revision),
    pending: pending && { proposalId: id(pending.proposalId), makePublic: typeof pending.makePublic === 'boolean' ? pending.makePublic : invalid(),
      proposedBy: id(pending.proposedBy), proposedAt: text(pending.proposedAt, 40), reason: text(pending.reason, 1000) } };
}

const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();

export function createPublicShowcaseGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, signal?: AbortSignal): Promise<PublicShowcase[]> {
      const result = record(data(await client.POST('/api/v1/catalog/public-showcase/query', {
        body: { purpose: 'catalog-public-showcase' }, params: { header: { 'X-Expected-Actor-Id': id(actorId) } }, signal })));
      return Array.isArray(result.items) && result.items.length <= 200 ? result.items.map(parsePublicShowcase) : invalid();
    },
    async propose(actorId: string, showcase: PublicShowcase, makePublic: boolean, reason: string, idempotencyKey: string) {
      return data(await client.POST('/api/v1/catalog/public-showcase/{assortmentId}/proposals', { body: { makePublic, reason: text(reason, 1000) },
        params: { path: { assortmentId: id(showcase.assortmentId) }, header: { 'X-Expected-Actor-Id': id(actorId), 'Idempotency-Key': key(idempotencyKey),
          'If-Match': '"' + showcase.revision + '"' } } }));
    },
    async decide(actorId: string, proposalId: string, approve: boolean, reason: string, idempotencyKey: string) {
      const route = approve ? '/api/v1/catalog/public-showcase/proposals/{proposalId}/approve' as const : '/api/v1/catalog/public-showcase/proposals/{proposalId}/reject' as const;
      return data(await client.POST(route, { body: { reason: text(reason, 1000) },
        params: { path: { proposalId: id(proposalId) }, header: { 'X-Expected-Actor-Id': id(actorId), 'Idempotency-Key': key(idempotencyKey), 'If-Match': '"1"' } } }));
    },
  };
}
