import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { parseShipmentDocument } from './shipment-document-client';

const invalid = (): never => { throw new AccessFailure(502, 'receipt_proof_response_invalid'); };
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
export function parseReceiptProof(value: unknown) {
  const row = record(value);
  if (Object.keys(row).sort().join(',') !== 'byteLength,contentHash,id,issuedAsRecipient,issuedAt,receiptId,shipmentId,shipmentRevision,version' || typeof row.issuedAsRecipient !== 'boolean') return invalid();
  try { return { ...parseShipmentDocument(row, 2097152), receiptId: priceId(row.receiptId), issuedAsRecipient: row.issuedAsRecipient }; } catch { return invalid(); }
}
export type ReceiptProof = ReturnType<typeof parseReceiptProof>;
export function prepareReceiptProof(actorId: string, shipmentId: string, receiptId: string, asRecipient: boolean, reason: string) {
  if (typeof asRecipient !== 'boolean' || !reason || reason !== reason.trim() || reason.length > 1000 || /[\u0000-\u001f\u007f]/.test(reason)) throw new AccessFailure(400, 'invalid_receipt_proof');
  return Object.freeze({ actorId: priceId(actorId), shipmentId: priceId(shipmentId), receiptId: priceId(receiptId), asRecipient, reason });
}
type Command = ReturnType<typeof prepareReceiptProof>;
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) { const error = record(result.error ?? {}); throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
export function createReceiptProofGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, shipmentId: string, asRecipient: boolean, signal?: AbortSignal) {
      const row = record(data(await client.POST('/api/v1/fulfillment/receipt-proofs/{shipmentId}/query', { body: { purpose: 'receipt-proof', asRecipient }, params: { path: { shipmentId: priceId(shipmentId) }, header: headers(actorId) }, signal })));
      if (row.status === 'not_issued' && row.document === null) return null;
      if (row.status !== 'issued') return invalid(); const proof = parseReceiptProof(row.document); if (proof.shipmentId !== shipmentId) return invalid(); return proof;
    },
    async issue(command: Command, requestKey: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'shipment_view_stale');
      const proof = parseReceiptProof(data(await client.POST('/api/v1/fulfillment/receipt-proofs/{shipmentId}', { body: { reason: command.reason, receiptId: command.receiptId, asRecipient: command.asRecipient },
        params: { path: { shipmentId: command.shipmentId }, header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey) } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (proof.shipmentId !== command.shipmentId || proof.receiptId !== command.receiptId || proof.issuedAsRecipient !== command.asRecipient) return invalid(); return proof;
    },
    async recover(actorId: string, requestKey: string) {
      const row = record(data(await client.POST('/api/v1/fulfillment/receipt-proofs/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) } })));
      if (row.status === 'not_confirmed' && row.document === null) return null;
      if (row.status !== 'committed') return invalid(); return { state: 'committed' as const, result: parseReceiptProof(row.document) };
    },
    async download(actorId: string, document: ReceiptProof, asRecipient: boolean) {
      const response = await client.POST('/api/v1/fulfillment/receipt-proofs/{shipmentId}/{documentId}/download', { body: { purpose: 'receipt-proof', asRecipient },
        params: { path: { shipmentId: document.shipmentId, documentId: document.id }, header: headers(actorId) }, parseAs: 'arrayBuffer' });
      const bytes = data(response);
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength !== Number(document.byteLength) || response.response.headers.get('content-type') !== 'text/html; charset=utf-8') return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes);
      if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== document.contentHash) return invalid();
      return new Blob([bytes], { type: 'text/html;charset=utf-8' });
    },
  };
}
