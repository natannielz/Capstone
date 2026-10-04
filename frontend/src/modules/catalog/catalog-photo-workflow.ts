import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {OperationReference} from '../administration/operation-journal';

export const catalogPhotoOperation = 'catalog.photo.associate' as const;
export const catalogPhotoPurpose = 'catalog-photo' as const;
const maximumLong = BigInt('9223372036854775807');
const invalid = (): never => { throw new AccessFailure(502, 'catalog_photo_response_invalid'); };
const badInput = (): never => { throw new AccessFailure(400, 'invalid_catalog_photo_request'); };

function object(value: unknown, fields: readonly string[]): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const result = value as Record<string, unknown>;
  if (Object.keys(result).sort().join(',') !== [...fields].sort().join(',')) return invalid();
  return result;
}
function integer(value: unknown, zero = false): string {
  if (typeof value !== 'string' || value !== value.trim() || !(zero ? /^(?:0|[1-9][0-9]*)$/u : /^[1-9][0-9]*$/u).test(value) ||
      value.length > 19 || BigInt(value) > maximumLong) return invalid();
  return value;
}
function timestamp(value: unknown): string {
  if (typeof value !== 'string' || value !== value.trim() || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/u.test(value) ||
      !Number.isFinite(Date.parse(value)) || new Date(value).toISOString().slice(0, 19) !== value.slice(0, 19)) return invalid();
  return value;
}
function input<T>(parse: () => T): T {
  try { return parse(); } catch { return badInput(); }
}
export function parseCatalogPhotoReference(value: unknown) {
  const v = object(value, ['id', 'version', 'sha256']);
  if (typeof v.sha256 !== 'string' || v.sha256.length !== 64 || !/^[0-9a-f]{64}$/u.test(v.sha256)) return invalid();
  return {id: integer(v.id), version: integer(v.version), sha256: v.sha256};
}
export type CatalogPhotoReference = Readonly<ReturnType<typeof parseCatalogPhotoReference>>;
export type CatalogPhotoTarget = Readonly<{skuId: string; skuRevision: string; definitionId: string; definitionVersion: string}>;
const targetFields = ['skuId', 'skuRevision', 'definitionId', 'definitionVersion'] as const;
function target(value: unknown): CatalogPhotoTarget {
  const v = object(value, targetFields);
  return {skuId: integer(v.skuId), skuRevision: integer(v.skuRevision), definitionId: integer(v.definitionId), definitionVersion: integer(v.definitionVersion)};
}
function sameTarget(actual: CatalogPhotoTarget, expected: CatalogPhotoTarget) {
  if (targetFields.some(field => actual[field] !== expected[field])) invalid();
}
export function parseCatalogPhotoCurrent(value: unknown, expected: CatalogPhotoTarget) {
  const v = object(value, [...targetFields, 'photoRevision', 'photo']);
  const source = target(Object.fromEntries(targetFields.map(field => [field, v[field]])));
  sameTarget(source, target(expected));
  const photoRevision = integer(v.photoRevision, true);
  const photo = v.photo === null ? null : (() => {
    const p = object(v.photo, ['associationId', 'reservationId', 'reference', 'mime', 'recordedAt', 'matchesCurrentDefinition']);
    if ((p.mime !== 'image/png' && p.mime !== 'image/jpeg') || typeof p.matchesCurrentDefinition !== 'boolean') return invalid();
    return {associationId: integer(p.associationId), reservationId: integer(p.reservationId), reference: parseCatalogPhotoReference(p.reference),
      mime: p.mime, recordedAt: timestamp(p.recordedAt), matchesCurrentDefinition: p.matchesCurrentDefinition};
  })();
  if ((photoRevision === '0') !== (photo === null) || BigInt(photoRevision) > BigInt(256)) return invalid();
  return {...source, photoRevision, photo};
}
export type CatalogPhotoCurrent = ReturnType<typeof parseCatalogPhotoCurrent>;
export type CatalogPhotoAssociationBody = Readonly<{expectedPhotoRevision: string; reservationId: string; reference: CatalogPhotoReference; reason: string}>;
export type CatalogPhotoCommand = Readonly<{
  actorId: string; operation: typeof catalogPhotoOperation; target: CatalogPhotoTarget;
  previousAssociationId: string | null; body: CatalogPhotoAssociationBody;
}>;
function associationBody(value: unknown): CatalogPhotoAssociationBody {
  const b = object(value, ['expectedPhotoRevision', 'reservationId', 'reference', 'reason']);
  const expectedPhotoRevision = integer(b.expectedPhotoRevision, true), reason = b.reason;
  if (typeof reason !== 'string' || !reason || reason !== reason.trim() || reason.length > 1000 ||
      /[\u0000-\u001f\u007f-\u009f]/u.test(reason) || !reason.isWellFormed() || BigInt(expectedPhotoRevision) >= BigInt(256)) return badInput();
  return Object.freeze({expectedPhotoRevision, reservationId: integer(b.reservationId), reference: Object.freeze(parseCatalogPhotoReference(b.reference)), reason});
}

