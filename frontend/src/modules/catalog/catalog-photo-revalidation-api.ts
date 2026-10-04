import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {OperationReference} from '../administration/operation-journal';
import {assertCatalogPhotoActor, freezeCatalogPhotoTarget, parseCatalogPhotoCurrent, parseCatalogPhotoReference,
  type CatalogPhotoCurrent, type CatalogPhotoJournal, type CatalogPhotoReference, type CatalogPhotoTarget} from './catalog-photo-workflow';

export const catalogPhotoRevalidationOperation = 'evidence.image-revalidation.request' as const;
export const catalogPhotoRevalidationPurpose = 'catalog-image-revalidation' as const;
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/u).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.string().regex(/^(?:0|[1-9][0-9]{0,2})$(?![\s\S])/u).refine(value => BigInt(value) <= BigInt(256));
const timestamp = z.string().datetime({offset: true}).refine(value => /(?:Z|\+00:00)$/u.test(value) &&
  Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 19) === value.slice(0, 19));
const resultSchema = z.object({requestId: id, skuId: id, versionRowId: id,
  state: z.enum(['pending', 'passed', 'rejected']), recordedAt: timestamp}).strict();
const currentSchema = z.object({skuId: id, versionRowId: id, skuRevision: id, proofRevision: revision,
  state: z.enum(['first_validation', 'pending', 'passed', 'rejected']), checkedAt: timestamp.nullable(),
  mime: z.enum(['image/png', 'image/jpeg']).nullable(), pipelineId: z.string().min(1).max(128).regex(/^[^\u0000-\u001f\u007f-\u009f]+$/u).nullable()}).strict()
  .refine(value => (value.proofRevision === '0') === (value.state === 'first_validation') &&
    (value.state === 'first_validation' || value.state === 'pending'
      ? value.checkedAt === null && value.mime === null && value.pipelineId === null
      : value.checkedAt !== null && value.pipelineId !== null && (value.state === 'passed' ? value.mime !== null : value.mime === null)));
const recoverySchema = z.object({result: resultSchema.nullable()}).strict();
const entrySchema = z.object({actorId: id, operation: z.literal(catalogPhotoRevalidationOperation), key: z.string(), createdAt: timestamp}).strict();
const invalid = (): never => { throw new AccessFailure(502, 'catalog_photo_revalidation_invalid'); };
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const result = schema.safeParse(value); return result.success ? result.data : invalid(); }
export type CatalogPhotoRevalidationResult = z.infer<typeof resultSchema>;
export type CatalogPhotoRevalidationCurrent = z.infer<typeof currentSchema>;
export type CatalogPhotoRevalidationEntry = Readonly<OperationReference & {operation: typeof catalogPhotoRevalidationOperation}>;
export function parseCatalogPhotoRevalidationResult(value: unknown, skuId: string): CatalogPhotoRevalidationResult {
  const result = parse(resultSchema, value); return result.skuId === parse(id, skuId) ? result : invalid();
}
export function parseCatalogPhotoRevalidationCurrent(value: unknown, target: CatalogPhotoTarget): CatalogPhotoRevalidationCurrent {
  const result = parse(currentSchema, value), expected = freezeCatalogPhotoTarget(target);
  return result.skuId === expected.skuId && result.skuRevision === expected.skuRevision ? result : invalid();
}
export function catalogPhotoRevalidationKey(skuId: string, uuid = crypto.randomUUID()): string {
  const key = `catalog.photo.revalidate.${parse(id, skuId)}.${uuid}`; catalogPhotoRevalidationKeySku(key); return key;
}
export function catalogPhotoRevalidationKeySku(key: string): string {
  const match = /^catalog\.photo\.revalidate\.([1-9][0-9]{0,18})\.[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/u.exec(key);
  return match ? parse(id, match[1]) : invalid();
}
function entry(value: unknown, actorId: string): CatalogPhotoRevalidationEntry {
  const result = parse(entrySchema, value); catalogPhotoRevalidationKeySku(result.key);
  if (result.actorId !== actorId) throw new AccessFailure(409, 'actor_changed');
  return Object.freeze(result);
}
function original(metadata: CatalogPhotoCurrent, target: CatalogPhotoTarget) {
  const state = parseCatalogPhotoCurrent(metadata, target);
  if (!state.photo?.matchesCurrentDefinition) throw new RequestNotSentError(409, 'catalog_photo_changed');
  return state.photo;
}
export type CatalogPhotoRevalidationCommand = Readonly<{actorId: string; target: CatalogPhotoTarget; versionRowId: string;
  reservationId: string; body: Readonly<{purpose: typeof catalogPhotoRevalidationPurpose; expectedProofRevision: string; reference: CatalogPhotoReference; reason: string}>}>;
const reasonSchema = z.string().min(1).max(1000).refine(value => value === value.trim() && value.isWellFormed() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value));
const commandSchema = z.object({actorId: id, target: z.object({skuId: id, skuRevision: id, definitionId: id, definitionVersion: id}).strict(),
  versionRowId: id, reservationId: id, body: z.object({purpose: z.literal(catalogPhotoRevalidationPurpose), expectedProofRevision: revision.refine(value => BigInt(value) < BigInt(256)),
    reference: z.object({id, version: id, sha256: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/u)}).strict(), reason: reasonSchema}).strict()}).strict();
