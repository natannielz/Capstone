import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createReceiptGateway, parseReceipt, parseReceiptChallenge, parseReceiptLink, prepareReceipt, type ReceiptReview } from '../src/modules/fulfillment/receipt-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';

const token = 'a'.repeat(64), actorId = '9007199254741000';
const review: ReceiptReview = { challengeId: '9007199254741001', shipmentId: '2', documentId: '3', documentVersion: '1', documentHash: 'b'.repeat(64), recipientName: 'Penerima', expiresAt: '2026-09-30T01:15:00Z', serverTime: '2026-09-30T01:00:00Z',
  consentVersion: 'delivery-receipt-v1', consentText: 'Saya telah memeriksa kiriman.', lines: [{ skuId: '4', skuCode: 'SKU-4', productName: 'ATK', definitionId: '5', packageLabel: 'Dus', baseUnitCode: 'piece', baseUnitsPerPackage: 12, packages: 1 }] };
const lines = [{ skuId: '4', accepted: { packages: 0, looseUnits: 6 }, rejected: { packages: 0, looseUnits: 4 }, missing: { packages: 0, looseUnits: 2 }, exceptionReason: 'Rusak dan kurang' }];
const receipt = { id: '6', shipmentId: '2', documentId: '3', recipientId: actorId, recipientName: 'Penerima', recordedAt: '2026-09-30T01:01:00Z', hasException: true,
  lines: [{ ...lines[0], definitionId: '5', dispatchedBaseUnits: '12', acceptedBaseUnits: '6', rejectedBaseUnits: '4', missingBaseUnits: '2' }] };
const challenge = { id: review.challengeId, shipmentId: '2', documentId: '3', recipientId: actorId, recipientName: 'Penerima', expiresAt: review.expiresAt, serverTime: review.serverTime, token: null };
test('receipt link and response preserve lossless IDs and reject malformed consent, totals and packaging', () => {
  assert.deepEqual(parseReceiptLink('#' + review.challengeId + '.' + token), { challengeId: review.challengeId, token });
  for (const fragment of ['?token=' + token, '#0.' + token, '#' + review.challengeId + '.' + token + '\n', '#9223372036854775808.' + token]) assert.throws(() => parseReceiptLink(fragment));
  assert.deepEqual(parseReceipt(receipt), receipt); assert.deepEqual(parseReceiptChallenge(challenge), challenge);
  assert.throws(() => parseReceipt({ ...receipt, hasException: false }));
  assert.throws(() => parseReceipt({ ...receipt, id: 6 }));
  assert.throws(() => parseReceipt({ ...receipt, lines: [{ ...receipt.lines[0], missingBaseUnits: '1' }] }));
  assert.throws(() => parseReceipt({ ...receipt, lines: [{ ...receipt.lines[0], acceptedBaseUnits: 'not-a-number' }] }), /shipment_receipt_response_invalid/);
  assert.throws(() => prepareReceipt(actorId, review, token, lines, false, 'Sesuai hitungan'));
  assert.throws(() => prepareReceipt(actorId, review, token, [{ ...lines[0], exceptionReason: null }], true, 'Sesuai hitungan'));
  assert.throws(() => prepareReceipt(actorId, review, token, [{ ...lines[0], accepted: { packages: 0, looseUnits: 12 } }], true, 'Sesuai hitungan'));
  const command = prepareReceipt(actorId, review, token, lines, true, 'Sesuai hitungan'); assert.ok(Object.isFrozen(command.body.lines[0].accepted));
});
test('receipt transport keeps secret in POST body and binds confirmation to the active recipient', async () => {
  const requests: Request[] = []; const client = createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    requests.push(request); assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    assert.ok(!request.url.includes(token)); assert.equal(request.method, 'POST'); return Response.json(request.url.endsWith('/confirm') ? receipt : review);
  } });
  const gateway = createReceiptGateway(client); assert.deepEqual(await gateway.review(actorId, review.challengeId, token), review);
  const command = prepareReceipt(actorId, review, token, lines, true, 'Sesuai hitungan');
  assert.deepEqual(await gateway.confirm(command, 'receipt-key', () => true), receipt);
  assert.equal(requests[1].headers.get('Idempotency-Key'), 'receipt-key');
  await assert.rejects(() => gateway.confirm(command, 'receipt-key', () => false)); assert.equal(requests.length, 2);
});
test('receipt recovery never persists credentials, resubmits, closes or clears an unconfirmed result', async () => {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  for (const operation of ['fulfillment.receipt.confirm', 'fulfillment.receipt.challenge.issue']) {
    const entry = journal.remember(actorId, operation, 'original-key'), absent = { 'fulfillment.receipt': { lookup: async () => null } };
    assert.deepEqual(Object.keys(JSON.parse([...values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.deepEqual(operationRecoveryCapabilities(operation, absent), { canCheck: true, canClose: false, unavailable: null });
    assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(values.size, 1);
    await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
    const result = operation.endsWith('.confirm') ? parseReceipt(receipt) : parseReceiptChallenge(challenge);
    assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.receipt': { lookup: async () => ({ state: 'committed', result }) } })).state, 'committed'); assert.equal(values.size, 0);
  }
});
