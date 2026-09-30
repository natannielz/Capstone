import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const date = z.string().refine(value => Number.isFinite(Date.parse(value)));
const day = z.string().regex(/^\d{4}-\d{2}-\d{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const amount = z.string().regex(/^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/).refine(value => value !== '0.00');
const number = z.number().int().min(2).max(501);
const policy = z.object({ id, revision: id, trustedSourceReference: text(256), operatingOwnerReference: text(256), approvalReference: text(256), cashMapping: text(80), suspenseMapping: text(80), expiresAt: date }).strict();
const account = z.object({ accountId: id, versionId: id, version: id, details: z.object({ bankCode: text(32), accountNumber: z.string().regex(/^[0-9]{4,34}$(?![\s\S])/), holderName: text(160), currency: z.literal('IDR') }).strict(), effectiveAt: date }).strict();
const row = z.object({ number, transactionId: text(80), bookedDate: day, direction: z.literal('credit'), amount, payerName: text(160).nullable(), reference: text(160).nullable(), identityCandidate: hash }).strict();
const preview = z.object({ importId: id, rowNumber: number, binding: hash, row, account, policy, existingReceiptId: id.nullable() }).strict().refine(value => value.rowNumber === value.row.number);
const result = z.object({ confirmationId: id, receiptId: id, importId: id, rowNumber: number, createdReceipt: z.boolean(), recordedAt: date, operation: z.literal('billing.bank-receipt.confirm') }).strict();
const receipt = z.object({ receiptId: id, importId: id, rowNumber: number, transactionId: text(80), bookedDate: day, amount, journalId: id, actorId: id, recordedAt: date, policy }).strict();
const observation = z.object({ accountNumber: z.string().regex(/^[0-9]{4,34}$(?![\s\S])/), transactionId: text(80), bookedDate: day, amount }).strict();
export type BankReceiptPreview = z.infer<typeof preview>;
export type BankReceiptResult = z.infer<typeof result>;
export type BankReceiptView = z.infer<typeof receipt>;
export type BankReceiptObservation = z.infer<typeof observation>;
export type BankReceiptCommand = { actorId: string; body: { importId: string; rowNumber: number; binding: string; observed: BankReceiptObservation; sourceReference: string; attested: true; reason: string } };
const invalid = (): never => { throw new AccessFailure(502, 'bank_receipt_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown, input = false): T { const found = schema.safeParse(value); if (found.success) return found.data; if (input) throw new AccessFailure(400, 'invalid_bank_receipt'); return invalid(); }
export function bankReceiptKey(key: string) {
  const parts = /^br\.confirm\.([1-9][0-9]{0,18})\.([0-9]{1,3})\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\s\S])/.exec(key);
  if (!parts) return invalid(); return { importId: parse(id, parts[1]), rowNumber: parse(number, Number(parts[2])) };
}
export function parseBankReceiptResult(value: unknown, key: string): BankReceiptResult {
  const owner = bankReceiptKey(key), saved = parse(result, value); if (saved.importId !== owner.importId || saved.rowNumber !== owner.rowNumber) return invalid(); return saved;
}
export function prepareBankReceipt(actorId: string, shown: BankReceiptPreview, observed: BankReceiptObservation, sourceReference: string, attested: boolean, reason: string): BankReceiptCommand {
  parse(id, actorId, true); parse(preview, shown, true); const copied = parse(observation, observed, true);
  if (!attested || copied.accountNumber !== shown.account.details.accountNumber || copied.transactionId !== shown.row.transactionId || copied.bookedDate !== shown.row.bookedDate || copied.amount !== shown.row.amount) throw new AccessFailure(400, 'bank_receipt_observation_mismatch');
  return { actorId, body: { importId: shown.importId, rowNumber: shown.rowNumber, binding: shown.binding, observed: { ...copied }, sourceReference: parse(text(256), sourceReference, true), attested: true, reason: parse(text(1000), reason, true) } };
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T { if (value.error !== undefined || value.data === undefined) { const error = value.error as { code?: string }; throw new AccessFailure(value.response.status, error?.code ?? 'request_failed'); } return value.data; }
export function createBankReceiptGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async list(actorId: string, afterId: string | null = null) { return parse(z.object({ items: z.array(receipt).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/bank-receipts/query', { body: { purpose: 'bank-receipt-review', afterId }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async read(actorId: string, receiptId: string) { const saved = parse(receipt, data(await client.POST('/api/v1/billing/bank-receipts/{id}/query', { body: { purpose: 'bank-receipt-review' }, params: { path: { id: receiptId }, header: headers(actorId) }, cache: 'no-store' }))); if (saved.receiptId !== receiptId) return invalid(); return saved; },
    async imports(actorId: string, importId: string) {
      const saved = parse(z.object({ items: z.array(result).max(500) }).strict(), data(await client.POST('/api/v1/billing/bank-receipts/imports/{id}/query', { body: { purpose: 'bank-receipt-review' }, params: { path: { id: importId }, header: headers(actorId) }, cache: 'no-store' })));
      if (saved.items.some(row => row.importId !== importId) || new Set(saved.items.map(row => row.rowNumber)).size !== saved.items.length) return invalid(); return saved.items;
    },
    async preview(actorId: string, importId: string, rowNumber: number) {
      const saved = parse(preview, data(await client.POST('/api/v1/billing/bank-receipts/preview', { body: { importId, rowNumber }, params: { header: headers(actorId) }, cache: 'no-store' })));
      if (saved.importId !== importId || saved.rowNumber !== rowNumber) return invalid(); return saved;
    },
    async send(command: BankReceiptCommand, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'bank_receipt_view_stale'); const owner = bankReceiptKey(key);
      if (owner.importId !== command.body.importId || owner.rowNumber !== command.body.rowNumber) throw new RequestNotSentError(409, 'bank_receipt_view_stale');
      const saved = data(await client.POST('/api/v1/billing/bank-receipts/confirmations', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"0"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parseBankReceiptResult(saved, key);
    },
    async recover(actorId: string, key: string) {
      bankReceiptKey(key); const saved = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/bank-receipts/operations/query', { body: { purpose: 'operation-recovery', operation: 'billing.bank-receipt.confirm', key }, params: { header: headers(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parseBankReceiptResult(saved.result, key) } : invalid();
    },
  };
}