function freezeCommand(value: CatalogPhotoRevalidationCommand): CatalogPhotoRevalidationCommand {
  const parsed = commandSchema.safeParse(value);
  if (!parsed.success) throw new RequestNotSentError(400, 'invalid_catalog_photo_revalidation_request');
  const command = parsed.data;
  return Object.freeze({...command, target: Object.freeze(command.target), body: Object.freeze({...command.body, reference: Object.freeze(command.body.reference)})});
}
export function prepareCatalogPhotoRevalidation(actorId: string, target: CatalogPhotoTarget, metadata: CatalogPhotoCurrent,
  proof: CatalogPhotoRevalidationCurrent, reason: string): CatalogPhotoRevalidationCommand {
  assertCatalogPhotoActor(actorId);
  const source = freezeCatalogPhotoTarget(target), photo = original(metadata, source), current = parseCatalogPhotoRevalidationCurrent(proof, source);
  if (!reason || reason !== reason.trim() || reason.length > 1000 || !reason.isWellFormed() || /[\u0000-\u001f\u007f-\u009f]/u.test(reason))
    throw new RequestNotSentError(400, 'invalid_catalog_photo_revalidation_request');
  if (current.state === 'pending' || BigInt(current.proofRevision) >= BigInt(256)) throw new RequestNotSentError(409, 'image_revalidation_changed');
  return freezeCommand({actorId, target: source, versionRowId: current.versionRowId, reservationId: photo.reservationId,
    body: Object.freeze({purpose: catalogPhotoRevalidationPurpose, expectedProofRevision: current.proofRevision,
      reference: Object.freeze(parseCatalogPhotoReference(photo.reference)), reason})});
}
type Headers = Readonly<{'X-Expected-Actor-Id': string; 'If-Match'?: string; 'Idempotency-Key'?: string}>;
type Path = `/api/v1/catalog/skus/${string}/photo-evidence/${string}/revalidation${'' | '/query'}` | '/api/v1/catalog/photo-revalidations/operations/query';
export type CatalogPhotoRevalidationRequest = Readonly<{path: Path; headers: Headers; body: Readonly<Record<string, unknown>>;
  isCurrent: () => boolean; signal?: AbortSignal}>;
export type CatalogPhotoRevalidationTransport = {send(request: CatalogPhotoRevalidationRequest): Promise<Response>};

