import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';

const invalid = (): never => { throw new AccessFailure(502, 'shipment_document_response_invalid'); };
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
export function parseShipmentDocument(value: unknown, maximumBytes = 524288) {
  const row = record(value), byteLength = priceId(row.byteLength);
  if (row.version !== '1' || row.shipmentRevision !== '1' || typeof row.contentHash !== 'string' || !/^[0-9a-f]{64}$(?![\s\S])/.test(row.contentHash) ||
    BigInt(byteLength) > BigInt(maximumBytes) || typeof row.issuedAt !== 'string' || row.issuedAt.length > 64 || !Number.isFinite(Date.parse(row.issuedAt))) return invalid();
  return { id: priceId(row.id), shipmentId: priceId(row.shipmentId), shipmentRevision: '1' as const, version: '1' as const, contentHash: row.contentHash, byteLength, issuedAt: row.issuedAt };
}
export type ShipmentDocument = ReturnType<typeof parseShipmentDocument>;
export function prepareShipmentDocument(actorId: string, shipmentId: string, reason: string) {
  if (!reason || reason !== reason.trim() || reason.length > 1000 || /[\u0000-\u001f\u007f]/.test(reason)) throw new AccessFailure(400, 'invalid_shipment_document');
  return Object.freeze({ actorId: priceId(actorId), shipmentId: priceId(shipmentId), reason });
}
type Command = ReturnType<typeof prepareShipmentDocument>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
  } return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
export function createShipmentDocumentGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, shipmentId: string, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/fulfillment/shipment-documents/{shipmentId}/query', { body: { purpose: 'shipment-document' },
        params: { path: { shipmentId: priceId(shipmentId) }, header: headers(actorId) }, signal })));
      if (row.status === 'not_issued' && row.document === null) return null;
      if (row.status !== 'issued') return invalid(); const document = parseShipmentDocument(row.document);
      if (document.shipmentId !== shipmentId) return invalid(); return document;
    },
    async issue(command: Command, requestKey: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'shipment_view_stale');
      const document = parseShipmentDocument(data(await client.POST('/api/v1/fulfillment/shipment-documents/{shipmentId}', { body: { reason: command.reason },
        params: { path: { shipmentId: command.shipmentId }, header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"1"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (document.shipmentId !== command.shipmentId) return invalid(); return document;
    },
    async recover(actorId: string, requestKey: string) {
      const row = record(data(await client.POST('/api/v1/fulfillment/shipment-documents/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) } })));
      if (row.status === 'not_confirmed' && row.document === null) return null;
      if (row.status !== 'committed') return invalid(); return { state: 'committed' as const, result: parseShipmentDocument(row.document) };
    },
    async download(actorId: string, document: ShipmentDocument) {
      const response = await client.POST('/api/v1/fulfillment/shipment-documents/{shipmentId}/{documentId}/download', { body: { purpose: 'shipment-document' },
        params: { path: { shipmentId: document.shipmentId, documentId: document.id }, header: headers(actorId) }, parseAs: 'arrayBuffer' });
      const bytes = data(response);
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength !== Number(document.byteLength) || response.response.headers.get('content-type') !== 'text/html; charset=utf-8') return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes);
      if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== document.contentHash) return invalid();
      return new Blob([bytes], { type: 'text/html;charset=utf-8' });
    },
  };
}
