import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {assertCatalogPhotoActor, freezeCatalogPhotoTarget, parseCatalogPhotoCurrent, parseCatalogPhotoReference,
  type CatalogPhotoCurrent, type CatalogPhotoReference, type CatalogPhotoTarget} from './catalog-photo-workflow';

export const catalogPhotoReserveOperation = 'evidence.catalog-image.reserve' as const;
export const catalogPhotoMaximumBytes = 10 * 1024 * 1024;
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/u).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const reference = z.object({id, version: id, sha256: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/u)}).strict();
const reservationSchema = z.object({reservationId: id, revision: z.literal('1'), expiresAt: z.string().datetime({offset: true})}).strict();
const statusSchema = z.object({reservationId: id, revision: id, expiresAt: z.string().datetime({offset: true}),
  state: z.enum(['fresh', 'writing', 'publish_pending', 'interrupted', 'quarantined']), reference: reference.nullable(),
  scanStatus: z.enum(['unavailable', 'clean', 'infected']), decoderStatus: z.enum(['unavailable', 'decoded', 'invalid']), safeFailure: z.string().max(100).nullable()}).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null) &&
    (value.state === 'quarantined' || value.scanStatus === 'unavailable' && value.decoderStatus === 'unavailable') &&
    (value.decoderStatus !== 'decoded' || value.scanStatus === 'clean' && value.safeFailure === null) &&
    (value.decoderStatus !== 'invalid' || value.scanStatus === 'clean' && value.safeFailure === 'invalid_media') &&
    (value.scanStatus !== 'infected' || value.decoderStatus === 'unavailable' && ['infected_media', 'integrity_mismatch'].includes(value.safeFailure ?? '')));
const outcomeSchema = z.object({state: z.enum(['quarantined', 'interrupted', 'unresolved', 'unknown', 'publish_pending', 'recovery_required']), reference: reference.nullable()}).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null));
const recoverySchema = z.object({status: z.enum(['committed', 'not_confirmed']), reservation: reservationSchema.nullable()}).strict()
  .refine(value => (value.status === 'committed') === (value.reservation !== null));
