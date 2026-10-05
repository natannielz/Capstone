import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '@/api/client';
import {AccessFailure} from '@/modules/administration/workflow';
import {parsePaymentDraft, type PaymentDraft} from '@/modules/billing/payment-client';
import {readCheckedPdfOriginal, pdfFileAllowed, type PdfEvidenceStatus} from '@/modules/documents/pdf-evidence-client';
import {parsePdfDocumentOcr, pdfDocumentOcrOwnerKey, type PdfDocumentOcrOwner, type PdfDocumentOcrView} from '../documents/pdf-document-ocr-client';
import {supplementKeyBinding, supplementOperations, type SupplementKeyBinding, type SupplementOperation} from './payment-pdf-supplement-recovery';

const pattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(pattern).refine(value => BigInt(value) < BigInt('9223372036854775806'));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const evidence = z.object({id, version: z.literal('1'), sha256: hash}).strict();
const timestamp = z.string().datetime({offset: true}).refine(value => !value.startsWith('0000'));
const cleanText = (maximum: number) => z.string().min(1).max(maximum * 2).refine(value => value === value.trim() && value === value.normalize('NFC') &&
  Array.from(value).length <= maximum && !/[\p{Cc}\p{Cf}]/u.test(value) && !Array.from(value).some(character => character.length === 1 && /[\uD800-\uDFFF]/u.test(character)));
const parent = z.object({actorId: id, declarationId: id, declarationRevision: id, submissionId: id, primaryEvidence: evidence}).strict();
const supplement = z.object({declarationId: id, declarationRevision: id, submissionId: id, primaryEvidence: evidence,
  supplementId: id, supplementRevision: z.enum(['1', '2']), state: z.enum(['draft', 'attached']), reservationId: id.nullable(),
  pdfEvidence: evidence.nullable(), attachedAt: timestamp.nullable(), reason: cleanText(1000)}).strict().refine(value => value.state === 'draft' ?
  value.supplementRevision === '1' && value.reservationId === null && value.pdfEvidence === null && value.attachedAt === null :
  value.supplementRevision === '2' && value.reservationId !== null && value.pdfEvidence !== null && value.attachedAt !== null &&
  value.pdfEvidence.id !== value.primaryEvidence.id && value.pdfEvidence.sha256 !== value.primaryEvidence.sha256);
const reservation = z.object({reservationId: id, revision: z.literal('1'), expiresAt: timestamp}).strict();
const status = z.object({reservationId: id, revision: id, expiresAt: timestamp,
  state: z.enum(['fresh', 'writing', 'publish_pending', 'interrupted', 'quarantined']), reference: evidence.nullable(),
  scanStatus: z.enum(['unavailable', 'clean', 'infected']), decoderStatus: z.enum(['unavailable', 'decoded', 'invalid']),
  safeFailure: z.string().regex(/^[a-z_]{1,100}$(?![\s\S])/).nullable()}).strict().refine(value =>
    (value.state === 'quarantined') === (value.reference !== null) &&
    (value.state === 'quarantined' || value.scanStatus === 'unavailable' && value.decoderStatus === 'unavailable' && value.safeFailure === null) &&
    (value.decoderStatus !== 'decoded' || value.scanStatus === 'clean' && value.safeFailure === null) &&
    (value.decoderStatus !== 'invalid' || value.scanStatus === 'clean' && value.safeFailure === 'invalid_media') &&
    (value.scanStatus !== 'infected' || value.decoderStatus === 'unavailable' && ['infected_media', 'integrity_mismatch'].includes(value.safeFailure ?? '')));
const uploadResult = z.object({state: z.enum(['quarantined', 'interrupted', 'unresolved', 'unknown', 'publish_pending', 'recovery_required']), reference: evidence.nullable()}).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null));
const list = z.object({items: z.array(supplement).max(25), nextSupplementId: id.nullable(), observedAt: timestamp,
  readiness: z.enum(['allowed', 'policy_unavailable'])}).strict();
