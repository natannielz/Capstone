import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {assertCatalogPhotoActor, catalogPhotoKeySku, catalogPhotoPurpose, freezeCatalogPhotoCommand, freezeCatalogPhotoTarget,
  parseCatalogPhotoCurrent, parseCatalogPhotoRecovery, parseCatalogPhotoRecoveryReference, parseCatalogPhotoResult,
  type CatalogPhotoAssociationBody, type CatalogPhotoCommand, type CatalogPhotoRecoveryReference, type CatalogPhotoTarget} from './catalog-photo-workflow';

type ActorHeader = Readonly<{'X-Expected-Actor-Id': string}>;
type RequestOptions = Readonly<{method: 'POST'; credentials: 'same-origin'; cache: 'no-store'; redirect: 'error'; signal?: AbortSignal}>;
export type CatalogPhotoRequest = RequestOptions & (
  {path: '/api/v1/catalog/skus/{id}/primary-photo/query'; params: {path: {id: string}; header: ActorHeader}; body: {purpose: typeof catalogPhotoPurpose}} |
  {path: '/api/v1/catalog/skus/{id}/primary-photo'; params: {path: {id: string}; header: ActorHeader & {'If-Match': string; 'Idempotency-Key': string}}; body: CatalogPhotoAssociationBody} |
  {path: '/api/v1/catalog/primary-photos/operations/query'; params: {header: ActorHeader}; body: {purpose: typeof catalogPhotoPurpose; key: string}}
);
export type CatalogPhotoTransport = {
  /** The eventual generated same-origin adapter supplies fresh CSRF protection and never retries writes. */
  post(request: CatalogPhotoRequest): Promise<{response: Response; data?: unknown; error?: unknown}>;
};
const options = {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error'} as const;
function data(value: {response: Response; data?: unknown; error?: unknown}): unknown {
  if (!value.response.ok) {
    const error = value.error !== null && typeof value.error === 'object' && !Array.isArray(value.error) ? value.error as Record<string, unknown> : {};
    throw new AccessFailure(value.response.status, typeof error.code === 'string' ? error.code : 'request_failed', typeof error.traceId === 'string' ? error.traceId : undefined);
  }
  if (value.data === undefined) throw new AccessFailure(502, 'catalog_photo_response_invalid');
  return value.data;
}
function available(isCurrent: () => boolean, signal?: AbortSignal): boolean { return !signal?.aborted && isCurrent(); }
function readCurrent(isCurrent: () => boolean, signal?: AbortSignal): void {
  if (!available(isCurrent, signal)) throw new AccessFailure(409, 'catalog_photo_view_changed');
}

/** Only the association owner is implemented here. Upload admission/validation remains a separate private-evidence workflow. */
export function createCatalogPhotoGateway(transport: CatalogPhotoTransport) {
  return {
    async current(actorId: string, target: CatalogPhotoTarget, isCurrent: () => boolean, signal?: AbortSignal) {
      assertCatalogPhotoActor(actorId);
      const expected = freezeCatalogPhotoTarget(target);
      readCurrent(isCurrent, signal);
      const result = await transport.post({...options, path: '/api/v1/catalog/skus/{id}/primary-photo/query',
        params: {path: {id: expected.skuId}, header: {'X-Expected-Actor-Id': actorId}}, body: {purpose: catalogPhotoPurpose}, signal});
      readCurrent(isCurrent, signal);
      return parseCatalogPhotoCurrent(data(result), expected);
    },
    async associate(value: CatalogPhotoCommand, key: string, isCurrent: () => boolean, signal?: AbortSignal) {
      const command = freezeCatalogPhotoCommand(value);
      if (catalogPhotoKeySku(key) !== command.target.skuId) throw new RequestNotSentError(400, 'invalid_catalog_photo_request');
      if (!available(isCurrent, signal)) throw new RequestNotSentError(409, 'catalog_photo_view_changed');
      const result = await transport.post({...options, path: '/api/v1/catalog/skus/{id}/primary-photo',
        params: {path: {id: command.target.skuId}, header: {'X-Expected-Actor-Id': command.actorId, 'If-Match': `"${command.target.skuRevision}"`, 'Idempotency-Key': key}},
        body: command.body, signal});
      if (!available(isCurrent, signal)) throw new AccessFailure(503, 'commit_outcome_unknown');
      return parseCatalogPhotoResult(data(result), command.target.skuId, command);
    },
    async recover(reference: CatalogPhotoRecoveryReference, isCurrent: () => boolean, signal?: AbortSignal) {
      const entry = parseCatalogPhotoRecoveryReference(reference, reference.actorId);
      readCurrent(isCurrent, signal);
      const result = await transport.post({...options, path: '/api/v1/catalog/primary-photos/operations/query',
        params: {header: {'X-Expected-Actor-Id': entry.actorId}}, body: {purpose: catalogPhotoPurpose, key: entry.key}, signal});
      readCurrent(isCurrent, signal);
      return parseCatalogPhotoRecovery(data(result), entry);
    },
  };
}
export type CatalogPhotoGateway = ReturnType<typeof createCatalogPhotoGateway>;