const invalid = (): never => { throw new AccessFailure(502, 'catalog_photo_evidence_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid(); }
export type CatalogPhotoReservation = z.infer<typeof reservationSchema>;
export type CatalogPhotoEvidenceStatus = z.infer<typeof statusSchema>;
export function parseCatalogPhotoReservation(value: unknown): CatalogPhotoReservation { return parse(reservationSchema, value); }
export function parseCatalogPhotoEvidenceStatus(value: unknown, reservationId?: string): CatalogPhotoEvidenceStatus {
  const result = parse(statusSchema, value); return reservationId === undefined || result.reservationId === reservationId ? result : invalid();
}
export function catalogPhotoEvidenceReady(value: CatalogPhotoEvidenceStatus): boolean {
  const status = parseCatalogPhotoEvidenceStatus(value);
  return status.state === 'quarantined' && status.reference !== null && status.scanStatus === 'clean' && status.decoderStatus === 'decoded' && status.safeFailure === null;
}
export function catalogPhotoEvidenceLabel(value: CatalogPhotoEvidenceStatus): string {
  if (catalogPhotoEvidenceReady(value)) return 'Foto lolos pemeriksaan keamanan dan format';
  if (value.scanStatus === 'infected') return 'Foto ditolak pemeriksaan keamanan';
  if (value.decoderStatus === 'invalid') return 'Format foto tidak dapat digunakan';
  if (value.safeFailure) return 'Pemeriksaan foto belum berhasil';
  if (value.state === 'quarantined') return 'Foto tersimpan; pemeriksaan keamanan dan format belum selesai';
  if (value.state === 'fresh') return 'Tempat unggah tersedia; foto belum tersimpan';
  return 'Hasil unggah perlu diperiksa kembali';
}
export function validateCatalogPhotoFile(file: File): void {
  if (!(file instanceof File) || file.size <= 0 || file.size > catalogPhotoMaximumBytes || !['image/png', 'image/jpeg'].includes(file.type))
    throw new RequestNotSentError(400, 'catalog_photo_image_required');
}
export function catalogPhotoReservationKey(skuId: string, uuid = crypto.randomUUID()): string {
  parse(id, skuId);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/u.test(uuid)) return invalid();
  return `catalog.photo.upload.${skuId}.${uuid}`;
}
export function catalogPhotoReservationOwner(key: string): string {
  const match = /^catalog\.photo\.upload\.([1-9][0-9]{0,18})\.[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/u.exec(key);
  return match ? parse(id, match[1]) : invalid();
}
type EvidencePath = `/api/v1/catalog/skus/${string}/photo-evidence${string}` | `/api/v1/catalog/skus/${string}/primary-photo/original/query`;
export type CatalogPhotoEvidenceRequest = Readonly<{path: EvidencePath; headers: Readonly<Record<string, string>>;
  body: Readonly<Record<string, unknown>> | File; isCurrent: () => boolean; signal?: AbortSignal}>;
export type CatalogPhotoEvidenceTransport = {send(request: CatalogPhotoEvidenceRequest): Promise<Response>};

/** Narrow same-origin transport until the generated endpoints are available; fresh CSRF precedes every request. */
export function createCatalogPhotoEvidenceTransport(options: {fetch?: typeof globalThis.fetch; origin?: string} = {}): CatalogPhotoEvidenceTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return {async send(request) {
    const origin = options.origin ?? window.location.origin, url = new URL(request.path, origin);
    if (url.origin !== new URL(origin).origin || typeof window !== 'undefined' && url.origin !== window.location.origin)
      throw new RequestNotSentError(400, 'invalid_origin');
    const before = () => { if (request.signal?.aborted || !request.isCurrent()) throw new RequestNotSentError(409, 'catalog_photo_view_changed'); };
    before();
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, request.signal);
    before();
    const raw = request.body instanceof File;
    return fetcher(new Request(url, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal,
      headers: {...request.headers, 'X-CSRF-Token': token, 'Content-Type': raw ? 'application/octet-stream' : 'application/json'},
      body: raw ? request.body : JSON.stringify(request.body)}));
  }};
}
async function json(response: Response): Promise<unknown> {
  const value: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const p = value !== null && typeof value === 'object' ? value as Record<string, unknown> : {};
    throw new AccessFailure(response.status, typeof p.code === 'string' ? p.code : 'request_failed');
  }
  return value;
}
const current = (valid: () => boolean, signal?: AbortSignal) => { if (signal?.aborted || !valid()) throw new AccessFailure(409, 'catalog_photo_view_changed'); };
const afterWrite = (valid: () => boolean, signal?: AbortSignal) => { if (signal?.aborted || !valid()) throw new AccessFailure(503, 'commit_outcome_unknown'); };
function reason(value: string): string {
  if (!value || value.length > 1000 || value !== value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value) || !value.isWellFormed())
    throw new RequestNotSentError(400, 'invalid_catalog_photo_request');
  return value;
}
async function originalBytes(response: Response, expected: CatalogPhotoReference, valid: () => boolean, signal?: AbortSignal, expectedMime?: string): Promise<Blob> {
  if (!response.ok) { await json(response); return invalid(); }
  const mime = response.headers.get('content-type'), length = response.headers.get('content-length');
  if (!['image/png', 'image/jpeg'].includes(mime ?? '') || expectedMime !== undefined && mime !== expectedMime ||
      length === null || !/^[1-9][0-9]{0,7}$(?![\s\S])/u.test(length) || Number(length) > catalogPhotoMaximumBytes || !response.body) {
    await response.body?.cancel(); return invalid();
  }
  const reader = response.body.getReader(), parts: Uint8Array<ArrayBuffer>[] = []; let size = 0;
  try {
    while (true) {
      current(valid, signal); const next = await reader.read(); current(valid, signal);
      if (next.done) break;
      size += next.value.byteLength;
      if (size > Number(length) || size > catalogPhotoMaximumBytes) return invalid();
      parts.push(new Uint8Array(next.value));
    }
  } finally { await reader.cancel().catch(() => undefined); reader.releaseLock(); }
  if (size !== Number(length)) return invalid();
  const bytes = new Uint8Array(size); let offset = 0;
  for (const part of parts) { bytes.set(part, offset); offset += part.length; }
  const hash = await crypto.subtle.digest('SHA-256', bytes); current(valid, signal);
  if (Array.from(new Uint8Array(hash), byte => byte.toString(16).padStart(2, '0')).join('') !== expected.sha256) return invalid();
  return new Blob([bytes], {type: mime!});
}