export type PaymentSupplementOwner = z.infer<typeof parent>;
export type PaymentSupplement = z.infer<typeof supplement>;
export type PaymentSupplementList = z.infer<typeof list>;
export type PaymentSupplementReservation = z.infer<typeof reservation>;
export type PaymentSupplementReadContext = 'requester' | 'reviewer';
export type PaymentSupplementOcr = {supplement: PaymentSupplement; view: PdfDocumentOcrView; owner: PdfDocumentOcrOwner};
const invalid = (): never => {throw new AccessFailure(502, 'payment_supplement_response_invalid');};
const equal = (first: unknown, second: unknown) => JSON.stringify(first) === JSON.stringify(second);
const parse = <T,>(shape: z.ZodType<T>, value: unknown): T => {const result = shape.safeParse(value); return result.success ? result.data : invalid();};
/** The owning gateway additionally verifies the immutable primary tuple; the journal contains no evidence or credentials. */
export function parseSupplementRecoveryResult(value: unknown, operation: SupplementOperation, key: string, actorId: string):
  Readonly<{id: string; version: string}> {
  const binding = supplementKeyBinding(operation, key, actorId);
  if (operation === supplementOperations.reserve) {
    const result = parse(reservation, value); return {id: result.reservationId, version: result.revision};
  }
  const result = parse(supplement, value);
  if (result.declarationId !== binding.declarationId || result.submissionId !== binding.submissionId ||
    binding.supplementId !== null && result.supplementId !== binding.supplementId ||
    result.state !== (operation === supplementOperations.create ? 'draft' : 'attached')) return invalid();
  return {id: result.supplementId, version: result.supplementRevision};
}
export function paymentSupplementOwner(actorId: string, draftInput: PaymentDraft): PaymentSupplementOwner {
  const draft = parsePaymentDraft(draftInput);
  if (draft.state !== 'submitted' || !draft.submission) throw new RequestNotSentError(409, 'payment_primary_image_required');
  return parse(parent, {actorId, declarationId: draft.id, declarationRevision: draft.revision, submissionId: draft.submission.id, primaryEvidence: draft.submission.evidence});
}
export const paymentSupplementOwnerKey = (value: PaymentSupplementOwner) => JSON.stringify(parse(parent, value));
export function parsePaymentSupplement(value: unknown, parentInput: PaymentSupplementOwner, expectedSupplementId?: string): PaymentSupplement {
  const owner = parse(parent, parentInput), shown = parse(supplement, value);
  if (shown.declarationId !== owner.declarationId || shown.declarationRevision !== owner.declarationRevision || shown.submissionId !== owner.submissionId ||
    !equal(shown.primaryEvidence, owner.primaryEvidence) || expectedSupplementId !== undefined && shown.supplementId !== parse(id, expectedSupplementId)) return invalid();
  return shown;
}
export function parsePaymentSupplementList(value: unknown, owner: PaymentSupplementOwner, beforeId: string | null): PaymentSupplementList {
  const shown = parse(list, value), before = beforeId === null ? null : parse(id, beforeId);
  shown.items.forEach((item, index) => {parsePaymentSupplement(item, owner);
    if (before !== null && BigInt(item.supplementId) >= BigInt(before) || index > 0 && BigInt(item.supplementId) >= BigInt(shown.items[index - 1].supplementId)) return invalid();});
  if (shown.nextSupplementId !== null && (shown.items.length !== 25 || shown.nextSupplementId !== shown.items.at(-1)?.supplementId)) return invalid(); return shown;
}
export function parsePaymentSupplementResult(value: unknown, operation: typeof supplementOperations.create | typeof supplementOperations.attach,
  owner: PaymentSupplementOwner, expectedSupplementId?: string) {
  const shown = parse(z.object({operation: z.literal(operation), result: supplement}).strict(), value);
  const saved = parsePaymentSupplement(shown.result, owner, expectedSupplementId);
  if (saved.state !== (operation === supplementOperations.create ? 'draft' : 'attached')) return invalid(); return saved;
}
export function supplementPdfOwner(ownerInput: PaymentSupplementOwner, supplementInput: PaymentSupplement): PdfDocumentOcrOwner {
  const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(supplementInput, owner);
  if (saved.state !== 'attached' || !saved.pdfEvidence) throw new RequestNotSentError(409, 'payment_supplement_not_attached');
  const source: PdfDocumentOcrOwner = {actorId: owner.actorId, sourceKind: 'PaymentSupplement', sourceId: saved.supplementId,
    sourceRevision: saved.supplementRevision, submissionId: saved.supplementId, evidence: saved.pdfEvidence}; pdfDocumentOcrOwnerKey(source); return source;
}
export function parsePaymentSupplementOcr(value: unknown, owner: PaymentSupplementOwner, supplementInput: PaymentSupplement): PaymentSupplementOcr {
  const saved = parsePaymentSupplement(supplementInput, owner), source = supplementPdfOwner(owner, saved);
  const wrapper = parse(z.object({declarationId: id, declarationRevision: id, submissionId: id, primaryEvidence: evidence,
    supplementId: id, supplementRevision: id, pdfEvidence: evidence, ocr: z.unknown()}).strict(), value);
  if (wrapper.declarationId !== saved.declarationId || wrapper.declarationRevision !== saved.declarationRevision || wrapper.submissionId !== saved.submissionId ||
    !equal(wrapper.primaryEvidence, saved.primaryEvidence) || wrapper.supplementId !== saved.supplementId || wrapper.supplementRevision !== saved.supplementRevision ||
    !equal(wrapper.pdfEvidence, saved.pdfEvidence)) return invalid();
  return {supplement: saved, owner: source, view: parsePdfDocumentOcr(wrapper.ocr, source)};
}
export function supplementReason(value: string): string {const chosen = cleanText(1000).safeParse(value.trim().normalize('NFC'));
  if (!chosen.success) throw new RequestNotSentError(400, 'invalid_payment_supplement_reason'); return chosen.data;}
