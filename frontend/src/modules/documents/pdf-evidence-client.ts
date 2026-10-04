import { z } from 'zod';
import { fetchCsrfToken, RequestNotSentError } from '@/api/client';
import { AccessFailure } from '@/modules/administration/workflow';
import { parsePdfDocumentOcr, pdfDocumentOcrOwnerKey, type PdfDocumentOcrOwner } from './pdf-document-ocr-client';

const numberPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(numberPattern).refine(value => numberPattern.test(value) && BigInt(value) < BigInt('9223372036854775807'));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const reference = z.object({id, version: id, sha256: hash}).strict();
const target = z.object({actorId: id, sourceId: id, sourceRevision: id, sourceKind: z.enum(['Payment', 'SupplierInvoice'])}).strict();
const recoveryTarget = target.omit({sourceRevision: true});
const reservation = z.object({reservationId: id, revision: z.literal('1'), expiresAt: z.string().datetime({offset: true})}).strict();
const status = z.object({reservationId: id, revision: id, expiresAt: z.string().datetime({offset: true}),
  state: z.enum(['fresh', 'writing', 'publish_pending', 'interrupted', 'quarantined']), reference: reference.nullable(),
  scanStatus: z.enum(['unavailable', 'clean', 'infected']), decoderStatus: z.enum(['unavailable', 'decoded', 'invalid']), safeFailure: z.string().max(100).nullable()}).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null) &&
    (value.state === 'quarantined' || value.scanStatus === 'unavailable' && value.decoderStatus === 'unavailable' && value.safeFailure === null) &&
    (value.decoderStatus !== 'decoded' || value.scanStatus === 'clean' && value.safeFailure === null));
const outcome = z.object({state: z.enum(['quarantined', 'interrupted', 'unresolved', 'unknown', 'publish_pending', 'recovery_required']), reference: reference.nullable()}).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null));
const format = z.object({sourceId: id, sourceRevision: id, evidence: reference, kind: z.enum(['image', 'pdf'])}).strict();
const text = z.string().trim().min(1).max(1000).refine(value => value === value.normalize('NFC') && !/[\p{Cc}\p{Cf}]/u.test(value));
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
export type PdfEvidenceTarget = z.infer<typeof target>;
export type PdfEvidenceRecoveryTarget = z.infer<typeof recoveryTarget>;
export type PdfEvidenceStatus = z.infer<typeof status>;
export type PdfEvidenceReservation = z.infer<typeof reservation>;
export type SubmittedEvidenceFormat = z.infer<typeof format>;
export type PdfReadContext = 'payment-requester' | 'payment-reviewer' | 'supplier-document';
const invalid = (): never => { throw new AccessFailure(502, 'pdf_evidence_response_invalid'); };
function parse<T>(shape: z.ZodType<T>, value: unknown): T { try { const parsed = shape.safeParse(value); if (parsed.success) return parsed.data; } catch { /* Never repair wire data. */ } return invalid(); }
const equal = (first: unknown, second: unknown) => JSON.stringify(first) === JSON.stringify(second);
const requireCurrent = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'pdf_view_stale'); };
const afterWrite = (current: () => boolean) => { if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); };
const ownerRoute = (value: Pick<PdfEvidenceTarget, 'sourceKind' | 'sourceId'>) => value.sourceKind === 'Payment' ? `/api/v1/billing/payment-declarations/${value.sourceId}` : `/api/v1/procurement/supplier-documents/drafts/${value.sourceId}`;
const contextRoute = (context: PdfReadContext, value: PdfEvidenceTarget) => {
  if ((context === 'supplier-document') !== (value.sourceKind === 'SupplierInvoice')) throw new RequestNotSentError(400, 'pdf_read_context_invalid');
  return context === 'payment-reviewer' ? `/api/v1/billing/payment-reviews/${value.sourceId}` : ownerRoute(value);
};
const evidencePurpose = (owner: PdfEvidenceTarget) => owner.sourceKind === 'Payment' ? 'payment-pdf-evidence' : 'supplier-document-pdf-evidence';
const formatPurpose = (context: PdfReadContext) => ({'payment-requester': 'payment-evidence-format-view', 'payment-reviewer': 'payment-review-evidence-format-view', 'supplier-document': 'supplier-document-evidence-format-view'})[context];
const ocrPurpose = (context: PdfReadContext) => ({'payment-requester': 'payment-pdf-ocr-view', 'payment-reviewer': 'payment-review-pdf-ocr-view', 'supplier-document': 'supplier-document-pdf-ocr-view'})[context];
export function pdfEvidenceTargetFor(owner: PdfDocumentOcrOwner): PdfEvidenceTarget {
  pdfDocumentOcrOwnerKey(owner); return parse(target, {actorId: owner.actorId, sourceId: owner.sourceId, sourceRevision: owner.sourceRevision, sourceKind: owner.sourceKind});
}
export const pdfReserveOperation = (owner: PdfEvidenceTarget) => owner.sourceKind === 'Payment' ? 'evidence.payment-pdf.reserve' as const : 'evidence.supplier-document-pdf.reserve' as const;
export function pdfReservationKey(ownerInput: PdfEvidenceTarget, requestUuid: string): string {
  const owner = parse(target, ownerInput), key = `pdf.${owner.sourceKind === 'Payment' ? 'p' : 's'}.${owner.actorId}.${owner.sourceId}.${requestUuid}`;
  pdfReservationKeyOwner(key, owner.actorId, owner.sourceKind); return key;
}
export function pdfReservationKeyOwner(key: string, actorId: string, sourceKind: PdfEvidenceTarget['sourceKind']) {
  const match = new RegExp(`^pdf\\.([ps])\\.([1-9][0-9]{0,18})\\.([1-9][0-9]{0,18})\\.(${uuid})$(?![\\s\\S])`).exec(key);
  if (!match || parse(id, actorId) !== parse(id, match[2]) || match[1] !== (sourceKind === 'Payment' ? 'p' : 's')) return invalid();
  return parse(id, match[3]);
}
export function pdfFileAllowed(file: Pick<File, 'size' | 'type'>, chosenFormat: 'image' | 'pdf' | null): void {
  if (chosenFormat !== 'pdf' || file.type !== 'application/pdf' || !Number.isSafeInteger(file.size) || file.size <= 0 || file.size > 10 * 1024 * 1024)
    throw new RequestNotSentError(400, 'explicit_pdf_required');
}
export function pdfEvidenceReady(value: PdfEvidenceStatus): boolean { const checked = parse(status, value); return checked.state === 'quarantined' && checked.reference !== null &&
  checked.scanStatus === 'clean' && checked.decoderStatus === 'decoded' && checked.safeFailure === null; }
