import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { freezeCatalogAttempt } from '../catalog/catalog-editor-state';

const { id, hash } = paymentAllocationSchemas;
const version = id.refine(value => BigInt(value) < BigInt('9223372036854775807'));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.normalize('NFKC').trim() && !/[\p{Cc}\p{Cf}\p{Co}\p{Cs}]/u.test(value));
const code = z.string().regex(/^[A-Za-z][A-Za-z0-9._-]{0,63}$(?![\s\S])/);
const month = z.string().regex(/^(?!0000)[0-9]{4}-(?:0[1-9]|1[0-2])$(?![\s\S])/);
const time = z.string().datetime({ offset: true });
const key = z.object({ organizationId: id, shopId: id, channelId: id, currency: z.literal('IDR') }).strict()
  .refine(value => new Set([value.organizationId, value.shopId, value.channelId]).size === 3);
const label = z.object({ id, code: text(64), name: text(160), scopeVersion: version, profileVersion: version }).strict();
const mapping = z.object({ mappingKey: code, accountCode: code }).strict();
const mappings = z.array(mapping).min(1).max(1000).refine(values => new Set(values.map(value => value.mappingKey)).size === values.length);
const input = z.object({ key, initialMonth: month, mappings }).strict();
const owner = { key, organization: label, shop: label, channel: label };
const labelsMatch = (value: { key: z.infer<typeof key>; organization: z.infer<typeof label>; shop: z.infer<typeof label>; channel: z.infer<typeof label> }) =>
  value.key.organizationId === value.organization.id && value.key.shopId === value.shop.id && value.key.channelId === value.channel.id;
const target = z.object({ ...owner, configured: z.boolean() }).strict().refine(labelsMatch);
export const accountingBookKey = (value: z.infer<typeof key>) => [value.organizationId, value.shopId, value.channelId, value.currency].join(':');
const sameKey = (left: z.infer<typeof key>, right: z.infer<typeof key>) => accountingBookKey(left) === accountingBookKey(right);
const targets = z.object({ items: z.array(target).max(256) }).strict().refine(value => new Set(value.items.map(item => accountingBookKey(item.key))).size === value.items.length);
const preview = z.object({ input, binding: hash, observedAt: time, organization: label, shop: label, channel: label }).strict().refine(value => labelsMatch({ ...value, key: value.input.key }));
const account = z.object({ mappingKey: code, accountId: id, accountCode: code, active: z.literal(true) }).strict();
const decision = z.enum(['approve', 'reject']);
const decisionResult = z.object({ id, decision, decidedAt: time, bookId: id.nullable(), chartId: id.nullable(), periodId: id.nullable(), accounts: z.array(account).max(1000) }).strict();
const proposal = z.object({ id, version: z.literal('1'), ...owner, initialMonth: month, mappings, proposedAt: time,
  state: z.enum(['pending', 'approved', 'rejected']), decision: decisionResult.nullable() }).strict().refine(labelsMatch).refine(value => {
    if (value.state === 'pending') return value.decision === null;
    const result = value.decision;
    if (!result || Date.parse(result.decidedAt) < Date.parse(value.proposedAt)) return false;
    if (value.state === 'rejected') return result.decision === 'reject' && result.bookId === null && result.chartId === null && result.periodId === null && result.accounts.length === 0;
    return result.decision === 'approve' && result.bookId !== null && result.chartId !== null && result.periodId !== null && result.accounts.length === value.mappings.length &&
      new Set(result.accounts.map(item => item.mappingKey)).size === result.accounts.length && result.accounts.every(item => value.mappings.some(mapping => mapping.mappingKey === item.mappingKey && mapping.accountCode === item.accountCode)) &&
      result.accounts.every(item => result.accounts.every(other => (item.accountId === other.accountId) === (item.accountCode === other.accountCode)));
  });
const current = z.object({ key, configured: z.boolean(), setup: proposal.nullable() }).strict().refine(value => value.configured
  ? value.setup?.state === 'approved' && sameKey(value.key, value.setup.key) : value.setup === null);
const proposals = z.object({ key, items: z.array(proposal).max(25), nextProposalId: id.nullable() }).strict().refine(value =>
  value.items.every((item, index) => sameKey(item.key, value.key) && (index === 0 || BigInt(item.id) < BigInt(value.items[index - 1].id))) &&
  (value.nextProposalId === null || value.items.length > 0 && value.nextProposalId === value.items.at(-1)!.id));
