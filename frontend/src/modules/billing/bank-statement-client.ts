import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';

const id = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const date = z.string().refine(value => Number.isFinite(Date.parse(value)));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const money = z.string().regex(/^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/);
const details = z.object({ bankCode: text(32), accountNumber: z.string().regex(/^[0-9]{4,34}$(?![\s\S])/), holderName: text(160), currency: z.literal('IDR') }).strict();
const format = z.object({ delimiter: z.enum([',', ';', '\t']), dateFormat: z.enum(['yyyy-MM-dd', 'dd/MM/yyyy']), decimalSeparator: z.enum(['.', ',']), creditMarker: text(16), debitMarker: text(16), columns: z.array(text(80)).length(8), identityNamespace: text(80).nullable() }).strict();
const profile = z.object({ id, revision: id, name: text(160), bankCode: text(32), format, trustedSourceReference: text(256), operatingOwnerReference: text(256), approvalReference: text(256), active: z.boolean(), approvedAt: date, expiresAt: date }).strict();
const account = z.object({ accountId: id, versionId: id, version: id, details, effectiveAt: date }).strict();
const row = z.object({ number: z.number().int().min(2).max(501), transactionId: text(80).nullable(), bookedDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$(?![\s\S])/), direction: z.enum(['credit', 'debit']), amount: money, payerName: text(160).nullable(), reference: text(160).nullable(), identityCandidate: hash.nullable() }).strict();
const issue = z.object({ row: z.number().int().min(2).max(501), field: text(32), code: z.enum(['identity_missing', 'identity_repeated', 'reference_repeated', 'payer_unknown', 'column_count', 'invalid_text', 'invalid_date', 'invalid_direction', 'invalid_amount', 'account_mismatch', 'currency_mismatch', 'total_out_of_range']) }).strict();
// A malformed row can have eight text findings and five typed-field findings.
const parsed = z.object({ sourceSha256: hash, rows: z.array(row).max(500), issues: z.array(issue).max(6500), credits: money, debits: money, canStage: z.boolean() }).strict();
const preview = z.object({ binding: hash, preview: parsed }).strict();
const summary = z.object({ importId: id, profileId: id, profileName: text(160), accountId: id, accountVersionId: id, maskedAccount: text(34), filename: text(160), rowCount: z.number().int().min(1).max(500), issueCount: z.number().int().min(0).max(2000), credits: money, debits: money, actorId: id, recordedAt: date, state: z.literal('staged') }).strict();
const detail = z.object({ importId: id, profile, account, filename: text(160), sourceReference: text(256), reason: text(1000), actorId: id, recordedAt: date, preview: parsed, state: z.literal('staged') }).strict();
const selection = z.object({ profileId: id, profileRevision: id, accountId: id, accountVersionId: id }).strict();
const result = z.object({ importId: id, operation: z.literal('billing.bank-statement.stage'), rowCount: z.number().int().min(1).max(500), recordedAt: date }).strict();
export type StatementProfile = z.infer<typeof profile>;
export type StatementAccount = z.infer<typeof account>;
export type StatementSelection = z.infer<typeof selection>;
export type StatementPreview = z.infer<typeof preview>;
export type StatementParsed = z.infer<typeof parsed>;
export type StatementSummary = z.infer<typeof summary>;
export type StatementDetail = z.infer<typeof detail>;
export type StatementResult = z.infer<typeof result>;
export type StatementCommand = { actorId: string; body: { selection: StatementSelection; originalBase64: string; filename: string; binding: string; sourceReference: string; attested: true; reason: string } };
const invalid = (): never => { throw new AccessFailure(502, 'bank_statement_response_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown, input = false): T { const found = schema.safeParse(value); if (found.success) return found.data; if (input) throw new AccessFailure(400, 'invalid_bank_statement'); return invalid(); }
export function statementKey(key: string) { if (!/^bs\.stage\.0\.[0-9a-f-]{36}$(?![\s\S])/.test(key)) return invalid(); return key; }
export function parseStatementResult(value: unknown, key: string): StatementResult { statementKey(key); return parse(result, value); }
export function prepareStatement(actorId: string, chosen: StatementSelection, originalBase64: string, filename: string, shown: StatementPreview, sourceReference: string, attested: boolean, reason: string): StatementCommand {
  parse(id, actorId, true); parse(selection, chosen, true); parse(preview, shown, true);
  if (!shown.preview.canStage || !shown.preview.rows.length || !attested || !/^[A-Za-z0-9+/]+={0,2}$(?![\s\S])/.test(originalBase64) || originalBase64.length > 349528 || /[/\\]/.test(filename)) throw new AccessFailure(400, 'invalid_bank_statement');
  return { actorId, body: { selection: { ...chosen }, originalBase64, filename: parse(text(160), filename, true), binding: shown.binding, sourceReference: parse(text(256), sourceReference, true), attested: true, reason: parse(text(1000), reason, true) } };
}
export async function statementFile(file: File): Promise<string> {
  if (file.size < 1 || file.size > 262144) throw new AccessFailure(400, 'bank_statement_file_limit');
  const bytes = new Uint8Array(await file.arrayBuffer()); if (bytes.length !== file.size) throw new AccessFailure(400, 'invalid_bank_statement');
  let binary = ''; for (const value of bytes) binary += String.fromCharCode(value); return btoa(binary);
}
function data<T>(value: { data?: T; error?: unknown; response: Response }): T { if (value.error !== undefined || value.data === undefined) { const error = value.error as { code?: string }; throw new AccessFailure(value.response.status, error?.code ?? 'request_failed'); } return value.data; }
export function createBankStatementGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
  return {
    async profiles(actorId: string, afterId: string | null = null) { return parse(z.object({ items: z.array(profile).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/bank-statements/profiles/query', { body: { purpose: 'bank-statement-review', afterId }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async accounts(actorId: string, selected: StatementProfile, afterId: string | null = null) { return parse(z.object({ items: z.array(account).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/bank-statements/accounts/query', { body: { purpose: 'bank-statement-review', profileId: selected.id, profileRevision: selected.revision, afterId }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async list(actorId: string, afterId: string | null = null) { return parse(z.object({ items: z.array(summary).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/bank-statements/imports/query', { body: { purpose: 'bank-statement-review', afterId }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async read(actorId: string, importId: string) {
      const saved = parse(detail, data(await client.POST('/api/v1/billing/bank-statements/imports/{id}/query', { body: { purpose: 'bank-statement-review' }, params: { path: { id: parse(id, importId) }, header: headers(actorId) }, cache: 'no-store' })));
      if (saved.importId !== importId || !saved.preview.canStage) return invalid(); return saved;
    },
    async preview(actorId: string, chosen: StatementSelection, originalBase64: string) { return parse(preview, data(await client.POST('/api/v1/billing/bank-statements/preview', { body: { selection: chosen, originalBase64 }, params: { header: headers(actorId) }, cache: 'no-store' }))); },
    async original(actorId: string, importId: string) {
      const response = await client.POST('/api/v1/billing/bank-statements/imports/{id}/original/query', { body: { purpose: 'bank-statement-review' }, params: { path: { id: parse(id, importId) }, header: headers(actorId) }, parseAs: 'arrayBuffer', cache: 'no-store' });
      const bytes = data(response); if (!(bytes instanceof ArrayBuffer) || bytes.byteLength < 1 || bytes.byteLength > 262144) return invalid(); return bytes;
    },
    async send(command: StatementCommand, key: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'bank_statement_view_stale'); statementKey(key);
      const saved = data(await client.POST('/api/v1/billing/bank-statements/imports', { body: command.body, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key, 'If-Match': '"0"' } }, cache: 'no-store' }));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return parseStatementResult(saved, key);
    },
    async recover(actorId: string, key: string) {
      statementKey(key); const saved = parse(z.object({ status: z.enum(['committed', 'not_confirmed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/bank-statements/operations/query', { body: { purpose: 'operation-recovery', operation: 'billing.bank-statement.stage', key }, params: { header: headers(actorId) }, cache: 'no-store' })));
      if (saved.status === 'not_confirmed' && saved.result === null) return null;
      return saved.status === 'committed' && saved.result ? { state: 'committed' as const, result: parseStatementResult(saved.result, key) } : invalid();
    },
  };
}
