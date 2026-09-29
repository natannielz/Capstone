import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createUnitTokoClient } from '../src/api/client';
import { createShipmentDocumentGateway, parseShipmentDocument, prepareShipmentDocument } from '../src/modules/fulfillment/shipment-document-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';

const actorId = '9007199254741000', shipmentId = '9007199254741001', html = '<!doctype html><html lang="id"><title>Surat Jalan</title><p>12 satuan</p></html>';
const document = { id: '9007199254741002', shipmentId, shipmentRevision: '1', version: '1', byteLength: String(Buffer.byteLength(html)),
  contentHash: createHash('sha256').update(html).digest('hex'), issuedAt: '2026-09-30T01:00:00Z' };

test('issued document references preserve big IDs, bounded lengths and exact version/hash', () => {
  assert.deepEqual(parseShipmentDocument(document), document);
  for (const patch of [{ id: Number(document.id) }, { version: '2' }, { shipmentRevision: '2' }, { byteLength: '524289' }, { byteLength: '0' }, { contentHash: '0' }, { issuedAt: '' }])
    assert.throws(() => parseShipmentDocument({ ...document, ...patch }));
});

test('document transport binds issue/read/download to current account and verifies exact received bytes', async () => {
  const requests: Request[] = [];
  const client = createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    requests.push(request); assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    if (request.url.endsWith('/download')) return new Response(html, { headers: { 'content-type': 'text/html; charset=utf-8' } });
    if (request.url.endsWith('/query')) return Response.json({ status: 'issued', document }); return Response.json(document);
  } });
  const gateway = createShipmentDocumentGateway(client), command = prepareShipmentDocument(actorId, shipmentId, 'Sesuai keberangkatan');
  assert.equal((await gateway.read(actorId, shipmentId))?.id, document.id);
  assert.equal((await gateway.issue(command, 'issue-1', () => true)).id, document.id);
  assert.equal(requests[1].headers.get('If-Match'), '"1"'); assert.equal(requests[1].headers.get('Idempotency-Key'), 'issue-1');
  assert.equal(await (await gateway.download(actorId, parseShipmentDocument(document))).text(), html);
  await assert.rejects(() => gateway.download(actorId, parseShipmentDocument({ ...document, contentHash: 'a'.repeat(64) })));
  const count = requests.length; await assert.rejects(() => gateway.issue(command, 'issue-2', () => false)); assert.equal(requests.length, count);
  await assert.rejects(() => gateway.read(actorId, '99'));
});

test('lost document issuance has lookup-only recovery and missing receipt remains unresolved', async () => {
  const values = new Map<string, string>();
  const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actorId, 'fulfillment.surat-jalan.issue', 'original-key');
  assert.deepEqual(Object.keys(JSON.parse([...values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('wrong recovery'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'fulfillment.surat-jalan': { lookup: async () => null } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, absent), { canCheck: true, canClose: false, unavailable: null });
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  const found = { 'fulfillment.surat-jalan': { lookup: async () => ({ state: 'committed' as const, result: parseShipmentDocument(document) }) } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, found)).state, 'committed'); assert.equal(values.size, 0);
});
