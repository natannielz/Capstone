import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createReceiptProofGateway, parseReceiptProof, prepareReceiptProof } from '../src/modules/fulfillment/receipt-proof-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';

const actorId = '9007199254740993';
const proof = { id: '9007199254740995', shipmentId: '9007199254740996', receiptId: '9007199254740997', shipmentRevision: '1', version: '1', contentHash: 'b'.repeat(64), byteLength: '120', issuedAt: '2026-09-30T01:00:00Z', issuedAsRecipient: true };
test('receipt proof preserves exact references and rejects mixed content/version and authority contracts', () => {
  assert.deepEqual(parseReceiptProof(proof), proof);
  assert.equal(parseReceiptProof({ ...proof, byteLength: '2097152' }).byteLength, '2097152');
  for (const changed of [{ ...proof, receiptId: 9007199254740997 }, { ...proof, issuedAsRecipient: 'true' }, { ...proof, version: '2' }, { ...proof, contentHash: 'b'.repeat(64) + '\n' }, { ...proof, byteLength: '2097153' }, { ...proof, token: 'secret' }]) assert.throws(() => parseReceiptProof(changed));
  assert.ok(Object.isFrozen(prepareReceiptProof(actorId, proof.shipmentId, proof.receiptId, true, 'Salinan penerima')));
});
test('proof command uses current actor and CSRF, sends once, and binds returned receipt and authority', async () => {
  const requests: Request[] = []; let wrong = false;
  const gateway = createReceiptProofGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    requests.push(request); assert.equal(request.method, 'POST'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    return Response.json({ ...proof, issuedAsRecipient: !wrong });
  } }));
  const command = prepareReceiptProof(actorId, proof.shipmentId, proof.receiptId, true, 'Salinan penerima');
  await assert.rejects(() => gateway.issue(command, 'proof-key', () => false)); assert.equal(requests.length, 0);
  assert.deepEqual(await gateway.issue(command, 'proof-key', () => true), proof); assert.equal(requests.length, 1);
  assert.deepEqual(await requests[0].clone().json(), { reason: command.reason, receiptId: proof.receiptId, asRecipient: true });
  wrong = true; await assert.rejects(() => gateway.issue(command, 'proof-key', () => true)); assert.equal(requests.length, 2);
});
test('proof recovery is receipt-only and preserves unknown, malformed and late results without another command', async () => {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actorId, 'fulfillment.receipt-proof.issue', 'proof-key');
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'fulfillment.receipt-proof': { lookup: async () => null } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, absent), { canCheck: true, canClose: false, unavailable: null });
  assert.deepEqual(Object.keys(JSON.parse([...values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  const bad = { 'fulfillment.receipt-proof': { lookup: async () => ({ state: 'committed' as const, result: { ...proof, version: '2' } }) } };
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, bad as never)); assert.equal(values.size, 1);
  const good = { 'fulfillment.receipt-proof': { lookup: async () => ({ state: 'committed' as const, result: parseReceiptProof(proof) }) } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, good)).state, 'committed'); assert.equal(values.size, 0);
});
test('proof download checks exact bytes and SHA-256 before returning any private file', async () => {
  const bytes = new TextEncoder().encode('<!doctype html><p>Bukti penerimaan</p>');
  const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), byte => byte.toString(16).padStart(2, '0')).join('');
  const document = parseReceiptProof({ ...proof, contentHash: hash, byteLength: String(bytes.length) }); let corrupt = false;
  const gateway = createReceiptProofGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    if ((request as Request).url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    return new Response(corrupt ? bytes.map(value => value === 66 ? 67 : value) : bytes, { headers: { 'content-type': 'text/html; charset=utf-8' } });
  } }));
  assert.equal((await gateway.download(actorId, document, true)).size, bytes.length);
  corrupt = true; await assert.rejects(() => gateway.download(actorId, document, true));
});
