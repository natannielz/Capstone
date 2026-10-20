import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {InvoiceReleaseReference} from './invoice-release-journal';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]).refine(value => BigInt(value) < BigInt('9223372036854775806'));
const lineRevision = revision.refine(value => value === '0' || BigInt(value) >= BigInt(2) && BigInt(value) <= BigInt(103));
const money = z.string().regex(/^(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?$(?![\s\S])/);
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const text = z.string().min(1).max(1000).refine(value => value.trim() === value && !/\p{Cc}/u.test(value));
const day = z.string().regex(/^\d{4}-\d{2}-\d{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const time = z.string().datetime({offset: true}).refine(value => value.endsWith('Z') || value.endsWith('+00:00'));
const action = z.enum(['propose', 'approve', 'reject']);
const selected = z.array(z.object({shipmentId: id, receiptId: id}).strict()).min(1).max(20)
  .refine(values => new Set(values.map(value => value.shipmentId)).size === values.length && new Set(values.map(value => value.receiptId)).size === values.length);
const mappings = z.object({receivable: text, revenue: text, tax: text, charges: text}).strict()
  .refine(value => new Set(Object.values(value)).size === 4);
const allocation = z.object({receiptId: id, skuId: id, lineRevision, tax: money, charges: money}).strict();
const lineKey = (value: {receiptId: string; skuId: string; lineRevision: string}) => [value.receiptId, value.skuId, value.lineRevision].join(':');
const input = z.object({receipts: selected, expectedReleaseRevision: revision, periodFrom: day, periodTo: day, validFrom: time, validUntil: time,
  templateApprovalReference: text, numberingApprovalReference: text, eligibilityReviewReference: text, mappings, allocations: z.array(allocation).min(1).max(100)}).strict()
  .refine(value => value.periodFrom <= value.periodTo && Date.parse(value.periodTo) - Date.parse(value.periodFrom) <= 366 * 86400000 &&
    Date.parse(value.validUntil) > Date.parse(value.validFrom) && new Set(value.allocations.map(lineKey)).size === value.allocations.length &&
    value.allocations.every(line => value.receipts.some(receipt => receipt.receiptId === line.receiptId)));
const line = z.object({orderId: id, shipmentId: id, receiptId: id, skuId: id, lineRevision, skuCode: text, productName: text, packageLabel: text,
  acceptedBaseUnits: id, unitPrice: money, net: money, tax: money.nullable(), charges: money.nullable()}).strict();
const budget = z.object({orderId: id, approvedTax: money, approvedCharges: money, postedTax: money, postedCharges: money}).strict();
const account = z.object({role: z.enum(['receivable', 'revenue', 'tax', 'charges']), mappingKey: text, accountId: id, accountCode: text}).strict();
const facts = z.object({buyerName: text, sellerName: text, grouping: z.enum(['buying_org_period_channel', 'buying_branch_period_channel', 'receipt']),
  termOrigin: z.enum(['issue_date', 'earliest_receipt', 'latest_receipt']), termDays: z.number().int().min(0).max(365), dayRule: z.enum(['calendar_days', 'working_days']),
  rounding: z.enum(['half_even', 'half_up']), taxDocumentProcess: text, numberingProcedure: text, reissueProcedure: text,
  workingDays: z.array(z.number().int().min(0).max(6)).max(7), holidays: z.array(day), accountingPeriod: z.string().regex(/^\d{4}-\d{2}$(?![\s\S])/).nullable(),
  accounts: z.array(account).max(4), lines: z.array(line).min(1).max(100), budgets: z.array(budget).min(1).max(20)}).strict()
  .refine(value => new Set(value.lines.map(lineKey)).size === value.lines.length && new Set(value.budgets.map(item => item.orderId)).size === value.budgets.length &&
    value.lines.every(item => value.budgets.some(b => b.orderId === item.orderId)) && value.budgets.every(b => value.lines.some(item => item.orderId === b.orderId)) &&
    new Set(value.workingDays).size === value.workingDays.length && new Set(value.holidays).size === value.holidays.length &&
    new Set(value.accounts.map(item => item.role)).size === value.accounts.length && new Set(value.accounts.map(item => item.mappingKey)).size === value.accounts.length &&
    (value.accountingPeriod === null ? value.accounts.length === 0 : value.accounts.length === 4));
const eligibility = z.object({revision, selectionKey: hash, releaseId: id.nullable(), state: action.nullable(), facts}).strict()
  .refine(value => value.revision === '0' ? value.releaseId === null && value.state === null : value.releaseId !== null && value.state !== null);
const preview = z.object({binding: hash, selectionKey: hash, revision, input, facts}).strict();
const result = z.object({releaseId: id, recordId: id, proposalId: id, revision: id, action, selectionKey: hash}).strict()
  .refine(value => value.action === 'propose' ? value.proposalId === value.recordId : value.proposalId !== value.recordId);
const view = z.object({result, actorId: id, recordedAt: time, reason: text, input, facts, recordHash: hash}).strict();
const candidate = z.object({shipmentId: id, receiptId: id, receivedAt: time, buyerName: text, acceptedLines: z.number().int().min(1).max(100), invoiced: z.boolean()}).strict();
const requestKey = z.string().regex(/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/);
const invalid = (): never => {throw new AccessFailure(502, 'invoice_release_response_invalid');};
function parse<T>(schema: z.ZodType<T>, value: unknown): T {const checked = schema.safeParse(value); return checked.success ? checked.data : invalid();}
function entered<T>(schema: z.ZodType<T>, value: unknown): T {const checked = schema.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'invoice_release_invalid'); return checked.data;}
export type InvoiceReleaseInput = z.infer<typeof input>;
export type InvoiceReleaseFacts = z.infer<typeof facts>;
export type InvoiceReleaseEligibility = z.infer<typeof eligibility>;
export type InvoiceReleasePreview = z.infer<typeof preview>;
export type InvoiceReleaseView = z.infer<typeof view>;
export type InvoiceReleaseResult = z.infer<typeof result>;
export type InvoiceReleaseSelection = z.infer<typeof selected>;
export type InvoiceReleaseCandidate = z.infer<typeof candidate>;
const selectionSignature = (value: InvoiceReleaseSelection) => [...value].sort((left, right) => BigInt(left.shipmentId) < BigInt(right.shipmentId) ? -1 : 1).map(value => value.shipmentId + ':' + value.receiptId).join('|');
function sameSources(value: InvoiceReleaseFacts, receipts: InvoiceReleaseSelection) {
  return value.lines.every(line => receipts.some(receipt => receipt.shipmentId === line.shipmentId && receipt.receiptId === line.receiptId)) &&
    receipts.every(receipt => value.lines.some(line => receipt.shipmentId === line.shipmentId && receipt.receiptId === line.receiptId));
}
const cents = (value: string) => {const [whole, decimal = ''] = value.split('.'); return BigInt(whole) * BigInt(100) + BigInt(decimal.padEnd(2, '0'));};
function sameInput(left: InvoiceReleaseInput, right: InvoiceReleaseInput) {
  return selectionSignature(left.receipts) === selectionSignature(right.receipts) &&
    ['expectedReleaseRevision', 'periodFrom', 'periodTo', 'templateApprovalReference', 'numberingApprovalReference', 'eligibilityReviewReference'].every(key => left[key as keyof InvoiceReleaseInput] === right[key as keyof InvoiceReleaseInput]) &&
    Date.parse(left.validFrom) === Date.parse(right.validFrom) && Date.parse(left.validUntil) === Date.parse(right.validUntil) &&
    (['receivable', 'revenue', 'tax', 'charges'] as const).every(key => left.mappings[key] === right.mappings[key]) &&
    left.allocations.length === right.allocations.length && left.allocations.every(item => right.allocations.some(other => lineKey(item) === lineKey(other) && cents(item.tax) === cents(other.tax) && cents(item.charges) === cents(other.charges)));
}
function accounted(value: InvoiceReleaseInput, source: InvoiceReleaseFacts) {
  return source.accounts.length === 4 && source.accountingPeriod !== null && source.accounts.every(account => value.mappings[account.role] === account.mappingKey) &&
    value.allocations.length === source.lines.length && source.lines.every(line => value.allocations.some(amount => lineKey(amount) === lineKey(line) && line.tax !== null && line.charges !== null && cents(amount.tax) === cents(line.tax) && cents(amount.charges) === cents(line.charges)));
}
export function parseInvoiceReleaseEligibility(value: unknown, receipts: InvoiceReleaseSelection) {
  const row = parse(eligibility, value); return sameSources(row.facts, parse(selected, receipts)) ? row : invalid();
}
export function parseInvoiceReleasePreview(value: unknown, expected: InvoiceReleaseInput, selectionKey: string, expectedRevision: string) {
  const row = parse(preview, value);
  if (row.selectionKey !== parse(hash, selectionKey) || row.revision !== parse(revision, expectedRevision) || !sameInput(row.input, parse(input, expected)) || !sameSources(row.facts, row.input.receipts) || !accounted(row.input, row.facts)) return invalid(); return row;
}
export function parseInvoiceReleaseView(value: unknown) {
  const row = parse(view, value); if (!sameSources(row.facts, row.input.receipts) || !accounted(row.input, row.facts) ||
    BigInt(row.result.revision) !== BigInt(row.input.expectedReleaseRevision) + (row.result.action === 'propose' ? BigInt(1) : BigInt(2))) return invalid(); return row;
}
export function parseInvoiceReleaseResult(value: unknown, reference?: Pick<InvoiceReleaseReference, 'action' | 'revision' | 'proposalId' | 'selectionKey'>) {
  const row = parse(result, value);
  if (reference && (row.action !== reference.action || row.selectionKey !== reference.selectionKey || BigInt(row.revision) !== BigInt(reference.revision) + BigInt(1) ||
    reference.proposalId !== null && row.proposalId !== reference.proposalId)) return invalid(); return row;
}
export function parseInvoiceReleaseInput(value: unknown) {return entered(input, value);}
export function invoiceReleaseWib(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$(?![\s\S])/.test(value) || !Number.isFinite(Date.parse(value + ':00+07:00'))) throw new RequestNotSentError(400, 'invoice_release_invalid');
  const parsed = new Date(value + ':00+07:00'); if (new Date(parsed.getTime() + 7 * 3600000).toISOString().slice(0, 16) !== value) throw new RequestNotSentError(400, 'invoice_release_invalid'); return parsed.toISOString();
}
export type InvoiceReleaseCommand = {actorId: string; action: 'propose' | 'approve' | 'reject'; revision: string; proposalId: string | null; selectionKey: string;
  preview: InvoiceReleasePreview; reason: string};
export function prepareInvoiceRelease(actorId: string, value: InvoiceReleasePreview, operation: InvoiceReleaseCommand['action'], proposalId: string | null, reason: string): InvoiceReleaseCommand {
  const checked = entered(preview, value); if (!sameSources(checked.facts, checked.input.receipts) || !accounted(checked.input, checked.facts) ||
    (operation === 'propose' ? proposalId !== null || checked.revision !== checked.input.expectedReleaseRevision : !id.safeParse(proposalId).success || BigInt(checked.revision) !== BigInt(checked.input.expectedReleaseRevision) + BigInt(1))) throw new RequestNotSentError(400, 'invoice_release_invalid');
  return {actorId: entered(id, actorId), action: entered(action, operation), revision: checked.revision, proposalId, selectionKey: checked.selectionKey, preview: checked, reason: entered(text, reason)};
}
export function createInvoiceReleaseGateway(fetcher: typeof fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function post(path: string, actorId: string, body: unknown, signal?: AbortSignal, command?: {key: string; revision: string; current: () => boolean}) {
    const actor = entered(id, actorId), controller = new AbortController(), abort = () => controller.abort();
    signal?.addEventListener('abort', abort, {once: true}); if (signal?.aborted) abort(); const timer = setTimeout(abort, 30000);
    const headers: Record<string, string> = {'Content-Type': 'application/json', 'X-Expected-Actor-Id': actor};
    if (command) {headers['Idempotency-Key'] = entered(requestKey, command.key); headers['If-Match'] = '"' + entered(revision, command.revision) + '"';}
    try {
      headers['X-CSRF-Token'] = await fetchCsrfToken(csrfUrl, fetcher, controller.signal);
      if (command && !command.current()) throw new RequestNotSentError(409, 'invoice_release_changed');
      const response = await fetcher('/api/v1/billing/invoice-releases' + path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal, headers, body: JSON.stringify(body)});
      if (command && !command.current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const value: unknown = await response.json(); if (command && !command.current()) throw new AccessFailure(503, 'commit_outcome_unknown'); if (!response.ok) {const problem = value && typeof value === 'object' ? value as Record<string, unknown> : {}; throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');} return value;
    } finally {clearTimeout(timer); signal?.removeEventListener('abort', abort);}
  }
  return {
    async candidates(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const before = beforeId === null ? null : entered(id, beforeId), page = parse(z.object({items: z.array(candidate).max(25), nextShipmentId: id.nullable()}).strict(), await post('/candidates/query', actorId, {purpose: 'invoice-releases', beforeId: before}, signal));
      let previous = before; for (const item of page.items) {if (previous !== null && BigInt(item.shipmentId) >= BigInt(previous)) return invalid(); previous = item.shipmentId;}
      if (new Set(page.items.map(item => item.receiptId)).size !== page.items.length || page.nextShipmentId !== null && (before !== null && BigInt(page.nextShipmentId) >= BigInt(before) || page.items.some(item => BigInt(item.shipmentId) < BigInt(page.nextShipmentId!)))) return invalid(); return page;
    },
    async eligibility(actorId: string, values: InvoiceReleaseSelection, signal?: AbortSignal) {
      const receipts = entered(selected, values); return parseInvoiceReleaseEligibility(await post('/eligibility/query', actorId, {purpose: 'invoice-releases', receipts}, signal), receipts);
    },
    async preview(actorId: string, value: InvoiceReleaseInput, selectedKey: string) {
      const checked = entered(input, value); return parseInvoiceReleasePreview(await post('/preview/query', actorId, {purpose: 'invoice-releases', input: checked}), checked, selectedKey, checked.expectedReleaseRevision);
    },
    async decisionPreview(actorId: string, record: InvoiceReleaseView, decision: 'approve' | 'reject') {
      const saved = parseInvoiceReleaseView(record); if (saved.result.action !== 'propose') throw new RequestNotSentError(409, 'invoice_release_changed');
      return parseInvoiceReleasePreview(await post('/proposals/' + saved.result.proposalId + '/decision-preview/query', actorId,
        {purpose: 'invoice-releases', expectedRevision: saved.result.revision, decision: entered(z.enum(['approve', 'reject']), decision)}), saved.input, saved.result.selectionKey, saved.result.revision);
    },
    async change(command: InvoiceReleaseCommand, key: string, current: () => boolean) {
      const checked = prepareInvoiceRelease(command.actorId, command.preview, command.action, command.proposalId, command.reason);
      if (checked.selectionKey !== command.selectionKey || checked.revision !== command.revision || !current()) throw new RequestNotSentError(409, 'invoice_release_changed');
      const body = command.action === 'propose' ? {input: checked.preview.input, binding: checked.preview.binding, reason: checked.reason} :
        {expectedRevision: checked.revision, decision: checked.action, binding: checked.preview.binding, reason: checked.reason};
      return parseInvoiceReleaseResult(await post(command.action === 'propose' ? '' : '/proposals/' + checked.proposalId + '/decisions', checked.actorId, body, undefined,
        {key, revision: checked.revision, current}), checked);
    },
    async read(actorId: string, recordId: string, signal?: AbortSignal) {
      const expected = entered(id, recordId), found = parseInvoiceReleaseView(await post('/records/' + expected + '/query', actorId, {purpose: 'invoice-releases'}, signal)); return found.result.recordId === expected ? found : invalid();
    },
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const before = beforeId === null ? null : entered(id, beforeId), page = parse(z.object({items: z.array(z.unknown()).max(25), nextId: id.nullable()}).strict(), await post('/query', actorId, {purpose: 'invoice-releases', beforeId: before}, signal));
      const items = page.items.map(parseInvoiceReleaseView); let previous = before;
      for (const item of items) {if (previous !== null && BigInt(item.result.releaseId) >= BigInt(previous)) return invalid(); previous = item.result.releaseId;}
      if (page.nextId !== null && (items.length !== 25 || page.nextId !== items.at(-1)!.result.releaseId)) return invalid(); return {items, nextId: page.nextId};
    },
    async recover(actorId: string, reference: InvoiceReleaseReference, signal?: AbortSignal) {
      if (actorId !== reference.actorId) throw new RequestNotSentError(409, 'actor_changed');
      const row = parse(z.object({status: z.enum(['not_confirmed', 'committed']), result: result.nullable()}).strict(), await post('/operations/query', actorId,
        {purpose: 'operation-recovery', action: entered(action, reference.action), key: entered(requestKey, reference.key)}, signal));
      if (row.status === 'not_confirmed' && row.result === null) return null; if (row.status !== 'committed' || row.result === null) return invalid(); return parseInvoiceReleaseResult(row.result, reference);
    },
  };
}
