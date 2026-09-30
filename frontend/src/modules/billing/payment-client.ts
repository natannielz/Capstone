import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const money = z.string().regex(/^(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?$(?![\s\S])/);
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(value));
const timestamp = z.string().datetime({ offset: true });
const day = z.string().regex(/^\d{4}-\d{2}-\d{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value && !value.startsWith('0000'));
const invoiceIds = z.array(id).min(1).max(20).refine(values => new Set(values).size === values.length);
const input = z.object({ invoiceIds, declaredAmount: money.refine(value => /[1-9]/.test(value)), transferDate: day, payerName: text(160), bankReference: text(160).nullable(), note: text(1000).nullable() }).strict();
const invoice = z.object({ invoiceId: id, number: text(100), total: money, buyerName: text(1000), sellerName: text(1000) }).strict();
const draft = z.object({ id, revision: id, createdAt: timestamp, updatedAt: timestamp, state: z.literal('draft'), input, invoices: z.array(invoice).min(1).max(20) }).strict()
  .refine(value => value.createdAt <= value.updatedAt && new Set(value.invoices.map(item => item.invoiceId)).size === value.invoices.length &&
    value.input.invoiceIds.length === value.invoices.length && value.input.invoiceIds.every(id => value.invoices.some(item => item.invoiceId === id)));
const result = z.object({ declarationId: id, versionId: id, revision: id, recordedAt: timestamp }).strict();
const reservation = z.object({ reservationId: id, revision: z.literal('1'), expiresAt: timestamp }).strict();
const reference = z.object({ id, version: z.literal('1'), sha256: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/) }).strict();
const status = z.object({ reservationId: id, state: z.enum(['fresh', 'writing', 'publish_pending', 'interrupted', 'quarantined']), revision: id,
  expiresAt: timestamp, reference: reference.nullable(), scanStatus: z.enum(['unavailable', 'clean', 'infected']), decoderStatus: z.enum(['unavailable', 'decoded', 'invalid']), safeFailure: z.string().max(100).nullable() }).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null) &&
    (value.state === 'quarantined' || value.scanStatus === 'unavailable' && value.decoderStatus === 'unavailable') &&
    (value.decoderStatus !== 'decoded' || value.scanStatus === 'clean' && value.safeFailure === null) &&
    (value.decoderStatus !== 'invalid' || value.scanStatus === 'clean' && value.safeFailure === 'invalid_media') &&
    (value.scanStatus !== 'infected' || value.decoderStatus === 'unavailable' && ['infected_media', 'integrity_mismatch'].includes(value.safeFailure ?? '')));
