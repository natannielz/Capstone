import { z } from 'zod';
import { fetchCsrfToken, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { parseCaseView, type AfterSalesCase } from './case-client';
import { parseInspectionView, type InspectionView } from './inspection-client';

export const returnLotOperation = 'fulfillment.case.return_lot_verify';
export const returnLotPurpose = 'after-sales-return-lot-verification';
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const units = z.string().regex(/^(?:0|[1-9][0-9]{0,18})$(?![\s\S])/).refine(value => BigInt(value) <= BigInt(1000000));
const positiveUnits = units.refine(value => BigInt(value) > BigInt(0));
const text = (max: number) => z.string().min(1).max(max).refine(value => value === value.trim() && !/[\p{Cc}\p{Cf}]/u.test(value));
const choice = z.object({ originalLotId: id, poolId: id, definitionId: id, packageLabel: text(160), baseUnitCode: text(64),
  baseUnitsPerPackage: z.number().int().min(1).max(1000000), dispatchedBaseUnits: id }).strict();
const uniqueLines = (lines: readonly { receiptId: string; skuId: string }[]) => new Set(lines.map(line => line.receiptId + ':' + line.skuId)).size === lines.length;
const line = z.object({ receiptId: id, skuId: id, receivedBaseUnits: positiveUnits, verifiedBaseUnits: units, manualBaseUnits: units,
  state: z.enum(['tetap_dikarantina', 'menunggu_verifikasi_fisik', 'penanganan_manual']), explanation: text(1000), choices: z.array(choice).max(100) }).strict();
const view = z.object({ caseId: id, returnId: id, revision: z.enum(['0', '1']), binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/),
  state: z.enum(['belum_diverifikasi', 'lot_asli_diverifikasi_tetap_dikarantina']), lines: z.array(line).min(1).max(100).refine(uniqueLines) }).strict();
const result = z.object({ returnId: id, caseId: id, invoiceId: id, revision: z.literal('1'), verifiedAt: z.string().datetime({ offset: true }),
  state: z.literal('verified_original_lot_quarantined'), operation: z.literal(returnLotOperation) }).strict();
const wireLine = z.object({ receiptId: id, skuId: id, originalLotId: id, verifiedBaseUnits: positiveUnits }).strict();
const commandSchema = z.object({ actorId: id, caseId: id, returnId: id, invoiceId: id, revision: z.literal('0'), body: z.object({
  binding: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), observedLotLabel: text(160), confirmPhysicalOriginalLotMatch: z.literal(true),
  lines: z.array(wireLine).min(1).max(100).refine(uniqueLines), reason: text(1000) }).strict() }).strict();
export type ReturnLotView = z.infer<typeof view>;
export type ReturnLotChoice = z.infer<typeof choice>;
export type ReturnLotResult = z.infer<typeof result>;
type ParsedCommand = z.infer<typeof commandSchema>;
export type ReturnLotCommand = Readonly<Omit<ParsedCommand, 'body'> & { body: Readonly<Omit<ParsedCommand['body'], 'lines'> & {
  lines: readonly Readonly<ParsedCommand['body']['lines'][number]>[] }> }>;
