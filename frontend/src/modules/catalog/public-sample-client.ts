import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';

/** One product (catalog family) as staff choose guest samples: whether guests see it now and any change waiting for review.
 * ownProposal says whether the viewer proposed that change; who proposed it never reaches the page. */
export type PublicSampleItem = {
  familyId: string; familyName: string; ownerLabel: string; packages: number; isSample: boolean;
  pending: { proposalId: string; adding: boolean; ownProposal: boolean } | null;
};
export type PublicSampleChange = { familyId: string; familyName: string };
export type PublicSampleProposal = {
  proposalId: string; ownProposal: boolean; ownerLabel: string; reason: string; proposedAt: string; adds: PublicSampleChange[]; removes: PublicSampleChange[];
};
export type PublicSamplePage = { items: PublicSampleItem[]; total: number; proposals: PublicSampleProposal[]; sampleCount: number; sampleLimit: number };
export type PublicSampleQuery = { search?: string; offset?: number; limit?: number };
export const publicSamplePageSize = 50;

const invalid = (): never => { throw new AccessFailure(502, 'catalog_public_response_invalid'); };
const id = (value: unknown) => typeof value === 'string' && /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) ? value : invalid();
const count = (value: unknown) => typeof value === 'string' && /^(0|[1-9][0-9]{0,14})$(?![\s\S])/.test(value) ? Number(value) : invalid();
const flag = (value: unknown) => typeof value === 'boolean' ? value : invalid();
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
const text = (value: unknown, limit: number) => typeof value === 'string' && value.trim() && value.length <= limit && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid();
const list = <T>(value: unknown, limit: number, parse: (entry: unknown) => T): T[] => Array.isArray(value) && value.length <= limit ? value.map(parse) : invalid();
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const problem = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return result.data;
}

const change = (value: unknown): PublicSampleChange => { const row = record(value); return { familyId: id(row.familyId), familyName: text(row.familyName, 160) }; };

export function parsePublicSamplePage(value: unknown): PublicSamplePage {
  const row = record(value);
  const items = list(row.items, 50, entry => {
    const item = record(entry);
    const pending = item.pending === null ? null : record(item.pending);
    return { familyId: id(item.familyId), familyName: text(item.familyName, 160), ownerLabel: text(item.ownerLabel, 440), packages: count(item.packages),
      isSample: flag(item.isSample), pending: pending && { proposalId: id(pending.proposalId), adding: flag(pending.adding), ownProposal: flag(pending.ownProposal) } };
  });
  const proposals = list(row.proposals, 50, entry => {
    const proposal = record(entry);
    return { proposalId: id(proposal.proposalId), ownProposal: flag(proposal.ownProposal), ownerLabel: text(proposal.ownerLabel, 440),
      reason: text(proposal.reason, 1000), proposedAt: text(proposal.proposedAt, 40), adds: list(proposal.adds, 200, change), removes: list(proposal.removes, 200, change) };
  });
  return { items, total: count(row.total), proposals, sampleCount: count(row.sampleCount), sampleLimit: count(row.sampleLimit) };
}

const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
const ids = (values: string[]) => [...new Set(values.map(id))].sort((left, right) => left.length - right.length || (left < right ? -1 : left > right ? 1 : 0));

export function createPublicSampleGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, query: PublicSampleQuery = {}, signal?: AbortSignal): Promise<PublicSamplePage> {
      const search = query.search?.trim() || null;
      return parsePublicSamplePage(data(await client.POST('/api/v1/catalog/public-samples/query', {
        body: { purpose: 'catalog-public-samples', search: search && search.slice(0, 64), offset: query.offset ?? 0, limit: query.limit ?? publicSamplePageSize },
        params: { header: { 'X-Expected-Actor-Id': id(actorId) } }, signal })));
    },
    /** One proposal for every toggled product; the checker approves or rejects it as a whole. */
    async propose(actorId: string, change: { adds: string[]; removes: string[] }, reason: string, idempotencyKey: string) {
      return data(await client.POST('/api/v1/catalog/public-samples/proposals', { body: { adds: ids(change.adds), removes: ids(change.removes), reason: text(reason, 1000) },
        params: { header: { 'X-Expected-Actor-Id': id(actorId), 'Idempotency-Key': key(idempotencyKey) } } }));
    },
    async decide(actorId: string, proposalId: string, approve: boolean, reason: string, idempotencyKey: string) {
      const route = approve ? '/api/v1/catalog/public-samples/proposals/{proposalId}/approve' as const : '/api/v1/catalog/public-samples/proposals/{proposalId}/reject' as const;
      return data(await client.POST(route, { body: { reason: text(reason, 1000) },
        params: { path: { proposalId: id(proposalId) }, header: { 'X-Expected-Actor-Id': id(actorId), 'Idempotency-Key': key(idempotencyKey) } } }));
    },
  };
}