export function pdfEvidenceLabel(value: PdfEvidenceStatus) {
  if (pdfEvidenceReady(value)) return 'Pemeriksaan keamanan PDF selesai; periksa seluruh dokumen asli.';
  if (value.scanStatus === 'infected') return 'PDF ditolak oleh pemeriksaan keamanan. Hubungi penanggung jawab atau gunakan dokumen asli yang aman.';
  if (value.safeFailure !== null || value.decoderStatus === 'invalid') return 'PDF belum lolos pemeriksaan keamanan atau struktur; dokumen ini tidak dapat diajukan.';
  return ({fresh: 'Reservasi PDF siap menerima berkas', writing: 'Unggah PDF belum terkonfirmasi', publish_pending: 'Penyimpanan PDF belum terkonfirmasi',
    interrupted: 'Unggah PDF terputus; periksa upaya yang sama', quarantined: 'PDF tersimpan; menunggu pemeriksaan keamanan dan struktur'})[value.state];
}

export function createPdfEvidenceGateway(options: {fetcher?: typeof fetch; baseUrl?: string} = {}) {
  const fetcher = options.fetcher ?? globalThis.fetch;
  const base = options.baseUrl ?? (typeof window === 'undefined' ? undefined : window.location.origin);
  const url = (path: string) => { if (!base) throw new RequestNotSentError(400, 'invalid_origin'); const chosen = new URL(path, base);
    if (typeof window !== 'undefined' && chosen.origin !== window.location.origin) throw new RequestNotSentError(400, 'invalid_origin'); return chosen; };
  async function post(path: string, owner: Pick<PdfEvidenceTarget, 'actorId'>, body: unknown, current: () => boolean, extras: {key?: string; revision?: string; signal?: AbortSignal; file?: File} = {}) {
    requireCurrent(current); const endpoint = url(path), token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', endpoint).href, fetcher, extras.signal); requireCurrent(current);
    const headers: Record<string, string> = {'X-Expected-Actor-Id': owner.actorId, 'X-CSRF-Token': token, 'Content-Type': extras.file ? 'application/octet-stream' : 'application/json'};
    if (extras.key) headers['Idempotency-Key'] = extras.key; if (extras.revision) headers['If-Match'] = `"${extras.revision}"`;
    const response = await fetcher(new Request(endpoint, {method: 'POST', body: extras.file ?? JSON.stringify(body), headers,
      signal: extras.signal, credentials: 'same-origin', redirect: 'error', cache: 'no-store'}));
    if (!response.ok) { let code = 'request_failed'; try { const problem: unknown = await response.json(); if (problem && typeof problem === 'object' &&
      'code' in problem && typeof problem.code === 'string' && /^[a-z_]{1,100}$/.test(problem.code)) code = problem.code; } catch { /* Safe fallback. */ }
      throw new AccessFailure(response.status, code); } return response;
  }
  async function json(response: Response) { try { return await response.json() as unknown; } catch { return invalid(); } }
  return {
    async list(ownerInput: PdfEvidenceTarget, beforeId: string | null, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(target, ownerInput), before = beforeId === null ? null : parse(id, beforeId);
      const shown = parse(z.object({items: z.array(status).max(25)}).strict(), await json(await post(`${ownerRoute(owner)}/pdf-evidence/query`, owner,
        {purpose: evidencePurpose(owner), beforeId: before}, current, {signal}))); requireCurrent(current);
      if (!shown.items.every((item, index) => (before === null || BigInt(item.reservationId) < BigInt(before)) &&
        (index === 0 || BigInt(item.reservationId) < BigInt(shown.items[index - 1].reservationId)))) return invalid(); return shown.items;
    },
    async reserve(ownerInput: PdfEvidenceTarget, key: string, reason: string, current: () => boolean) {
      const owner = parse(target, ownerInput); if (pdfReservationKeyOwner(key, owner.actorId, owner.sourceKind) !== owner.sourceId) return invalid();
      const shown = parse(reservation, await json(await post(`${ownerRoute(owner)}/pdf-evidence`, owner, {reason: parse(text, reason)}, current,
        {key, revision: owner.sourceRevision}))); afterWrite(current); return shown;
    },
    async recoverReservation(ownerInput: PdfEvidenceRecoveryTarget, key: string, current: () => boolean) {
      const owner = parse(recoveryTarget, ownerInput); if (pdfReservationKeyOwner(key, owner.actorId, owner.sourceKind) !== owner.sourceId) return invalid();
      const shown = parse(z.object({status: z.enum(['not_confirmed', 'committed']), reservation: reservation.nullable()}).strict(), await json(await post(
        `${ownerRoute(owner)}/pdf-evidence/operations/query`, owner, {purpose: 'operation-recovery', key}, current))); requireCurrent(current);
      if (shown.status === 'not_confirmed' && shown.reservation === null) return null;
      return shown.status === 'committed' && shown.reservation !== null ? {state: 'committed' as const, result: shown.reservation} : invalid();
    },
    async upload(ownerInput: PdfEvidenceTarget, reservedInput: PdfEvidenceReservation, file: File, chosenFormat: 'image' | 'pdf' | null, current: () => boolean) {
      const owner = parse(target, ownerInput), reserved = parse(reservation, reservedInput); pdfFileAllowed(file, chosenFormat);
      const shown = parse(outcome, await json(await post(`${ownerRoute(owner)}/pdf-evidence/${reserved.reservationId}/content`, owner, null, current,
        {key: `upload.${reserved.reservationId}`, revision: reserved.revision, file}))); afterWrite(current); return shown;
    },
    async recoverUpload(ownerInput: PdfEvidenceTarget, reservationId: string, current: () => boolean) {
      const owner = parse(target, ownerInput), reserved = parse(id, reservationId);
      const shown = parse(outcome, await json(await post(`${ownerRoute(owner)}/pdf-evidence/${reserved}/recover`, owner,
        {purpose: 'upload-recovery', reason: 'Periksa hasil upaya unggah PDF asli yang sama'}, current, {key: `upload.${reserved}`}))); afterWrite(current); return shown;
    },
    async format(context: PdfReadContext, ownerInput: PdfDocumentOcrOwner, current: () => boolean, signal?: AbortSignal) {
      const owner = pdfEvidenceTargetFor(ownerInput), expected = parse(reference, ownerInput.evidence);
      const shown = parse(format, await json(await post(`${contextRoute(context, owner)}/evidence-format/query`, owner,
        {purpose: formatPurpose(context), reference: expected}, current, {revision: owner.sourceRevision, signal}))); requireCurrent(current);
      if (shown.sourceId !== owner.sourceId || shown.sourceRevision !== owner.sourceRevision || !equal(shown.evidence, expected)) return invalid(); return shown;
    },
    async ocr(context: PdfReadContext, ownerInput: PdfDocumentOcrOwner, current: () => boolean, signal?: AbortSignal) {
      const owner = pdfEvidenceTargetFor(ownerInput), expected = parse(reference, ownerInput.evidence);
      const raw = await json(await post(`${contextRoute(context, owner)}/pdf-ocr/query`, owner,
        {purpose: ocrPurpose(context), reference: expected}, current, {revision: owner.sourceRevision, signal})); requireCurrent(current);
      return parsePdfDocumentOcr(raw, ownerInput);
    },
    async original(context: PdfReadContext, ownerInput: PdfEvidenceTarget, reservationId: string, expectedInput: z.infer<typeof reference>, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(target, ownerInput), expected = parse(reference, expectedInput), reserved = parse(id, reservationId);
      const response = await post(`${contextRoute(context, owner)}/pdf-evidence/${reserved}/original/query`, owner,
        {purpose: owner.sourceKind === 'Payment' ? 'payment-evidence-view' : 'supplier-document-evidence-view', reference: expected}, current, {revision: owner.sourceRevision, signal});
      return readCheckedPdfOriginal(response, expected, current);
    },
  };
}