const decisionPreview = z.object({ proposal, decision, binding: hash, observedAt: time }).strict().refine(value => value.proposal.state === 'pending' && Date.parse(value.observedAt) >= Date.parse(value.proposal.proposedAt));
const operation = z.enum(['accounting.book.propose', 'accounting.book.approve', 'accounting.book.reject']);
const result = z.object({ proposalId: id, proposalVersion: z.literal('1'), decisionId: id.nullable(), bookId: id.nullable(), chartId: id.nullable(), periodId: id.nullable(), status: z.enum(['pending', 'approved', 'rejected']) }).strict()
  .refine(value => value.status === 'pending' ? value.decisionId === null && value.bookId === null && value.chartId === null && value.periodId === null : value.status === 'rejected'
    ? value.decisionId !== null && value.bookId === null && value.chartId === null && value.periodId === null : value.decisionId !== null && value.bookId !== null && value.chartId !== null && value.periodId !== null);
const recovery = z.object({ status: z.enum(['confirmed', 'not_confirmed']), result: result.nullable() }).strict().refine(value => (value.status === 'confirmed') === (value.result !== null));
const invalid = (): never => { throw new AccessFailure(502, 'accounting_book_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function reviewedReason(value: string) { const checked = text(1000).safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'accounting_book_invalid'); return checked.data; }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) throw new AccessFailure(response.response.status, (response.error as { code?: string })?.code ?? 'request_failed'); return response.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const requireCurrent = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'accounting_book_view_stale'); };
export type AccountingBookKey = z.infer<typeof key>;
export type AccountingBookChoice = z.infer<typeof target>;
export type AccountingBookInput = z.infer<typeof input>;
export type AccountingBookPreview = z.infer<typeof preview>;
export type AccountingBookCurrent = z.infer<typeof current>;
export type AccountingBookProposal = z.infer<typeof proposal>;
export type AccountingBookDecision = z.infer<typeof decision>;
export type AccountingBookDecisionPreview = z.infer<typeof decisionPreview>;
export type AccountingBookResult = z.infer<typeof result>;
export function accountingBookInput(value: AccountingBookInput) {
  const checked = input.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'accounting_book_invalid');
  return { ...checked.data, mappings: [...checked.data.mappings].sort((a, b) => a.mappingKey < b.mappingKey ? -1 : a.mappingKey > b.mappingKey ? 1 : 0) };
}
export function parseAccountingBookTargets(value: unknown) { return parse(targets, value); }
export function parseAccountingBookCurrent(value: unknown, expected: AccountingBookKey) { const checked = parse(current, value); return sameKey(checked.key, expected) ? checked : invalid(); }
export function parseAccountingBookProposal(value: unknown, expectedId?: string) { const checked = parse(proposal, value); return expectedId === undefined || checked.id === expectedId ? checked : invalid(); }
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function accountingBookKeyOwner(value: string, requestedOperation: string) {
  parse(operation, requestedOperation);
  if (requestedOperation === 'accounting.book.propose') {
    const found = new RegExp(`^ab\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.IDR\\.(${uuid})$(?![\\s\\S])`).exec(value);
    if (!found || found[4] === '00000000-0000-0000-0000-000000000000') return invalid();
    return { book: parse(key, { organizationId: found[1], shopId: found[2], channelId: found[3], currency: 'IDR' }), proposalId: null };
  }
  const found = new RegExp(`^abd\\.(approve|reject)\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(value);
  if (!found || requestedOperation !== 'accounting.book.' + found[1] || found[3] === '00000000-0000-0000-0000-000000000000') return invalid();
  return { book: null, proposalId: parse(id, found[2]) };
}
export function parseAccountingBookResult(value: unknown, requestKey: string, requestedOperation: string) {
  const owner = accountingBookKeyOwner(requestKey, requestedOperation), checked = parse(result, value);
  if (checked.status !== (requestedOperation === 'accounting.book.propose' ? 'pending' : requestedOperation === 'accounting.book.approve' ? 'approved' : 'rejected') || owner.proposalId !== null && checked.proposalId !== owner.proposalId) return invalid(); return checked;
}
export function prepareAccountingBook(actorId: string, selected: AccountingBookChoice, sent: AccountingBookInput, shown: AccountingBookPreview, reason: string) {
  const owner = parse(target, selected), clean = accountingBookInput(sent), checked = parse(preview, shown);
  if (owner.configured || !sameKey(clean.key, owner.key) || JSON.stringify(clean) !== JSON.stringify(accountingBookInput(checked.input)) ||
    (['organization', 'shop', 'channel'] as const).some(field => JSON.stringify(owner[field]) !== JSON.stringify(checked[field]))) throw new RequestNotSentError(409, 'accounting_book_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), key: owner.key, body: { input: clean, binding: checked.binding, reason: reviewedReason(reason) } });
}
export function prepareAccountingBookDecision(actorId: string, source: AccountingBookProposal, shown: AccountingBookDecisionPreview, reason: string) {
  const owner = parse(proposal, source), checked = parse(decisionPreview, shown);
  if (owner.state !== 'pending' || JSON.stringify(owner) !== JSON.stringify(checked.proposal)) throw new RequestNotSentError(409, 'accounting_book_view_stale');
  return freezeCatalogAttempt({ actorId: parse(id, actorId), proposalId: owner.id, revision: owner.version, decision: checked.decision,
    body: { decision: checked.decision, binding: checked.binding, reason: reviewedReason(reason) } });
}
export function createAccountingBookGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async targets(actorId: string, signal?: AbortSignal) { return parseAccountingBookTargets(data(await client.POST('/api/v1/accounting/books/targets/query', { body: { purpose: 'accounting-book-review' }, params: { header: headers(actorId) }, signal }))); },
    async read(actorId: string, owner: AccountingBookKey, signal?: AbortSignal) { return parseAccountingBookCurrent(data(await client.POST('/api/v1/accounting/books/current/query', { body: { purpose: 'accounting-book-review', key: parse(key, owner) }, params: { header: headers(actorId) }, signal })), owner); },
    async proposals(actorId: string, owner: AccountingBookKey, beforeId: string | null = null, signal?: AbortSignal) {
      const checked = parse(proposals, data(await client.POST('/api/v1/accounting/books/proposals/query', { body: { purpose: 'accounting-book-review', key: parse(key, owner), beforeId }, params: { header: headers(actorId) }, signal })));
      if (!sameKey(checked.key, owner) || beforeId !== null && checked.items.some(item => BigInt(item.id) >= BigInt(parse(id, beforeId)))) return invalid(); return checked;
    },
    async proposal(actorId: string, proposalId: string, signal?: AbortSignal) { return parseAccountingBookProposal(data(await client.POST('/api/v1/accounting/books/proposals/{id}/query', { body: { purpose: 'accounting-book-review' }, params: { path: { id: parse(id, proposalId) }, header: headers(actorId) }, signal })), proposalId); },
    async preview(actorId: string, value: AccountingBookInput, signal?: AbortSignal) { return parse(preview, data(await client.POST('/api/v1/accounting/books/preview/query', { body: { purpose: 'accounting-book-review', input: accountingBookInput(value) }, params: { header: headers(actorId) }, signal }))); },
    async previewDecision(actorId: string, source: AccountingBookProposal, selected: AccountingBookDecision, signal?: AbortSignal) {
      const checked = parse(decisionPreview, data(await client.POST('/api/v1/accounting/books/proposals/{id}/decision-preview/query', { body: { purpose: 'accounting-book-review', decision: selected }, params: { path: { id: parse(id, source.id) }, header: headers(actorId) }, signal })));
      if (checked.proposal.id !== source.id || checked.decision !== selected) return invalid(); return checked;
    },
    async propose(command: ReturnType<typeof prepareAccountingBook>, requestKey: string, isCurrent: () => boolean) {
      const op = 'accounting.book.propose', owner = accountingBookKeyOwner(requestKey, op); if (!owner.book || !sameKey(owner.book, command.key)) return invalid(); requireCurrent(isCurrent);
      const saved = parseAccountingBookResult(data(await client.POST('/api/v1/accounting/books/proposals', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': requestKey } } })), requestKey, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async decide(command: ReturnType<typeof prepareAccountingBookDecision>, requestKey: string, isCurrent: () => boolean) {
      const op = 'accounting.book.' + command.decision, owner = accountingBookKeyOwner(requestKey, op); if (owner.proposalId !== command.proposalId) return invalid(); requireCurrent(isCurrent);
      const saved = parseAccountingBookResult(data(await client.POST('/api/v1/accounting/books/proposals/{id}/decisions', { body: command.body,
        params: { path: { id: command.proposalId }, header: { ...headers(command.actorId), 'If-Match': '"' + command.revision + '"', 'Idempotency-Key': requestKey } } })), requestKey, op);
      if (!isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async recover(actorId: string, requestedOperation: string, requestKey: string) {
      const owner = accountingBookKeyOwner(requestKey, requestedOperation), checked = parse(recovery, data(await client.POST('/api/v1/accounting/books/operations/query', { body: { purpose: 'operation-recovery', operation: requestedOperation, key: requestKey }, params: { header: headers(actorId) } })));
      if (checked.result === null) return null; const saved = parseAccountingBookResult(checked.result, requestKey, requestedOperation);
      if (owner.book) {
        const actual = parseAccountingBookProposal(data(await client.POST('/api/v1/accounting/books/proposals/{id}/query', { body: { purpose: 'accounting-book-review' }, params: { path: { id: saved.proposalId }, header: headers(actorId) } })), saved.proposalId);
        if (!sameKey(actual.key, owner.book)) return invalid();
      }
      return { state: 'committed' as const, result: saved };
    },
  };
}
