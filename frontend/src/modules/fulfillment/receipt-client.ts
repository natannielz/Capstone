import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { validReference } from '../organizations/access-scope';

const id = z.string().refine(validReference), text = z.string().min(1).max(1000), name = text.max(256);
const date = z.string().max(64).refine(value => Number.isFinite(Date.parse(value)));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const amount = z.object({ packages: z.number().int().min(0).max(1000000), looseUnits: z.number().int().min(0).max(999999) }).strict();
const units = z.string().regex(/^(0|[1-9][0-9]{0,12})$(?![\s\S])/).refine(value => BigInt(value) <= BigInt(1000000000000));
const lineRevision = z.string().regex(/^(?:0|[2-9]|[1-9][0-9]|10[0-3])$(?![\s\S])/).default('0');
const declaration = z.object({ skuId: id, accepted: amount, rejected: amount, missing: amount, exceptionReason: text.nullable(), lineRevision }).strict();
const receiptSchema = z.object({ id, shipmentId: id, documentId: id, recipientId: id, recipientName: name, recordedAt: date, hasException: z.boolean(),
  lines: z.array(declaration.extend({ definitionId: id, dispatchedBaseUnits: units, acceptedBaseUnits: units, rejectedBaseUnits: units, missingBaseUnits: units })).min(1).max(100) }).strict()
  .refine(row => new Set(row.lines.map(line => line.skuId)).size === row.lines.length && row.hasException === row.lines.some(line => BigInt(line.rejectedBaseUnits) + BigInt(line.missingBaseUnits) > BigInt(0)) &&
    row.lines.every(line => BigInt(line.dispatchedBaseUnits) > BigInt(0) && BigInt(line.acceptedBaseUnits) + BigInt(line.rejectedBaseUnits) + BigInt(line.missingBaseUnits) === BigInt(line.dispatchedBaseUnits)));
const challengeSchema = z.object({ id, shipmentId: id, documentId: id, recipientId: id, recipientName: name, expiresAt: date, serverTime: date, token: hash.nullable() }).strict();
const reviewSchema = z.object({ challengeId: id, shipmentId: id, documentId: id, documentVersion: z.literal('1'), documentHash: hash, recipientName: name, expiresAt: date, serverTime: date,
  consentVersion: z.literal('delivery-receipt-v1'), consentText: text, lines: z.array(z.object({ skuId: id, lineRevision, skuCode: text, productName: text, definitionId: id, packageLabel: text, baseUnitCode: text,
    baseUnitsPerPackage: z.number().int().min(1).max(1000000), packages: z.number().int().min(1).max(1000000) }).strict()).min(1).max(100) }).strict()
  .refine(row => new Set(row.lines.map(line => line.skuId)).size === row.lines.length);