export function supplementBinding(owner: PaymentSupplementOwner, saved: PaymentSupplement | null): SupplementKeyBinding {
  const parentOwner = parse(parent, owner), record = saved === null ? null : parsePaymentSupplement(saved, parentOwner);
  return {actorId: parentOwner.actorId, declarationId: parentOwner.declarationId, submissionId: parentOwner.submissionId, supplementId: record?.supplementId ?? null};
}
export function paymentSupplementError(failure: unknown): string {
  if (!(failure instanceof AccessFailure || failure instanceof RequestNotSentError)) return 'Hasil belum dapat dipastikan. Muat sumber terbaru atau periksa upaya yang sama; jangan kirim ulang.';
  const labels: Record<string, string> = {
    payment_supplement_response_invalid: 'Identitas atau kelengkapan lampiran belum sesuai. Muat pengajuan foto dan lampiran terbaru.',
    payment_primary_image_required: 'Bukti foto utama yang memenuhi syarat harus diajukan lebih dahulu. PDF tidak dapat menggantikannya.',
    payment_supplement_policy_unavailable: 'Lampiran PDF belum diizinkan oleh kebijakan yang disetujui. Pengajuan foto utama tetap tersedia.',
    payment_supplement_limit: 'Jumlah lampiran tambahan telah mencapai batas kebijakan unit. Hubungi penanggung jawab; bukti foto utama tetap digunakan.',
    idempotency_key_reused: 'Rujukan upaya telah terikat pada permintaan lain. Muat pengajuan asal dan periksa rujukan yang sama; jangan mengirim ulang.',
    payment_supplement_view_stale: 'Sesi atau sumber berubah. Muat pengajuan dan lampiran terbaru sebelum melanjutkan.',
    resolve_previous_attempt: 'Ada upaya yang belum pasti. Periksa rujukan upaya tersebut sebelum membuat upaya baru.',
    commit_outcome_unknown: 'Hasil penyimpanan belum pasti. Periksa upaya yang sama tanpa mengirim ulang.',
    recovery_storage_unavailable: 'Rujukan pemulihan belum dapat disimpan di browser. Periksa penyimpanan atau sesi sebelum melanjutkan.',
    explicit_pdf_required: 'Pilih satu berkas PDF asli, tidak kosong dan paling besar 10 MiB.',
    invalid_payment_supplement_reason: 'Isi alasan lampiran paling banyak 1.000 karakter yang dapat dibaca, tanpa karakter kontrol atau tersembunyi.',
  };
  if (labels[failure.code]) return labels[failure.code];
  if (failure.status === 401 || failure.status === 403) return 'Sesi atau izin belum memenuhi syarat untuk lampiran ini. Masuk kembali atau hubungi penanggung jawab.';
  if (failure.status === 409) return 'Lampiran atau sumber telah berubah. Muat data terbaru; jangan mengulang upaya yang belum pasti.';
  if (failure.status === 400 || failure.status === 413 || failure.status === 422) return 'Isian atau PDF belum memenuhi syarat. Periksa berkas, alasan dan batas yang ditampilkan.';
  return 'Lampiran belum dapat dimuat dengan aman. Periksa upaya yang sama atau hubungi penanggung jawab.';
}

