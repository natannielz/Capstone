import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {parseOrderPage} from './order-submit-client';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]);
const name = z.string().min(1).max(80).refine(value => value.trim() === value && !/\p{Cc}/u.test(value));
const context = z.object({organizationId: id, branchId: id, divisionId: id.nullable()}).strict();
const filter = z.object({context: context.nullable(), sort: z.enum(['newest', 'oldest'])}).strict();
const action = z.enum(['create', 'rename', 'remove']);
const time = z.string().datetime({offset: true}).refine(value => value.endsWith('Z') || value.endsWith('+00:00'));
const key = z.string().regex(/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/);
const view = z.object({id, revision: id, name, filter, updatedAt: time}).strict();
const result = z.object({viewId: id, versionId: id, revision: id, action, recordedAt: time}).strict();
const invalid = (): never => {throw new AccessFailure(502, 'order_view_response_invalid');};
function parse<T>(schema: z.ZodType<T>, value: unknown): T {const checked = schema.safeParse(value); return checked.success ? checked.data : invalid();}
export type OrderHistoryFilter = z.infer<typeof filter>;
export type SavedOrderView = z.infer<typeof view>;
export type OrderViewResult = z.infer<typeof result>;
export const defaultOrderHistoryFilter: OrderHistoryFilter = {context: null, sort: 'newest'};
export const orderViewContextKey = (value: OrderHistoryFilter['context']) => value === null ? '' : [value.organizationId, value.branchId, value.divisionId ?? ''].join(':');
export function parseOrderHistoryFilter(value: unknown) {return parse(filter, value);}
export function orderHistoryCursor(value: OrderHistoryFilter, cursorId: string) {
  const chosen = parse(filter, value), validId = parse(id, cursorId);
  return ['v1', chosen.sort, chosen.context?.organizationId ?? '0', chosen.context?.branchId ?? '0', chosen.context?.divisionId ?? '0', validId].join(':');
}
export function parseOrderHistoryCursor(value: OrderHistoryFilter, cursor: string | null) {
  if (cursor === null) return null;
  if (typeof cursor !== 'string' || cursor.length > 110 || cursor.split(':').length !== 6) return invalid();
  const owner = parse(id, cursor.split(':')[5]); return orderHistoryCursor(value, owner) === cursor ? owner : invalid();
}
/** Links contain supported query choices only. A recipient's account is resolved afresh by the server. */
export function orderHistorySharePath(value: OrderHistoryFilter) {
  const chosen = parse(filter, value), query = new URLSearchParams({ov: '1', sort: chosen.sort});
  if (chosen.context) {query.set('organization', chosen.context.organizationId); query.set('branch', chosen.context.branchId); if (chosen.context.divisionId) query.set('division', chosen.context.divisionId);}
  return '/ordering/orders?' + query.toString();
}
export function parseOrderHistoryShare(search: string): OrderHistoryFilter {
  const params = new URLSearchParams(search);
  if (params.size === 0) return {...defaultOrderHistoryFilter};
  const supported = ['ov', 'sort', 'organization', 'branch', 'division'];
  if ([...params.keys()].some(value => !supported.includes(value) || params.getAll(value).length !== 1) || params.get('ov') !== '1' ||
      !params.has('sort') || params.has('organization') !== params.has('branch') || params.has('division') && !params.has('branch')) throw new AccessFailure(400, 'invalid_order_view_link');
  const checked = filter.safeParse({context: params.has('branch') ? {organizationId: params.get('organization'), branchId: params.get('branch'), divisionId: params.get('division')} : null, sort: params.get('sort')});
  if (!checked.success) throw new AccessFailure(400, 'invalid_order_view_link'); return checked.data;
}
export type OrderViewCommand = {actorId: string; revision: string; body: {action: 'create' | 'rename' | 'remove'; viewId: string | null; name: string | null; filter: OrderHistoryFilter | null; reason: string}};
export function prepareOrderViewCommand(actorId: string, operation: 'create' | 'rename' | 'remove', current: SavedOrderView | null, enteredName: string, chosen: OrderHistoryFilter): OrderViewCommand {
  const saved = current === null ? null : parse(view, current);
  if (operation !== 'create' && saved === null) throw new RequestNotSentError(400, 'invalid_order_view');
  const command = {actorId: parse(id, actorId), revision: operation === 'create' ? '0' : saved!.revision,
    body: {action: operation, viewId: operation === 'create' ? null : saved!.id, name: operation === 'remove' ? null : parse(name, enteredName),
      filter: operation === 'create' ? parse(filter, chosen) : null, reason: 'Pemilik akun mengonfirmasi ' + ({create: 'penyimpanan', rename: 'perubahan nama', remove: 'penghapusan'}[operation]) + ' filter pesanan pribadi'}};
  return Object.freeze({...command, body: Object.freeze(command.body)});
}
export function parseOrderViewResult(value: unknown, expected?: {action: string; viewId: string | null; revision: string}) {
  const found = parse(result, value);
  if (expected && (found.action !== expected.action || expected.viewId !== null && found.viewId !== expected.viewId || BigInt(found.revision) !== BigInt(expected.revision) + BigInt(1))) return invalid();
  return found;
}
export function createOrderViewGateway(fetcher: typeof fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function post(path: string, actorId: string, body: unknown, signal?: AbortSignal, command?: {key: string; revision: string; current: () => boolean}) {
    const actor = parse(id, actorId), controller = new AbortController();
    const cancel = () => controller.abort(); signal?.addEventListener('abort', cancel, {once: true});
    if (signal?.aborted) controller.abort();
    const timeout = setTimeout(cancel, 30_000);
    try {
      const csrf = await fetchCsrfToken(csrfUrl, fetcher, controller.signal);
      if (command && !command.current()) throw new RequestNotSentError(409, 'order_view_changed');
      const response = await fetcher('/api/v1/ordering/order-views' + path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal,
        headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Expected-Actor-Id': actor,
          ...(command ? {'Idempotency-Key': parse(key, command.key), 'If-Match': '"' + parse(revision, command.revision) + '"'} : {})}, body: JSON.stringify(body)});
      // Once dispatched, stale session or malformed/lost response is an unknown outcome, never a known unsent command.
      if (command && !command.current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const value: unknown = await response.json();
      if (!response.ok) {const error = value && typeof value === 'object' ? value as Record<string, unknown> : {}; throw new AccessFailure(response.status, typeof error.code === 'string' ? error.code : 'request_failed');}
      return value;
    } finally {clearTimeout(timeout); signal?.removeEventListener('abort', cancel);}
  }
  return {
    async contexts(actorId: string, signal?: AbortSignal) {
      const label = z.string().min(1).max(200).refine(value => value.trim() === value && !/\p{Cc}/u.test(value)).nullable();
      const row = parse(z.object({items: z.array(z.object({context, organizationName: label, branchName: label, divisionName: label}).strict()).max(256)}).strict(), await post('/contexts/query', actorId, {purpose: 'order-history'}, signal));
      if (new Set(row.items.map(item => orderViewContextKey(item.context))).size !== row.items.length) return invalid(); return row.items;
    },
    async orders(actorId: string, chosen: OrderHistoryFilter, cursor: string | null, signal?: AbortSignal) {
      const query = parse(filter, chosen), boundary = parseOrderHistoryCursor(query, cursor);
      const raw = parse(z.object({items: z.array(z.unknown()).max(25), hasMore: z.boolean(), nextCursor: z.string().nullable()}).strict(), await post('/orders/query', actorId, {purpose: 'order-history', filter: query, cursor}, signal));
      const page = parseOrderPage(raw, boundary, query.sort);
      if (page.items.some(item => query.context !== null && orderViewContextKey(item.input.context) !== orderViewContextKey(query.context)) ||
        raw.nextCursor !== (page.hasMore ? orderHistoryCursor(query, page.items.at(-1)!.submission.orderId) : null)) return invalid();
      return {...page, nextCursor: raw.nextCursor};
    },
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const cursor = beforeId === null ? null : parse(id, beforeId);
      const page = parse(z.object({items: z.array(view).max(25), nextId: id.nullable()}).strict(), await post('/query', actorId, {purpose: 'order-history', beforeId: cursor}, signal));
      let previous = cursor === null ? null : BigInt(cursor);
      for (const item of page.items) {if (previous !== null && BigInt(item.id) >= previous) return invalid(); previous = BigInt(item.id);}
      if (page.nextId !== null && (cursor !== null && BigInt(page.nextId) >= BigInt(cursor) || page.items.some(item => BigInt(item.id) < BigInt(page.nextId!)))) return invalid(); return page;
    },
    async read(actorId: string, viewId: string, signal?: AbortSignal) {
      const expected = parse(id, viewId), found = parse(view, await post('/' + expected + '/query', actorId, {purpose: 'order-history'}, signal));
      return found.id === expected ? found : invalid();
    },
    async change(command: OrderViewCommand, originalKey: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'order_view_changed');
      return parseOrderViewResult(await post('', command.actorId, command.body, undefined, {key: originalKey, revision: command.revision, current}), {...command.body, revision: command.revision});
    },
    async recover(actorId: string, originalKey: string, signal?: AbortSignal) {
      const row = parse(z.object({status: z.enum(['not_confirmed', 'committed']), result: result.nullable()}).strict(), await post('/operations/query', actorId, {purpose: 'operation-recovery', key: parse(key, originalKey)}, signal));
      if (row.status === 'not_confirmed' && row.result === null) return null;
      if (row.status !== 'committed' || row.result === null) return invalid(); return row.result;
    },
  };
}