function parse<T>(schema: z.ZodType<T, z.ZodTypeDef, unknown>, value: unknown): T {
  try { const result = schema.safeParse(value); if (result.success) return result.data; } catch { /* A malformed numeric field is an invalid response as well. */ }
  throw new AccessFailure(502, 'shipment_receipt_response_invalid');
}
export const parseReceipt = (value: unknown) => parse(receiptSchema, value);
export const parseReceiptChallenge = (value: unknown) => parse(challengeSchema, value);
export type Receipt = z.infer<typeof receiptSchema>;
export type ReceiptChallenge = z.infer<typeof challengeSchema>;
export type ReceiptReview = z.infer<typeof reviewSchema>;
export type ReceiptDeclaration = z.input<typeof declaration>;
export function parseReceiptLink(fragment: string) {
  const match = /^#([1-9][0-9]{0,18})\.([0-9a-f]{64})$(?![\s\S])/.exec(fragment);
  if (!match || !validReference(match[1])) throw new AccessFailure(400, 'invalid_receipt_link');
  return Object.freeze({ challengeId: match[1], token: match[2] });
}
export function prepareReceipt(actorId: string, review: ReceiptReview, token: string, lines: ReceiptDeclaration[], consent: boolean, reason: string) {
  if (!consent || !validReference(actorId) || !/^[0-9a-f]{64}$(?![\s\S])/.test(token) || !reason.trim() || reason !== reason.trim() || reason.length > 1000 || /[\u0000-\u001f\u007f]/.test(reason)) throw new AccessFailure(400, 'invalid_delivery_receipt');
  const parsed = z.array(declaration).safeParse(lines); if (!parsed.success || lines.length !== review.lines.length || new Set(lines.map(line => line.skuId)).size !== lines.length) throw new AccessFailure(400, 'invalid_delivery_receipt');
  for (const source of review.lines) {
    const line = parsed.data.find(line => line.skuId === source.skuId && line.lineRevision === source.lineRevision); if (!line) throw new AccessFailure(400, 'invalid_delivery_receipt');
    const total = (value: z.infer<typeof amount>) => BigInt(value.packages) * BigInt(source.baseUnitsPerPackage) + BigInt(value.looseUnits);
    if ([line.accepted, line.rejected, line.missing].some(value => value.looseUnits >= source.baseUnitsPerPackage) || total(line.accepted) + total(line.rejected) + total(line.missing) !== BigInt(source.packages) * BigInt(source.baseUnitsPerPackage) ||
      (total(line.rejected) + total(line.missing) > BigInt(0) && !line.exceptionReason?.trim()) || line.exceptionReason !== null && (line.exceptionReason !== line.exceptionReason.trim() || /[\u0000-\u001f\u007f]/.test(line.exceptionReason))) throw new AccessFailure(400, 'invalid_delivery_receipt');
  }
  return Object.freeze({ actorId, challengeId: review.challengeId, body: Object.freeze({ token, consent: true, consentVersion: review.consentVersion, reason,
    lines: Object.freeze(parsed.data.map(line => Object.freeze({ ...line, accepted: Object.freeze(line.accepted), rejected: Object.freeze(line.rejected), missing: Object.freeze(line.missing) }))) }) });
}
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) { const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {};
    throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
function check(current: () => boolean, sent = false) { if (!current()) { if (sent) throw new AccessFailure(503, 'commit_outcome_unknown'); throw new RequestNotSentError(409, 'receipt_view_stale'); } }
export function createReceiptGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async recipients(actorId: string, shipmentId: string, beforeId: string | null) { return parse(z.object({ items: z.array(z.object({ id, name }).strict()).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/fulfillment/receipts/{shipmentId}/recipients/query',
      { body: { purpose: 'shipment-receipt', beforeId }, params: { path: { shipmentId }, header: headers(actorId) } }))); },
    async issue(actorId: string, shipmentId: string, documentId: string, recipientId: string, reason: string, key: string, current: () => boolean) {
      check(current); const result = parseReceiptChallenge(data(await client.POST('/api/v1/fulfillment/receipts/{shipmentId}/challenges', { body: { documentId, recipientId, reason },
        params: { path: { shipmentId }, header: { ...headers(actorId), 'Idempotency-Key': key } } }))); check(current, true);
      if (result.shipmentId !== shipmentId || result.documentId !== documentId || result.recipientId !== recipientId) throw new AccessFailure(502, 'shipment_receipt_response_invalid'); return result;
    },
    async review(actorId: string, challengeId: string, token: string) { const result = parse(reviewSchema, data(await client.POST('/api/v1/fulfillment/receipts/challenges/{challengeId}/query',
      { body: { token }, params: { path: { challengeId }, header: headers(actorId) } }))); if (result.challengeId !== challengeId) throw new AccessFailure(502, 'shipment_receipt_response_invalid'); return result; },
    async confirm(command: ReturnType<typeof prepareReceipt>, key: string, current: () => boolean) {
      check(current); const result = parseReceipt(data(await client.POST('/api/v1/fulfillment/receipts/challenges/{challengeId}/confirm', { body: { ...command.body, lines: [...command.body.lines] },
        params: { path: { challengeId: command.challengeId }, header: { ...headers(command.actorId), 'Idempotency-Key': key } } }))); check(current, true);
      if (result.recipientId !== command.actorId) throw new AccessFailure(502, 'shipment_receipt_response_invalid'); return result;
    },
    async read(actorId: string, shipmentId: string) { const result = data(await client.POST('/api/v1/fulfillment/receipts/{shipmentId}/query', { body: { purpose: 'shipment-receipt' }, params: { path: { shipmentId }, header: headers(actorId) } }));
      if (result.status === 'not_received' && result.receipt === null) return null; const receipt = parseReceipt(result.receipt);
      if (result.status !== 'received' || receipt.shipmentId !== shipmentId) throw new AccessFailure(502, 'shipment_receipt_response_invalid'); return receipt; },
    async recover(actorId: string, key: string, challenge: boolean) {
      if (challenge) { const result = data(await client.POST('/api/v1/fulfillment/receipts/challenge-operations/query', { body: { purpose: 'operation-recovery', key }, params: { header: headers(actorId) } }));
        if (result.status === 'not_confirmed' && result.challenge === null) return null; const row = parseReceiptChallenge(result.challenge);
        if (result.status !== 'committed' || row.token !== null) throw new AccessFailure(502, 'shipment_receipt_response_invalid'); return { state: 'committed' as const, result: row }; }
      const result = data(await client.POST('/api/v1/fulfillment/receipts/operations/query', { body: { purpose: 'operation-recovery', key }, params: { header: headers(actorId) } }));
      if (result.status === 'not_confirmed' && result.receipt === null) return null; const row = parseReceipt(result.receipt);
      if (result.status !== 'committed' || row.recipientId !== actorId) throw new AccessFailure(502, 'shipment_receipt_response_invalid'); return { state: 'committed' as const, result: row };
    },
  };
}