export function createPaymentSupplementGateway(options: {fetcher?: typeof fetch; baseUrl?: string} = {}) {
  const fetcher = options.fetcher ?? globalThis.fetch, base = options.baseUrl ?? (typeof window === 'undefined' ? undefined : window.location.origin);
  const path = (context: PaymentSupplementReadContext, owner: PaymentSupplementOwner) => `/api/v1/billing/${context === 'requester' ? 'payment-declarations' : 'payment-reviews'}/${owner.declarationId}/supplements`;
  const purpose = (context: PaymentSupplementReadContext, action: 'list' | 'detail' | 'view' | 'ocr') =>
    `${context === 'requester' ? 'payment-' : 'payment-review-'}${action === 'list' ? 'supplements-list' : action === 'detail' ? 'supplement-detail' : `supplement-pdf-${action === 'ocr' ? 'ocr-view' : 'view'}`}`;
  const requireCurrent = (current: () => boolean) => {if (!current()) throw new RequestNotSentError(409, 'payment_supplement_view_stale');};
  const committedCurrent = (current: () => boolean) => {if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');};
  async function post(endpointPath: string, owner: PaymentSupplementOwner, body: unknown, revision: string, current: () => boolean,
    extras: {key?: string; signal?: AbortSignal; file?: File} = {}) {
    requireCurrent(current); if (!base) throw new RequestNotSentError(400, 'invalid_origin'); const endpoint = new URL(endpointPath, base);
    if (typeof window !== 'undefined' && endpoint.origin !== window.location.origin) throw new RequestNotSentError(400, 'invalid_origin');
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', endpoint).href, fetcher, extras.signal); requireCurrent(current);
    const headers: Record<string, string> = {'X-Expected-Actor-Id': owner.actorId, 'X-CSRF-Token': token, 'If-Match': `"${parse(id, revision)}"`,
      'Content-Type': extras.file ? 'application/octet-stream' : 'application/json'};
    if (extras.key) headers['Idempotency-Key'] = extras.key;
    const response = await fetcher(new Request(endpoint, {method: 'POST', body: extras.file ?? JSON.stringify(body), headers,
      signal: extras.signal, credentials: 'same-origin', redirect: 'error', cache: 'no-store'}));
    if (!response.ok) {let code = 'request_failed'; try {const failure: unknown = await response.json(); if (failure && typeof failure === 'object' &&
      'code' in failure && typeof failure.code === 'string' && /^[a-z_]{1,100}$/.test(failure.code)) code = failure.code;} catch { /* No raw response text reaches the UI. */ }
      throw new AccessFailure(response.status, code);} return response;
  }
  async function json(response: Response): Promise<unknown> {try {const value: unknown = await response.json();
    if (new TextEncoder().encode(JSON.stringify(value)).length > 2 * 1024 * 1024) return invalid(); return value;} catch {return invalid();}}
  const checkedKey = (operation: SupplementOperation, owner: PaymentSupplementOwner, key: string, saved: PaymentSupplement | null) => {
    const actual = supplementKeyBinding(operation, key, owner.actorId), expected = supplementBinding(owner, saved);
    if (!equal(actual, expected)) return invalid(); return key;
  };
  return {
    async list(context: PaymentSupplementReadContext, ownerInput: PaymentSupplementOwner, beforeId: string | null, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(parent, ownerInput), before = beforeId === null ? null : parse(id, beforeId);
      const raw = await json(await post(`${path(context, owner)}/query`, owner, {purpose: purpose(context, 'list'), beforeId: before}, owner.declarationRevision, current, {signal}));
      requireCurrent(current); return parsePaymentSupplementList(raw, owner, before);
    },
    async detail(context: PaymentSupplementReadContext, ownerInput: PaymentSupplementOwner, supplementId: string, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(parent, ownerInput), selected = parse(id, supplementId);
      const raw = await json(await post(`${path(context, owner)}/${selected}/query`, owner, {purpose: purpose(context, 'detail')}, owner.declarationRevision, current, {signal}));
      requireCurrent(current); return parsePaymentSupplement(raw, owner, selected);
    },
    async create(ownerInput: PaymentSupplementOwner, key: string, reason: string, current: () => boolean) {
      const owner = parse(parent, ownerInput); checkedKey(supplementOperations.create, owner, key, null);
      const raw = await json(await post(path('requester', owner), owner, {submissionId: owner.submissionId, primaryEvidence: owner.primaryEvidence, reason: supplementReason(reason)},
        owner.declarationRevision, current, {key})); committedCurrent(current); return parsePaymentSupplementResult(raw, supplementOperations.create, owner);
    },
    async attach(ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, statusInput: PdfEvidenceStatus, reason: string, key: string, current: () => boolean) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner), selected = parse(status, statusInput);
      if (saved.state !== 'draft' || selected.state !== 'quarantined' || !selected.reference || selected.scanStatus !== 'clean' || selected.decoderStatus !== 'decoded' || selected.safeFailure !== null)
        throw new RequestNotSentError(409, 'payment_supplement_not_ready');
      checkedKey(supplementOperations.attach, owner, key, saved);
      const raw = await json(await post(`${path('requester', owner)}/${saved.supplementId}/attach`, owner,
        {reservationId: selected.reservationId, evidence: selected.reference, reason: supplementReason(reason)}, saved.supplementRevision, current, {key}));
      committedCurrent(current); const result = parsePaymentSupplementResult(raw, supplementOperations.attach, owner, saved.supplementId);
      if (result.reservationId !== selected.reservationId || !equal(result.pdfEvidence, selected.reference)) return invalid(); return result;
    },
    async recoverCommand(operation: typeof supplementOperations.create | typeof supplementOperations.attach, ownerInput: PaymentSupplementOwner,
      key: string, savedInput: PaymentSupplement | null, current: () => boolean) {
      const owner = parse(parent, ownerInput), saved = savedInput === null ? null : parsePaymentSupplement(savedInput, owner);
      checkedKey(operation, owner, key, saved); if (operation === supplementOperations.attach && !saved) return invalid();
      const endpoint = operation === supplementOperations.create ? `${path('requester', owner)}/operations/query` : `${path('requester', owner)}/${saved!.supplementId}/attachment/operations/query`;
      const raw = await json(await post(endpoint, owner, {purpose: 'operation-recovery', key}, saved?.supplementRevision ?? owner.declarationRevision, current)); requireCurrent(current);
      const receipt = parse(z.object({status: z.enum(['not_confirmed', 'committed']), result: supplement.nullable()}).strict(), raw);
      if (receipt.status === 'not_confirmed' && receipt.result === null) return null;
      if (receipt.status !== 'committed' || !receipt.result) return invalid();
      const result = parsePaymentSupplement(receipt.result, owner, saved?.supplementId);
      if (result.state !== (operation === supplementOperations.create ? 'draft' : 'attached')) return invalid(); return result;
    },
    async reservations(ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, beforeId: string | null, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner), before = beforeId === null ? null : parse(id, beforeId);
      if (saved.state !== 'draft') throw new RequestNotSentError(409, 'payment_supplement_already_attached');
      const raw = await json(await post(`${path('requester', owner)}/${saved.supplementId}/pdf-evidence/query`, owner,
        {purpose: 'payment-supplement-pdf-status', beforeId: before}, saved.supplementRevision, current, {signal})); requireCurrent(current);
      const shown = parse(z.object({items: z.array(status).max(25)}).strict(), raw);
      if (!shown.items.every((item, index) => (before === null || BigInt(item.reservationId) < BigInt(before)) &&
        (index === 0 || BigInt(item.reservationId) < BigInt(shown.items[index - 1].reservationId)))) return invalid(); return shown.items;
    },
    async reserve(ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, key: string, current: () => boolean) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner); checkedKey(supplementOperations.reserve, owner, key, saved);
      if (saved.state !== 'draft') throw new RequestNotSentError(409, 'payment_supplement_already_attached');
      const raw = await json(await post(`${path('requester', owner)}/${saved.supplementId}/pdf-evidence`, owner,
        {reason: 'Unggah lampiran PDF tambahan; bukti foto utama tetap wajib'}, saved.supplementRevision, current, {key})); committedCurrent(current); return parse(reservation, raw);
    },
    async recoverReservation(ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, key: string, current: () => boolean) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner); checkedKey(supplementOperations.reserve, owner, key, saved);
      const raw = await json(await post(`${path('requester', owner)}/${saved.supplementId}/pdf-evidence/operations/query`, owner,
        {purpose: 'operation-recovery', key}, saved.supplementRevision, current)); requireCurrent(current);
      const shown = parse(z.object({status: z.enum(['not_confirmed', 'committed']), reservation: reservation.nullable()}).strict(), raw);
      if (shown.status === 'not_confirmed' && shown.reservation === null) return null;
      return shown.status === 'committed' && shown.reservation ? shown.reservation : invalid();
    },
    async upload(ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, reservedInput: PaymentSupplementReservation, file: File, current: () => boolean) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner), reserved = parse(reservation, reservedInput); pdfFileAllowed(file, 'pdf');
      if (saved.state !== 'draft') throw new RequestNotSentError(409, 'payment_supplement_already_attached');
      const raw = await json(await post(`${path('requester', owner)}/${saved.supplementId}/pdf-evidence/${reserved.reservationId}/content`, owner,
        null, saved.supplementRevision, current, {key: `upload.${reserved.reservationId}`, file})); committedCurrent(current); return parse(uploadResult, raw);
    },
    async recoverUpload(ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, reservationId: string, current: () => boolean) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner), selected = parse(id, reservationId);
      const raw = await json(await post(`${path('requester', owner)}/${saved.supplementId}/pdf-evidence/${selected}/recover`, owner,
        {purpose: 'upload-recovery', reason: 'Periksa upaya unggah lampiran PDF tambahan yang sama'}, saved.supplementRevision, current, {key: `upload.${selected}`}));
      committedCurrent(current); return parse(uploadResult, raw);
    },
    async original(context: PaymentSupplementReadContext, ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner); supplementPdfOwner(owner, saved);
      const response = await post(`${path(context, owner)}/${saved.supplementId}/pdf-evidence/${saved.reservationId}/original/query`, owner,
        {purpose: purpose(context, 'view'), reference: saved.pdfEvidence}, saved.supplementRevision, current, {signal});
      return readCheckedPdfOriginal(response, saved.pdfEvidence!, current);
    },
    async ocr(context: PaymentSupplementReadContext, ownerInput: PaymentSupplementOwner, savedInput: PaymentSupplement, current: () => boolean, signal?: AbortSignal) {
      const owner = parse(parent, ownerInput), saved = parsePaymentSupplement(savedInput, owner); supplementPdfOwner(owner, saved);
      const raw = await json(await post(`${path(context, owner)}/${saved.supplementId}/pdf-ocr/query`, owner,
        {purpose: purpose(context, 'ocr'), reference: saved.pdfEvidence}, saved.supplementRevision, current, {signal})); requireCurrent(current);
      return parsePaymentSupplementOcr(raw, owner, saved);
    },
  };
}
export type PaymentSupplementGateway = ReturnType<typeof createPaymentSupplementGateway>;
