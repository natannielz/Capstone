import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPublicShowcaseGateway, parsePublicShowcase } from '../src/modules/catalog/public-showcase-client';

const actorId = '42';
const row = { assortmentId: '7', owner: { kind: 'fulfillmentShop', organizationId: '3', shopId: '5', warehouseId: null, channelId: null },
  listedProducts: '24', isPublic: false, revision: '2', pending: null, ownerName: 'Toko Melati' };
const pending = { ...row, pending: { proposalId: '901', makePublic: true, proposedBy: '41', proposedAt: '2026-10-21T02:00:00+00:00', reason: 'Barang umum untuk pengunjung' } };
function gateway(handler: (request: Request) => Promise<Response>) {
  return createPublicShowcaseGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    assert.equal(input.method, 'POST'); assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    return handler(input);
  } }));
}

test('staff see each assortment with whether guests may browse it and any change waiting for a second person', async () => {
  const items = await gateway(async request => {
    assert.equal(new URL(request.url).pathname, '/api/v1/catalog/public-showcase/query');
    assert.deepEqual(await request.json(), { purpose: 'catalog-public-showcase' });
    return Response.json({ items: [row, pending] });
  }).list(actorId);
  assert.equal(items[0].ownerLabel, 'Toko Melati'); assert.equal(parsePublicShowcase({ ...row, ownerName: null }).ownerLabel, 'Toko 5 (nama belum tersedia)'); assert.equal(items[0].listedProducts, 24); assert.equal(items[0].isPublic, false); assert.equal(items[0].pending, null);
  assert.equal(items[1].pending?.makePublic, true); assert.equal(items[1].pending?.proposedBy, '41');
});

test('a proposal is fenced to the revision staff saw and a decision to the single proposal revision', async () => {
  const seen: Request[] = [];
  const client = gateway(async request => { seen.push(request.clone()); return Response.json({ proposalId: '901', decisionId: null, assortmentId: '7', status: 'proposed', headRevision: null }); });
  await client.propose(actorId, parsePublicShowcase(row), true, 'Barang umum untuk pengunjung', 'showcase-key-1');
  await client.decide(actorId, '901', false, 'Masih ada barang khusus kontrak', 'showcase-key-2');
  assert.equal(new URL(seen[0].url).pathname, '/api/v1/catalog/public-showcase/7/proposals');
  assert.equal(seen[0].headers.get('If-Match'), '"2"'); assert.equal(seen[0].headers.get('Idempotency-Key'), 'showcase-key-1');
  assert.deepEqual(await seen[0].json(), { makePublic: true, reason: 'Barang umum untuk pengunjung' });
  assert.equal(new URL(seen[1].url).pathname, '/api/v1/catalog/public-showcase/proposals/901/reject'); assert.equal(seen[1].headers.get('If-Match'), '"1"');
});

test('a refused command keeps the server code so the page can explain it', async () => {
  const client = gateway(async () => Response.json({ code: 'step_up_required' }, { status: 403 }));
  await assert.rejects(() => client.decide(actorId, '901', true, 'Sesuai', 'showcase-key-3'),
    (error: unknown) => (error as { status?: number; code?: string }).status === 403 && (error as { code?: string }).code === 'step_up_required');
});

test('a malformed staff answer is refused instead of shown', () => {
  for (const broken of [{ ...row, assortmentId: '0' }, { ...row, isPublic: 'no' }, { ...row, owner: { ...row.owner, kind: 'buyer' } },
    { ...pending, pending: { ...pending.pending, reason: '' } }, { ...row, listedProducts: 24 }])
    assert.throws(() => parsePublicShowcase(broken), (error: unknown) => (error as { code?: string }).code === 'catalog_public_response_invalid');
});