const outcome = z.object({ state: z.enum(['quarantined', 'interrupted', 'unresolved', 'unknown', 'publish_pending', 'recovery_required']), reference: reference.nullable() }).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null));
export type PaymentInput = z.infer<typeof input>;
export type PaymentDraft = z.infer<typeof draft>;
export type PaymentDraftResult = z.infer<typeof result>;
export type PaymentReservation = z.infer<typeof reservation>;
export type PaymentEvidenceStatus = z.infer<typeof status>;
function invalid(): never { throw new AccessFailure(502, 'payment_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export const parsePaymentResult = (value: unknown) => parse(result, value);
export const parsePaymentReservation = (value: unknown) => parse(reservation, value);
export const parsePaymentEvidenceStatus = (value: unknown) => parse(status, value);
export function paymentEvidenceLabel(value: PaymentEvidenceStatus): string {
  if (value.state !== 'quarantined') return ({ fresh: 'Siap menerima berkas', writing: 'Unggah belum terkonfirmasi', publish_pending: 'Penyimpanan perlu dikonfirmasi', interrupted: 'Unggah terputus; berkas belum lengkap' })[value.state];
  if (value.safeFailure === 'integrity_mismatch') return 'Identitas berkas tidak cocok. Bukti ditahan; hubungi penanggung jawab.';
  if (value.scanStatus === 'infected') return 'Berkas ditolak karena terdeteksi berbahaya. Unggah bukti asli yang aman.';
  if (value.decoderStatus === 'invalid') return 'Gambar tidak dapat dibaca dengan aman. Unggah ulang gambar asli yang lengkap.';
  if (value.scanStatus === 'clean' && value.decoderStatus === 'decoded' && value.safeFailure === null) return 'Pemeriksaan keamanan selesai. Bukti tetap dalam draf; pembayaran belum diverifikasi.';
  if (value.safeFailure !== null) return 'Pemeriksaan keamanan belum selesai. Bukti tetap ditahan; coba muat status kembali atau hubungi penanggung jawab.';
  return 'Tersimpan · menunggu pemeriksaan keamanan';
}
export function paymentReservationOwner(key: string) { const match = /^p\.([1-9][0-9]{0,18})\.([0-9a-f-]{36})$(?![\s\S])/.exec(key); return match ? priceId(match[1]) : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) { const error = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {}; throw new AccessFailure(response.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return response.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
const check = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'payment_view_stale'); };
export function preparePayment(actorId: string, declarationId: string | null, revision: string, values: PaymentInput, reason: string) {
  const frozen = parse(input, values);
  if (declarationId === null) { if (revision !== '0') return invalid(); }
  else { priceId(declarationId); priceId(revision); }
  return Object.freeze({ actorId: priceId(actorId), revision, body: Object.freeze({ declarationId, input: Object.freeze({ ...frozen, invoiceIds: Object.freeze([...frozen.invoiceIds].sort((a, b) => BigInt(a) < BigInt(b) ? -1 : 1)) }), reason: parse(text(1000), reason) }) });
}
export function createPaymentGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const value = parse(z.object({ items: z.array(draft).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/payment-declarations/query', { body: { purpose: 'payment-declarations', beforeId }, params: { header: headers(actorId) }, signal })));
      if (new Set(value.items.map(item => item.id)).size !== value.items.length || value.hasMore && value.items.length !== 25 || beforeId !== null && value.items.some(item => BigInt(item.id) >= BigInt(priceId(beforeId)))) return invalid(); return value;
    },
    async read(actorId: string, declarationId: string) {
      const value = parse(draft, data(await client.POST('/api/v1/billing/payment-declarations/{id}/query', { body: { purpose: 'payment-declarations' }, params: { path: { id: priceId(declarationId) }, header: headers(actorId) } })));
      return value.id === declarationId ? value : invalid();
    },
    async save(command: ReturnType<typeof preparePayment>, requestKey: string, current: () => boolean) {
      check(current); const value = parsePaymentResult(data(await client.POST('/api/v1/billing/payment-declarations', { body: { ...command.body, input: { ...command.body.input, invoiceIds: [...command.body.input.invoiceIds] } },
        params: { header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"' + command.revision + '"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (command.body.declarationId !== null && command.body.declarationId !== value.declarationId || BigInt(value.revision) !== BigInt(command.revision) + BigInt(1)) return invalid(); return value;
    },
    async recover(actorId: string, requestKey: string) {
      const value = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-declarations/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) } })));
      if (value.status === 'not_confirmed' && value.result === null) return null;
      return value.status === 'committed' && value.result !== null ? { state: 'committed' as const, result: value.result } : invalid();
    },
    async reserve(actorId: string, declarationId: string, revision: string, requestKey: string, current: () => boolean) {
      check(current); if (paymentReservationOwner(requestKey) !== declarationId) return invalid();
      const value = parsePaymentReservation(data(await client.POST('/api/v1/billing/payment-declarations/{id}/evidence', { body: { reason: 'Unggah bukti pembayaran asli' }, params: { path: { id: priceId(declarationId) },
        header: { ...headers(actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"' + priceId(revision) + '"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return value;
    },
    async recoverReservation(actorId: string, requestKey: string) {
      const value = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), reservation: reservation.nullable() }).strict(), data(await client.POST('/api/v1/billing/payment-declarations/{id}/evidence/operations/query', {
        body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { path: { id: paymentReservationOwner(requestKey) }, header: headers(actorId) } })));
      if (value.status === 'not_confirmed' && value.reservation === null) return null;
      return value.status === 'committed' && value.reservation !== null ? { state: 'committed' as const, result: value.reservation } : invalid();
    },
    async evidence(actorId: string, declarationId: string, beforeId: string | null = null) {
      const value = parse(z.object({ items: z.array(status).max(25) }).strict(), data(await client.POST('/api/v1/billing/payment-declarations/{id}/evidence/query', { body: { purpose: 'payment-evidence', beforeId }, params: { path: { id: priceId(declarationId) }, header: headers(actorId) } })));
      if (new Set(value.items.map(item => item.reservationId)).size !== value.items.length || beforeId !== null && value.items.some(item => BigInt(item.reservationId) >= BigInt(priceId(beforeId)))) return invalid(); return value.items;
    },
    async upload(actorId: string, declarationId: string, reservationId: string, revision: string, file: File, current: () => boolean) {
      if (file.size <= 0 || file.size > 10 * 1024 * 1024 || !['image/jpeg', 'image/png'].includes(file.type)) throw new RequestNotSentError(400, 'payment_image_required');
      check(current); const value = parse(outcome, data(await client.POST('/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/content', { body: '', bodySerializer: () => file,
        headers: { 'Content-Type': 'application/octet-stream' }, params: { path: { id: priceId(declarationId), reservationId: priceId(reservationId) }, header: { ...headers(actorId), 'Idempotency-Key': 'upload.' + reservationId, 'If-Match': '"' + priceId(revision) + '"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return value;
    },
    async original(actorId: string, declarationId: string, revision: string, evidence: PaymentEvidenceStatus, current: () => boolean) {
      const expected = parsePaymentEvidenceStatus(evidence);
      if (!expected.reference || expected.scanStatus !== 'clean' || expected.decoderStatus !== 'decoded' || expected.safeFailure !== null) throw new RequestNotSentError(409, 'evidence_read_not_eligible');
      check(current);
      const response = await client.POST('/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/original/query', {
        body: { purpose: 'payment-evidence-view', reference: expected.reference }, params: { path: { id: priceId(declarationId), reservationId: expected.reservationId },
          header: { ...headers(actorId), 'If-Match': '"' + priceId(revision) + '"' } }, parseAs: 'arrayBuffer', cache: 'no-store' });
      check(current); const bytes = data(response), mime = response.response.headers.get('content-type');
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength <= 0 || bytes.byteLength > 10 * 1024 * 1024 ||
        response.response.headers.get('content-length') !== String(bytes.byteLength) || !['image/png', 'image/jpeg'].includes(mime ?? '')) return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes); check(current);
      if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== expected.reference.sha256) return invalid();
      return new Blob([bytes], { type: mime! });
    },
    async recoverUpload(actorId: string, declarationId: string, reservationId: string) {
      return parse(outcome, data(await client.POST('/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/recover', { body: { purpose: 'upload-recovery', reason: 'Periksa hasil unggah asli' },
        params: { path: { id: priceId(declarationId), reservationId: priceId(reservationId) }, header: { ...headers(actorId), 'Idempotency-Key': 'upload.' + reservationId } } })));
    },
  };
}