/** Freeze the current SKU/definition and selected private original. The server proves its upload ownership and validation. */
export function prepareCatalogPhotoAssociation(actorId: string, expected: CatalogPhotoTarget, current: CatalogPhotoCurrent,
  reservationId: string, reference: CatalogPhotoReference, reason: string): CatalogPhotoCommand {
  return input(() => {
    const source = target(expected), state = parseCatalogPhotoCurrent(current, source);
    return Object.freeze({actorId: integer(actorId), operation: catalogPhotoOperation, target: Object.freeze(source),
      previousAssociationId: state.photo?.associationId ?? null,
      body: associationBody({expectedPhotoRevision: state.photoRevision, reservationId, reference, reason})});
  });
}

/** Parse persisted results independently of today's selection; recovery may follow a later SKU revision. */
export function parseCatalogPhotoResult(value: unknown, skuId: string, command?: CatalogPhotoCommand) {
  const v = object(value, ['associationId', ...targetFields, 'photoRevision', 'recordedAt']);
  const result = {associationId: integer(v.associationId), skuId: integer(v.skuId), skuRevision: integer(v.skuRevision),
    definitionId: integer(v.definitionId), definitionVersion: integer(v.definitionVersion), photoRevision: integer(v.photoRevision), recordedAt: timestamp(v.recordedAt)};
  if (result.skuId !== integer(skuId) || BigInt(result.photoRevision) > BigInt(256)) return invalid();
  if (command) {
    sameTarget(result, command.target);
    if (BigInt(result.photoRevision) !== BigInt(command.body.expectedPhotoRevision) + BigInt(1) || result.associationId === command.previousAssociationId) return invalid();
  }
  return result;
}
export type CatalogPhotoResult = ReturnType<typeof parseCatalogPhotoResult>;

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;
export function catalogPhotoKey(skuId: string, uuid: string = crypto.randomUUID()): string {
  return input(() => { if (uuid.length !== 36 || !uuidPattern.test(uuid)) return badInput(); return `catalog.photo.${integer(skuId)}.${uuid}`; });
}
export function catalogPhotoKeySku(key: string): string {
  const match = /^catalog\.photo\.([1-9][0-9]*)\.([0-9a-f-]{36})$/u.exec(key);
  if (key !== key.trim() || !match || !uuidPattern.test(match[2])) return invalid();
  return integer(match[1]);
}
export function assertCatalogPhotoActor(actorId: string): void { input(() => integer(actorId)); }
export function freezeCatalogPhotoTarget(value: CatalogPhotoTarget): CatalogPhotoTarget { return Object.freeze(input(() => target(value))); }
export function freezeCatalogPhotoCommand(command: CatalogPhotoCommand): CatalogPhotoCommand {
  return input(() => {
    object(command, ['actorId', 'operation', 'target', 'previousAssociationId', 'body']);
    if (command.operation !== catalogPhotoOperation) return badInput();
    const body = associationBody(command.body), previous = command.previousAssociationId;
    if ((body.expectedPhotoRevision === '0') !== (previous === null)) return badInput();
    if (previous !== null) integer(previous);
    return Object.freeze({actorId: integer(command.actorId), operation: catalogPhotoOperation,
      target: Object.freeze(target(command.target)), previousAssociationId: previous, body});
  });
}