export function createCatalogPhotoEvidenceGateway(transport: CatalogPhotoEvidenceTransport = createCatalogPhotoEvidenceTransport()) {
  const request = (actorId: string, skuId: string, suffix: string, body: CatalogPhotoEvidenceRequest['body'], valid: () => boolean, signal?: AbortSignal, extra: Record<string, string> = {}) => {
    assertCatalogPhotoActor(actorId); parse(id, skuId); current(valid, signal);
    return transport.send({path: `/api/v1/catalog/skus/${skuId}/photo-evidence${suffix}`, headers: {'X-Expected-Actor-Id': actorId, ...extra}, body, isCurrent: valid, signal});
  };
  return {
    async reserve(actorId: string, source: CatalogPhotoTarget, key: string, explanation: string, valid: () => boolean, signal?: AbortSignal) {
      const target = freezeCatalogPhotoTarget(source);
      if (catalogPhotoReservationOwner(key) !== target.skuId) throw new RequestNotSentError(400, 'invalid_catalog_photo_request');
      const value = await json(await request(actorId, target.skuId, '', {reason: reason(explanation)}, valid, signal, {'If-Match': `"${target.skuRevision}"`, 'Idempotency-Key': key}));
      afterWrite(valid, signal); return parseCatalogPhotoReservation(value);
    },
    async recoverReservation(actorId: string, key: string, valid: () => boolean, signal?: AbortSignal) {
      const value = await json(await request(actorId, catalogPhotoReservationOwner(key), '/operations/query', {purpose: 'catalog-photo', key}, valid, signal));
      current(valid, signal); return parse(recoverySchema, value).reservation;
    },
    async status(actorId: string, skuId: string, reservationId: string, valid: () => boolean, signal?: AbortSignal) {
      const value = await json(await request(actorId, skuId, `/${parse(id, reservationId)}/query`, {purpose: 'catalog-photo'}, valid, signal));
      current(valid, signal); return parseCatalogPhotoEvidenceStatus(value, reservationId);
    },
    async upload(actorId: string, skuId: string, reservation: CatalogPhotoReservation, file: File, valid: () => boolean, signal?: AbortSignal) {
      validateCatalogPhotoFile(file); const saved = parseCatalogPhotoReservation(reservation);
      const value = await json(await request(actorId, skuId, `/${saved.reservationId}/content`, file, valid, signal,
        {'If-Match': `"${saved.revision}"`, 'Idempotency-Key': `upload.${saved.reservationId}`}));
      afterWrite(valid, signal); return parse(outcomeSchema, value);
    },
    async recoverUpload(actorId: string, skuId: string, reservationId: string, valid: () => boolean, signal?: AbortSignal) {
      const savedId = parse(id, reservationId);
      const value = await json(await request(actorId, skuId, `/${savedId}/recover`, {purpose: 'catalog-photo', reason: 'Periksa hasil unggah foto katalog'}, valid, signal, {'Idempotency-Key': `upload.${savedId}`}));
      afterWrite(valid, signal); return parse(outcomeSchema, value);
    },
    async original(actorId: string, source: CatalogPhotoTarget, status: CatalogPhotoEvidenceStatus, valid: () => boolean, signal?: AbortSignal) {
      const target = freezeCatalogPhotoTarget(source), image = parseCatalogPhotoEvidenceStatus(status);
      if (!catalogPhotoEvidenceReady(image) || !image.reference) throw new RequestNotSentError(409, 'evidence_read_not_eligible');
      const response = await request(actorId, target.skuId, `/${image.reservationId}/original/query`, {purpose: 'catalog-photo-association', reference: image.reference}, valid, signal, {'If-Match': `"${target.skuRevision}"`});
      return originalBytes(response, image.reference, valid, signal);
    },
    async currentOriginal(actorId: string, selected: CatalogPhotoCurrent, valid: () => boolean, signal?: AbortSignal) {
      assertCatalogPhotoActor(actorId); const target = freezeCatalogPhotoTarget({skuId: selected.skuId, skuRevision: selected.skuRevision, definitionId: selected.definitionId, definitionVersion: selected.definitionVersion});
      const metadata = parseCatalogPhotoCurrent(selected, target);
      if (!metadata.photo) throw new RequestNotSentError(409, 'evidence_read_not_eligible');
      const expected = parseCatalogPhotoReference(metadata.photo.reference); current(valid, signal);
      const response = await transport.send({path: `/api/v1/catalog/skus/${target.skuId}/primary-photo/original/query`,
        headers: {'X-Expected-Actor-Id': actorId}, body: {purpose: 'catalog-photo'}, isCurrent: valid, signal});
      current(valid, signal);
      if (response.status === 204) return null;
      return originalBytes(response, expected, valid, signal, metadata.photo.mime);
    },
  };
}
export type CatalogPhotoEvidenceGateway = ReturnType<typeof createCatalogPhotoEvidenceGateway>;
