import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';

export const accountingReportPurpose = 'accounting-reporting' as const;
export const accountingReportLimits = Object.freeze({books: 128, journals: 500, lines: 10_000, accounts: 2_000, charts: 64, csvBytes: 8 * 1024 * 1024});
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/u).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/u);
const code = z.string().regex(/^[A-Za-z][A-Za-z0-9._-]{0,63}$(?![\s\S])/u);
const text = (maximum: number) => z.string().min(1).refine(value => value === value.trim() && value === value.normalize('NFC') &&
  value.isWellFormed() && [...value].length <= maximum && !/[\p{Cc}\p{Cf}\p{Cs}]/u.test(value));
const date = z.string().regex(/^(?!0000)[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])$(?![\s\S])/u)
  .refine(value => value !== '0001-01-01' && Number.isFinite(Date.parse(value + 'T00:00:00.000Z')) && new Date(value + 'T00:00:00.000Z').toISOString().slice(0, 10) === value);
const timestamp = z.string().datetime({offset: true}).refine(value => /(?:Z|\+00:00)$/u.test(value) && Number.isFinite(Date.parse(value)) &&
  new Date(value).toISOString().slice(0, 19) === value.slice(0, 19));
const money = z.string().regex(/^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/u);
const signedMoney = z.string().regex(/^-?(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/u).refine(value => value !== '-0.00');
const bookKey = z.object({organizationId: id, shopId: id, channelId: id, currency: z.string().regex(/^[A-Z]{3}$(?![\s\S])/u)}).strict();
const label = z.object({id, code: text(64), name: text(160)}).strict();
const book = z.object({id, key: bookKey, organization: label, shop: label, channel: label}).strict().refine(value =>
  value.key.organizationId === value.organization.id && value.key.shopId === value.shop.id && value.key.channelId === value.channel.id);
const books = z.object({readId: id, items: z.array(book).max(accountingReportLimits.books)}).strict().refine(value =>
  new Set(value.items.map(item => item.id)).size === value.items.length && new Set(value.items.map(item => accountingReportBookKey(item.key))).size === value.items.length);
const account = z.object({accountId: id, accountCodes: z.array(code).min(1).max(10_000), debit: money, credit: money, balance: signedMoney}).strict();
const line = z.object({mappingKey: code, accountId: id, accountCode: code, debit: money, credit: money}).strict();
const source = z.object({namespace: code, documentId: id, eventKind: code, occurrence: id}).strict();
// The installed journal schema retains its original reason; reading does not normalize historical text.
const description = z.string().min(1).refine(value => value.isWellFormed() && value === value.trim() &&
  [...value].length <= 2000 && !/[\u0000-\u001f\u007f-\u009f]/u.test(value));
const journal = z.object({id, businessDate: date, source, chartId: id, chartVersion: id, reversalOf: id.nullable(),
  description, lines: z.array(line).min(2).max(100)}).strict();
const snapshot = z.object({readId: id, snapshotHash: hash, book, endDate: date, capturedAt: timestamp, expiresAt: timestamp,
  debit: money, credit: money, accounts: z.array(account).max(accountingReportLimits.accounts), journals: z.array(journal).max(accountingReportLimits.journals),
  timeZone: z.literal('Asia/Jakarta'), cumulative: z.literal(true), isApprovedClose: z.literal(false)}).strict();
const invalid = (): never => {throw new AccessFailure(502, 'accounting_report_response_invalid');};
function parse<T>(schema: z.ZodType<T>, value: unknown): T {const checked = schema.safeParse(value); return checked.success ? checked.data : invalid();}
export type AccountingReportBookKey = z.infer<typeof bookKey>;
export type AccountingReportBook = z.infer<typeof book>;
export type AccountingReportBooks = z.infer<typeof books>;
export type AccountingReportSnapshot = z.infer<typeof snapshot>;
export type AccountingReportCsv = Readonly<{readId: string; snapshotReadId: string; snapshotHash: string; fileName: string; bytes: Uint8Array}>;
export function accountingReportBookKey(value: AccountingReportBookKey): string {return [value.organizationId, value.shopId, value.channelId, value.currency].join(':');}
export function parseAccountingReportBooks(value: unknown): AccountingReportBooks {return parse(books, value);}
export function accountingReportDate(value: string): string {
  const checked = date.safeParse(value); if (!checked.success) throw new RequestNotSentError(400, 'invalid_accounting_report_date'); return checked.data;
}
/** Formatting uses the server amount directly, with no floating-point conversion or calculated report totals. */
export function formatAccountingReportMoney(value: string, currency: string): string {
  const amount = parse(signedMoney, value), currencyCode = parse(bookKey.shape.currency, currency);
  const negative = amount.startsWith('-'), [whole, fraction] = (negative ? amount.slice(1) : amount).split('.');
  return `${currencyCode === 'IDR' ? 'Rp' : currencyCode} ${negative ? '-' : ''}${whole.replace(/\B(?=(\d{3})+(?!\d))/gu, '.')},${fraction}`;
}
export function parseAccountingReportSnapshot(value: unknown, expectedBook: AccountingReportBook, expectedDate: string): AccountingReportSnapshot {
  const result = parse(snapshot, value), owner = parse(book, expectedBook), endDate = accountingReportDate(expectedDate);
  if (JSON.stringify(result.book) !== JSON.stringify(owner) || result.endDate !== endDate || result.debit !== result.credit ||
    Date.parse(result.expiresAt) - Date.parse(result.capturedAt) !== 15 * 60_000) return invalid();
  const accounts = new Map(result.accounts.map(item => [item.accountId, item]));
  if (accounts.size !== result.accounts.length || result.accounts.some((item, index) =>
    index > 0 && BigInt(item.accountId) <= BigInt(result.accounts[index - 1].accountId) ||
    item.accountCodes.some((value, codeIndex) => codeIndex > 0 && value <= item.accountCodes[codeIndex - 1]))) return invalid();
  let lines = 0; const sources = new Set<string>(), journalIds = new Set<string>(), charts = new Set<string>();
  for (let index = 0; index < result.journals.length; index++) {
    const item = result.journals[index], previous = result.journals[index - 1];
    const sourceKey = [item.source.namespace, item.source.documentId, item.source.eventKind, item.source.occurrence].join(':');
    if (item.businessDate > endDate || previous && (item.businessDate < previous.businessDate || item.businessDate === previous.businessDate && BigInt(item.id) <= BigInt(previous.id)) ||
      journalIds.has(item.id) || sources.has(sourceKey)) return invalid();
    journalIds.add(item.id); sources.add(sourceKey); charts.add(item.chartId + ':' + item.chartVersion); lines += item.lines.length;
    for (const row of item.lines) {
      const target = accounts.get(row.accountId);
      if (!target || !target.accountCodes.includes(row.accountCode) || (row.debit === '0.00') === (row.credit === '0.00')) return invalid();
    }
  }
  if (lines > accountingReportLimits.lines || charts.size > accountingReportLimits.charts ||
    result.journals.some(item => item.reversalOf !== null && (!journalIds.has(item.reversalOf) || item.reversalOf === item.id))) return invalid();
  return result;
}

type Path = '/api/v1/accounting/reports/books/query' | '/api/v1/accounting/reports/query' | '/api/v1/accounting/reports/export';
export type AccountingReportRequest = Readonly<{path: Path; actorId: string; body: Readonly<Record<string, unknown>>; isCurrent: () => boolean; signal?: AbortSignal}>;
export type AccountingReportTransport = {send(request: AccountingReportRequest): Promise<Response>};
export function createAccountingReportTransport(options: {fetch?: typeof globalThis.fetch; origin?: string} = {}): AccountingReportTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return {async send(request) {
    const origin = options.origin ?? window.location.origin, url = new URL(request.path, origin);
    if (url.origin !== new URL(origin).origin || typeof window !== 'undefined' && url.origin !== window.location.origin)
      throw new RequestNotSentError(400, 'invalid_origin');
    const check = () => {if (!request.isCurrent() || request.signal?.aborted) throw new RequestNotSentError(409, 'accounting_report_view_stale');};
    check();
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, request.signal); check();
    return fetcher(new Request(url, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal,
      headers: {'X-Expected-Actor-Id': request.actorId, 'X-CSRF-Token': token, 'Content-Type': 'application/json'}, body: JSON.stringify(request.body)}));
  }};
}
async function data(response: Response): Promise<unknown> {
  const value: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const problem = value !== null && typeof value === 'object' ? value as Record<string, unknown> : {};
    throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return value;
}
function current(isCurrent: () => boolean, signal?: AbortSignal) {if (!isCurrent() || signal?.aborted) throw new AccessFailure(409, 'accounting_report_view_stale');}
function csvName(disposition: string | null): string {
  if (!disposition || !/^attachment\s*;/iu.test(disposition) || /[\r\n]/u.test(disposition)) return invalid();
  const plain = /(?:^|;)\s*filename=(?:"([^"]+)"|([^;\s]+))/iu.exec(disposition);
  const encoded = /(?:^|;)\s*filename\*=UTF-8''([^;\s]+)/iu.exec(disposition);
  let name: string | undefined;
  try {name = encoded ? decodeURIComponent(encoded[1]) : plain?.[1] ?? plain?.[2];} catch {return invalid();}
  if (!name || plain && (plain[1] ?? plain[2]) !== name || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,155}\.csv$(?![\s\S])/u.test(name) ||
    name.includes('..') || /^(?:con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/iu.test(name)) return invalid();
  return name;
}
async function readCsv(response: Response, source: AccountingReportSnapshot, isCurrent: () => boolean, signal?: AbortSignal): Promise<AccountingReportCsv> {
  if (!response.ok) {await data(response); return invalid();}
  current(isCurrent, signal);
  const readId = parse(id, response.headers.get('X-Read-Id')), snapshotReadId = response.headers.get('X-Snapshot-Read-Id');
  const snapshotHash = response.headers.get('X-Snapshot-Hash'), fileName = csvName(response.headers.get('Content-Disposition'));
  if (snapshotReadId !== source.readId || snapshotHash !== source.snapshotHash || readId === snapshotReadId ||
    !/^text\/csv\s*;\s*charset=utf-8$/iu.test(response.headers.get('Content-Type') ?? '') ||
    !/(?:^|,)\s*private\s*(?:,|$)/iu.test(response.headers.get('Cache-Control') ?? '') ||
    !/(?:^|,)\s*no-store\s*(?:,|$)/iu.test(response.headers.get('Cache-Control') ?? '') || response.headers.get('X-Content-Type-Options') !== 'nosniff') return invalid();
  const declared = response.headers.get('Content-Length');
  if (declared !== null && (!/^[1-9][0-9]{0,7}$(?![\s\S])/u.test(declared) || Number(declared) > accountingReportLimits.csvBytes)) return invalid();
  if (!response.body) return invalid();
  const reader = response.body.getReader(), chunks: Uint8Array[] = []; let length = 0;
  try {
    while (true) {
      current(isCurrent, signal); const next = await reader.read(); current(isCurrent, signal);
      if (next.done) break;
      length += next.value.byteLength;
      if (length > accountingReportLimits.csvBytes) return invalid();
      chunks.push(next.value);
    }
  } finally {await reader.cancel().catch(() => undefined); reader.releaseLock();}
  if (length < 5 || declared !== null && length !== Number(declared)) return invalid();
  const bytes = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) {bytes.set(chunk, offset); offset += chunk.byteLength;}
  if (bytes[0] !== 0xef || bytes[1] !== 0xbb || bytes[2] !== 0xbf) return invalid();
  let content: string;
  try {content = new TextDecoder('utf-8', {fatal: true}).decode(bytes);} catch {return invalid();}
  if (!content.endsWith('\r\n') || /(?<!\r)\n|\r(?!\n)/u.test(content) || /\u0000/u.test(content) ||
    !content.includes(`"Snapshot","${source.snapshotHash}"`)) return invalid();
  current(isCurrent, signal); return Object.freeze({readId, snapshotReadId, snapshotHash, fileName, bytes});
}
async function csv(response: Response, source: AccountingReportSnapshot, isCurrent: () => boolean, signal?: AbortSignal): Promise<AccountingReportCsv> {
  try {return await readCsv(response, source, isCurrent, signal);}
  catch (error) {
    // A rejected header or changed view may fail before a reader is acquired; stop that private stream too.
    if (response.body && !response.body.locked) await response.body.cancel().catch(() => undefined);
    throw error;
  }
}
export function createAccountingReportGateway(transport: AccountingReportTransport = createAccountingReportTransport(), now: () => number = () => Date.now()) {
  return {
    async books(actorId: string, isCurrent: () => boolean, signal?: AbortSignal) {
      parse(id, actorId); current(isCurrent, signal);
      const result = await data(await transport.send({path: '/api/v1/accounting/reports/books/query', actorId,
        body: {purpose: accountingReportPurpose}, isCurrent, signal})); current(isCurrent, signal); return parseAccountingReportBooks(result);
    },
    async read(actorId: string, selected: AccountingReportBook, endDate: string, isCurrent: () => boolean, signal?: AbortSignal) {
      parse(id, actorId); const owner = parse(book, selected), date = accountingReportDate(endDate); current(isCurrent, signal);
      const result = await data(await transport.send({path: '/api/v1/accounting/reports/query', actorId,
        body: {purpose: accountingReportPurpose, key: owner.key, endDate: date}, isCurrent, signal}));
      current(isCurrent, signal); return parseAccountingReportSnapshot(result, owner, date);
    },
    async export(actorId: string, shown: AccountingReportSnapshot, isCurrent: () => boolean, signal?: AbortSignal) {
      parse(id, actorId); const source = parseAccountingReportSnapshot(shown, shown.book, shown.endDate); current(isCurrent, signal);
      if (now() >= Date.parse(source.expiresAt)) throw new RequestNotSentError(409, 'accounting_report_snapshot_stale');
      const response = await transport.send({path: '/api/v1/accounting/reports/export', actorId,
        body: {purpose: accountingReportPurpose, readId: source.readId, snapshotHash: source.snapshotHash}, isCurrent, signal});
      const result = await csv(response, source, isCurrent, signal);
      if (now() >= Date.parse(source.expiresAt)) throw new AccessFailure(409, 'accounting_report_snapshot_stale'); return result;
    },
  };
}
export type AccountingReportGateway = ReturnType<typeof createAccountingReportGateway>;