export type CatalogPhotoRecoveryReference = Readonly<OperationReference & {operation: typeof catalogPhotoOperation}>;
export function parseCatalogPhotoRecoveryReference(value: unknown, actorId: string): CatalogPhotoRecoveryReference {
  const v = object(value, ['actorId', 'operation', 'key', 'createdAt']);
  if (v.actorId !== integer(actorId) || v.operation !== catalogPhotoOperation || typeof v.key !== 'string') return invalid();
  catalogPhotoKeySku(v.key);
  return Object.freeze({actorId, operation: catalogPhotoOperation, key: v.key, createdAt: timestamp(v.createdAt)});
}
export function parseCatalogPhotoRecovery(value: unknown, entry: CatalogPhotoRecoveryReference, command?: CatalogPhotoCommand): CatalogPhotoResult | null {
  const original = parseCatalogPhotoRecoveryReference(entry, entry.actorId), v = object(value, ['result']);
  if (command && (command.actorId !== original.actorId || command.target.skuId !== catalogPhotoKeySku(original.key))) return invalid();
  return v.result === null ? null : parseCatalogPhotoResult(v.result, catalogPhotoKeySku(original.key), command);
}

/** Existing OperationJournal can implement this port once the operation is registered. Never give it command bodies. */
export type CatalogPhotoJournal = Pick<import('../administration/operation-journal').OperationJournal, 'list' | 'remember' | 'confirmed'>;
export type CatalogPhotoAssociationGateway = {
  associate(command: CatalogPhotoCommand, key: string, isCurrent: () => boolean, signal?: AbortSignal): Promise<CatalogPhotoResult>;
  recover(entry: CatalogPhotoRecoveryReference, isCurrent: () => boolean, signal?: AbortSignal): Promise<CatalogPhotoResult | null>;
};

/** A retained reference locks new photo writes. A missing lookup result never authorizes an automatic resend. */
export function createCatalogPhotoAssociationWorkflow(gateway: CatalogPhotoAssociationGateway, journal: CatalogPhotoJournal,
  newUuid: () => string = () => crypto.randomUUID()) {
  const pending = (actorId: string) => {
    assertCatalogPhotoActor(actorId);
    return journal.list(actorId).filter(entry => entry.operation === catalogPhotoOperation).map(entry => parseCatalogPhotoRecoveryReference(entry, actorId));
  };
  const clear = (entry: CatalogPhotoRecoveryReference) => { try { journal.confirmed(entry); } catch { /* Retain lookup-only recovery after cleanup failure. */ } };
  return {
    pending,
    async associate(value: CatalogPhotoCommand, isCurrent: () => boolean, signal?: AbortSignal) {
      const command = freezeCatalogPhotoCommand(value);
      if (!isCurrent() || signal?.aborted) throw new RequestNotSentError(409, 'catalog_photo_view_changed');
      if (pending(command.actorId).length) throw new AccessFailure(409, 'resolve_previous_attempt');
      const key = catalogPhotoKey(command.target.skuId, newUuid());
      const entry = parseCatalogPhotoRecoveryReference(journal.remember(command.actorId, catalogPhotoOperation, key), command.actorId);
      if (entry.key !== key) throw new AccessFailure(400, 'recovery_storage_unavailable');
      try {
        const result = await gateway.associate(command, key, isCurrent, signal);
        clear(entry);
        return result;
      } catch (error) {
        if (error instanceof RequestNotSentError || error instanceof AccessFailure && error.status >= 400 && error.status < 500) clear(entry);
        throw error;
      }
    },
    async recover(actorId: string, reference: OperationReference, isCurrent: () => boolean, signal?: AbortSignal) {
      if (actorId !== reference.actorId) throw new AccessFailure(409, 'actor_changed');
      const entry = parseCatalogPhotoRecoveryReference(reference, actorId);
      const result = await gateway.recover(entry, isCurrent, signal);
      if (result !== null) clear(entry);
      return result;
    },
  };
}