function invalid(): never { throw new AccessFailure(502, 'return_lot_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
function inputInvalid(): never { throw new AccessFailure(400, 'invalid_return_lot_verification'); }
export function returnLotOwner(key: string) {
  const matched = /^cl\.([1-9][0-9]{0,18})\.([1-9][0-9]{0,18})\.[a-zA-Z0-9_-]{1,68}$(?![\s\S])/.exec(key);
  if (!matched || key.length > 128) return invalid(); return { caseId: parse(id, matched[1]), returnId: parse(id, matched[2]) };
}
export function parseReturnLotResult(value: unknown, key: string) {
  const parsed = parse(result, value), owner = returnLotOwner(key);
  if (parsed.caseId !== owner.caseId || parsed.returnId !== owner.returnId) return invalid(); return parsed;
}
export function parseReturnLotView(value: unknown, original: AfterSalesCase, inspected: InspectionView): ReturnLotView {
  const owner = parseCaseView(original), inspection = parseInspectionView(inspected, owner, inspected.receipt.id), parsed = parse(view, value);
  if (!inspection.inspection || parsed.caseId !== owner.id || parsed.returnId !== inspection.receipt.id ||
    (parsed.revision === '0') !== (parsed.state === 'belum_diverifikasi') || parsed.lines.length !== inspection.receipt.lines.length) return invalid();
  for (const item of parsed.lines) {
    const received = inspection.receipt.lines.find(row => row.receiptId === item.receiptId && row.skuId === item.skuId);
    const assessed = inspection.inspection.lines.find(row => row.receiptId === item.receiptId && row.skuId === item.skuId);
    const product = owner.source.lines.find(row => row.receiptId === item.receiptId && row.skuId === item.skuId);
    if (!received || !assessed || !product || received.receivedBaseUnits !== item.receivedBaseUnits ||
      BigInt(item.verifiedBaseUnits) + BigInt(item.manualBaseUnits) !== BigInt(item.receivedBaseUnits) ||
      BigInt(item.verifiedBaseUnits) % BigInt(product.baseUnitsPerPackage) !== BigInt(0) ||
      parsed.revision === '0' && (item.verifiedBaseUnits !== '0' || item.state === 'tetap_dikarantina') || parsed.revision === '1' && (item.choices.length !== 0 || item.state !== 'tetap_dikarantina') ||
      item.state === 'penanganan_manual' && item.choices.length !== 0 || item.state === 'menunggu_verifikasi_fisik' && item.choices.length === 0 ||
      new Set(item.choices.map(option => option.originalLotId)).size !== item.choices.length ||
      item.choices.length > 0 && (assessed.identity !== 'matched' || assessed.condition !== 'sealed' || BigInt(item.receivedBaseUnits) < BigInt(product.baseUnitsPerPackage)) ||
      item.choices.some(option => option.packageLabel !== product.packageLabel || option.baseUnitCode !== product.baseUnitCode ||
        option.baseUnitsPerPackage !== product.baseUnitsPerPackage || BigInt(option.dispatchedBaseUnits) % BigInt(option.baseUnitsPerPackage) !== BigInt(0))) return invalid();
  }
  return parsed;
}
export function prepareReturnLot(actorId: string, original: AfterSalesCase, inspection: InspectionView, preview: ReturnLotView,
  selected: { receiptId: string; skuId: string; originalLotId: string; packages: string }, observedLotLabel: string, reason: string): ReturnLotCommand {
  const reviewed = parseReturnLotView(preview, original, inspection);
  const label = text(160).safeParse(observedLotLabel), note = text(1000).safeParse(reason), packages = positiveUnits.safeParse(selected.packages);
  const selectedLine = reviewed.lines.find(row => row.receiptId === selected.receiptId && row.skuId === selected.skuId);
  const option = selectedLine?.choices.find(row => row.originalLotId === selected.originalLotId);
  if (reviewed.revision !== '0' || !label.success || !note.success || !packages.success || !selectedLine || !option || selectedLine.state !== 'menunggu_verifikasi_fisik') return inputInvalid();
  const amount = BigInt(packages.data) * BigInt(option.baseUnitsPerPackage);
  if (amount > BigInt(1000000) || amount > BigInt(selectedLine.receivedBaseUnits) || amount > BigInt(option.dispatchedBaseUnits)) return inputInvalid();
  return freezeCommand({ actorId: parse(id, actorId), caseId: original.id, returnId: reviewed.returnId, invoiceId: original.source.invoiceId, revision: '0',
    body: { binding: reviewed.binding, observedLotLabel: label.data, confirmPhysicalOriginalLotMatch: true,
      lines: [{ receiptId: selected.receiptId, skuId: selected.skuId, originalLotId: option.originalLotId, verifiedBaseUnits: amount.toString() }], reason: note.data } });
}
function freezeCommand(value: unknown): ReturnLotCommand {
  const parsed = commandSchema.safeParse(value); if (!parsed.success) return inputInvalid();
  return Object.freeze({ ...parsed.data, body: Object.freeze({ ...parsed.data.body, lines: Object.freeze(parsed.data.body.lines.map(item => Object.freeze(item))) }) }) as ReturnLotCommand;
}
type Path = `/api/v1/fulfillment/cases/${string}/returns/${string}/lot-verification${'' | '/query' | '/operations/query'}`;
export type ReturnLotRequest = Readonly<{ path: Path; headers: Readonly<{ 'X-Expected-Actor-Id': string; 'If-Match'?: string; 'Idempotency-Key'?: string }>;
  body: Readonly<Record<string, unknown>>; isCurrent: () => boolean; signal?: AbortSignal }>;
export type ReturnLotTransport = { send(request: ReturnLotRequest): Promise<Response> };
export function createReturnLotTransport(options: { fetch?: typeof globalThis.fetch; origin?: string } = {}): ReturnLotTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return { async send(request) {
    const matched = /^\/api\/v1\/fulfillment\/cases\/([1-9][0-9]{0,18})\/returns\/([1-9][0-9]{0,18})\/lot-verification(?:\/query|\/operations\/query)?$(?![\s\S])/.exec(request.path);
    if (!matched || !id.safeParse(matched[1]).success || !id.safeParse(matched[2]).success || !id.safeParse(request.headers['X-Expected-Actor-Id']).success)
      throw new RequestNotSentError(400, 'invalid_return_lot_verification');
    const origin = options.origin ?? window.location.origin, url = new URL(request.path, origin);
    if (url.origin !== new URL(origin).origin || typeof window !== 'undefined' && url.origin !== window.location.origin)
      throw new RequestNotSentError(400, 'invalid_origin');
    const before = () => { if (request.signal?.aborted || !request.isCurrent()) throw new RequestNotSentError(409, 'return_lot_view_changed'); };
    before(); const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, request.signal); before();
    return fetcher(new Request(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal,
      headers: { ...request.headers, 'X-CSRF-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(request.body) }));
  } };
}
async function json(response: Response): Promise<unknown> {
  const value: unknown = await response.json().catch(() => null);
  if (!response.ok) { const problem = value !== null && typeof value === 'object' ? value as Record<string, unknown> : {};
    throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed'); }
  return value;
}
function stillCurrent(current: () => boolean, signal?: AbortSignal) { if (signal?.aborted || !current()) throw new AccessFailure(409, 'return_lot_view_changed'); }
export function createReturnLotGateway(transport: ReturnLotTransport = createReturnLotTransport()) {
  const path = (caseId: string, returnId: string, suffix: '' | '/query' | '/operations/query'): Path =>
    `/api/v1/fulfillment/cases/${parse(id, caseId)}/returns/${parse(id, returnId)}/lot-verification${suffix}`;
  return {
    async read(actorId: string, owner: AfterSalesCase, inspection: InspectionView, current: () => boolean, signal?: AbortSignal) {
      stillCurrent(current, signal); const original = parseInspectionView(inspection, owner, inspection.receipt.id);
      const value = await json(await transport.send({ path: path(owner.id, original.receipt.id, '/query'),
        headers: { 'X-Expected-Actor-Id': parse(id, actorId), 'If-Match': '"2"' }, body: { purpose: returnLotPurpose }, isCurrent: current, signal }));
      stillCurrent(current, signal); return parseReturnLotView(value, owner, inspection);
    },
    async verify(value: ReturnLotCommand, key: string, current: () => boolean, signal?: AbortSignal) {
      const command = freezeCommand(value), owner = returnLotOwner(key);
      if (owner.caseId !== command.caseId || owner.returnId !== command.returnId) throw new RequestNotSentError(400, 'invalid_return_lot_verification');
      if (signal?.aborted || !current()) throw new RequestNotSentError(409, 'return_lot_view_changed');
      const response = await transport.send({ path: path(command.caseId, command.returnId, ''), headers: { 'X-Expected-Actor-Id': command.actorId,
        'If-Match': '"0"', 'Idempotency-Key': key }, body: command.body, isCurrent: current, signal });
      if (signal?.aborted || !current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const payload = await json(response); if (signal?.aborted || !current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const saved = parseReturnLotResult(payload, key); if (saved.invoiceId !== command.invoiceId) return invalid(); return saved;
    },
    async recover(actorId: string, key: string, current: () => boolean, signal?: AbortSignal) {
      const owner = returnLotOwner(key); stillCurrent(current, signal);
      const value = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), await json(await transport.send({
        path: path(owner.caseId, owner.returnId, '/operations/query'), headers: { 'X-Expected-Actor-Id': parse(id, actorId) },
        body: { purpose: 'operation-recovery', key }, isCurrent: current, signal })));
      stillCurrent(current, signal); if (value.status === 'not_confirmed' && value.result === null) return null;
      if (value.status !== 'committed' || value.result === null) return invalid(); return { state: 'committed' as const, result: parseReturnLotResult(value.result, key) };
    },
  };
}