/** Explicit same-origin JSON requests; fresh CSRF and the actor are checked before every single send. */
export function createCatalogPhotoRevalidationTransport(options: {fetch?: typeof globalThis.fetch; origin?: string} = {}): CatalogPhotoRevalidationTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return {async send(request) {
    const origin = options.origin ?? window.location.origin, url = new URL(request.path, origin);
    if (url.origin !== new URL(origin).origin || typeof window !== 'undefined' && url.origin !== window.location.origin)
      throw new RequestNotSentError(400, 'invalid_origin');
    const before = () => { if (request.signal?.aborted || !request.isCurrent()) throw new RequestNotSentError(409, 'catalog_photo_view_changed'); };
    before();
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, request.signal);
    before();
    return fetcher(new Request(url, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal,
      headers: {...request.headers, 'X-CSRF-Token': token, 'Content-Type': 'application/json'}, body: JSON.stringify(request.body)}));
  }};
}
async function json(response: Response): Promise<unknown> {
  const value: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const problem = value !== null && typeof value === 'object' ? value as Record<string, unknown> : {};
    throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return value;
}
function current(isCurrent: () => boolean, signal?: AbortSignal) {
  if (signal?.aborted || !isCurrent()) throw new AccessFailure(409, 'catalog_photo_view_changed');
}
export function createCatalogPhotoRevalidationApiGateway(transport: CatalogPhotoRevalidationTransport = createCatalogPhotoRevalidationTransport()) {
  return {
    async current(actorId: string, target: CatalogPhotoTarget, metadata: CatalogPhotoCurrent, isCurrent: () => boolean, signal?: AbortSignal) {
      assertCatalogPhotoActor(actorId); const source = freezeCatalogPhotoTarget(target), photo = original(metadata, source);
      current(isCurrent, signal);
      const result = await json(await transport.send({path: `/api/v1/catalog/skus/${source.skuId}/photo-evidence/${photo.reservationId}/revalidation/query`,
        headers: {'X-Expected-Actor-Id': actorId}, body: {purpose: catalogPhotoRevalidationPurpose, reference: photo.reference}, isCurrent, signal}));
      current(isCurrent, signal); return parseCatalogPhotoRevalidationCurrent(result, source);
    },
    async request(value: CatalogPhotoRevalidationCommand, key: string, isCurrent: () => boolean, signal?: AbortSignal) {
      const command = freezeCommand(value);
      if (catalogPhotoRevalidationKeySku(key) !== command.target.skuId) throw new RequestNotSentError(400, 'invalid_catalog_photo_revalidation_request');
      if (signal?.aborted || !isCurrent()) throw new RequestNotSentError(409, 'catalog_photo_view_changed');
      const response = await transport.send({path: `/api/v1/catalog/skus/${command.target.skuId}/photo-evidence/${command.reservationId}/revalidation`,
        headers: {'X-Expected-Actor-Id': command.actorId, 'If-Match': `"${command.target.skuRevision}"`, 'Idempotency-Key': key}, body: command.body, isCurrent, signal});
      if (signal?.aborted || !isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const data = await json(response);
      if (signal?.aborted || !isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const result = parseCatalogPhotoRevalidationResult(data, command.target.skuId);
      if (result.versionRowId !== command.versionRowId) return invalid();
      return result;
    },
    async recover(reference: OperationReference, isCurrent: () => boolean, signal?: AbortSignal) {
      const saved = entry(reference, reference.actorId); current(isCurrent, signal);
      const result = parse(recoverySchema, await json(await transport.send({path: '/api/v1/catalog/photo-revalidations/operations/query',
        headers: {'X-Expected-Actor-Id': saved.actorId}, body: {purpose: catalogPhotoRevalidationPurpose, key: saved.key}, isCurrent, signal})));
      current(isCurrent, signal);
      return result.result === null ? null : parseCatalogPhotoRevalidationResult(result.result, catalogPhotoRevalidationKeySku(saved.key));
    },
  };
}
export type CatalogPhotoRevalidationGateway = ReturnType<typeof createCatalogPhotoRevalidationApiGateway>;
export function createCatalogPhotoRevalidationWorkflow(gateway: CatalogPhotoRevalidationGateway, journal: CatalogPhotoJournal, uuid: () => string = () => crypto.randomUUID()) {
  const pending = (actorId: string) => journal.list(actorId).filter(value => value.operation === catalogPhotoRevalidationOperation).map(value => entry(value, actorId));
  const clear = (value: OperationReference) => { try { journal.confirmed(value); } catch { /* Keep lookup-only recovery if local cleanup fails. */ } };
  return {
    pending,
    async request(value: CatalogPhotoRevalidationCommand, isCurrent: () => boolean, signal?: AbortSignal) {
      const command = freezeCommand(value);
      if (signal?.aborted || !isCurrent()) throw new RequestNotSentError(409, 'catalog_photo_view_changed');
      if (pending(command.actorId).length) throw new AccessFailure(409, 'resolve_previous_attempt');
      const key = catalogPhotoRevalidationKey(command.target.skuId, uuid()), saved = entry(journal.remember(command.actorId, catalogPhotoRevalidationOperation, key), command.actorId);
      if (saved.key !== key) throw new AccessFailure(400, 'recovery_storage_unavailable');
      try { const result = await gateway.request(command, key, isCurrent, signal); clear(saved); return result; }
      catch (error) { if (error instanceof RequestNotSentError || error instanceof AccessFailure && error.status >= 400 && error.status < 500) clear(saved); throw error; }
    },
    async recover(actorId: string, value: OperationReference, isCurrent: () => boolean, signal?: AbortSignal) {
      const saved = entry(value, actorId), result = await gateway.recover(saved, isCurrent, signal);
      if (result !== null) clear(saved);
      return result;
    },
  };
}
export function catalogPhotoRevalidationLabel(value: CatalogPhotoRevalidationCurrent): string {
  return {first_validation: 'Validasi awal tercatat; belum ada pemeriksaan ulang', pending: 'Pemeriksaan ulang sedang menunggu hasil',
    passed: 'Pemeriksaan ulang terakhir lulus', rejected: 'Pemeriksaan ulang terakhir ditolak'}[value.state];
}