/** Only security-approved original bytes enter a transient viewer/download; never a guessed MIME or public URL. */
export async function readCheckedPdfOriginal(response: Response, expectedInput: z.infer<typeof reference>, current: () => boolean): Promise<Blob> {
  const expected = parse(reference, expectedInput), chunks: Uint8Array[] = []; let reader: ReadableStreamDefaultReader<Uint8Array> | null = null;
  try {
    requireCurrent(current); const length = response.headers.get('content-length');
    if (!response.ok || response.headers.get('content-type')?.toLowerCase() !== 'application/pdf' || !length || !/^[1-9][0-9]{0,7}$/.test(length) ||
      Number(length) > 10 * 1024 * 1024 || !response.headers.get('cache-control')?.includes('private') || !response.headers.get('cache-control')?.includes('no-store') ||
      response.headers.get('x-content-type-options')?.toLowerCase() !== 'nosniff' || !response.headers.get('content-security-policy')?.includes("default-src 'none'") ||
      !response.headers.get('content-security-policy')?.includes('sandbox') || !/^attachment;/i.test(response.headers.get('content-disposition') ?? '') || !response.body) return invalid();
    reader = response.body.getReader(); let received = 0;
    while (true) { requireCurrent(current); const chunk = await reader.read(); requireCurrent(current); if (chunk.done) break; received += chunk.value.byteLength;
      if (received > 10 * 1024 * 1024 || received > Number(length)) return invalid(); chunks.push(chunk.value); }
    if (received !== Number(length) || received < 8) return invalid();
    const bytes = new Uint8Array(received); let offset = 0; for (const chunk of chunks) {bytes.set(chunk, offset); offset += chunk.byteLength;}
    if (new TextDecoder('ascii').decode(bytes.subarray(0, 5)) !== '%PDF-') return invalid();
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)); requireCurrent(current);
    if (Array.from(digest, byte => byte.toString(16).padStart(2, '0')).join('') !== expected.sha256) return invalid();
    return new Blob([bytes], {type: 'application/pdf'});
  } catch (failure) { if (reader) {try {await reader.cancel();} catch { /* Best effort private stream cleanup. */ }}
    else if (response.body && !response.body.locked) {try {await response.body.cancel();} catch { /* Best effort. */ }} throw failure;
  } finally { reader?.releaseLock(); }
}
