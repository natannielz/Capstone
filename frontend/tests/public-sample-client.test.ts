import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createPublicSampleGateway, parsePublicSamplePage } from '../src/modules/catalog/public-sample-client';

const actorId = '42';
const item = { familyId: '7', familyName: 'Pensil 2B', ownerLabel: 'Toko Melati', packages: '2', isSample: false, pending: null };
const waiting = { ...item, familyId: '8', familyName: 'Kopi bubuk', pending: { proposalId: '901', adding: true, ownProposal: false } };
const proposal = { proposalId: '901', ownProposal: false, ownerLabel: 'Toko Melati', reason: 'Barang umum untuk tamu', proposedAt: '2026-10-21T02:00:00+00:00',
  adds: [{ familyId: '8', familyName: 'Kopi bubuk' }], removes: [] };
const page = { items: [item, waiting], total: '2', proposals: [proposal], sampleCount: '0', sampleLimit: '200' };
function gateway(handler: (request: Request) => Promise<Response>) {
  return createPublicSampleGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    assert.equal(input.method, 'POST'); assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    return handler(input);
  } }));
}

test('staff search products and see whether guests see each one and what waits for a second person', async () => {
  const result = await gateway(async request => {
    assert.equal(new URL(request.url).pathname, '/api/v1/catalog/public-samples/query');
    assert.deepEqual(await request.json(), { purpose: 'catalog-public-samples', search: 'pensil', offset: 0, limit: 50 });
    return Response.json(page);
  }).list(actorId, { search: 'pensil' });
  assert.equal(result.total, 2); assert.equal(result.sampleCount, 0); assert.equal(result.sampleLimit, 200);
  assert.equal(result.items[0].packages, 2); assert.equal(result.items[0].isSample, false); assert.equal(result.items[0].pending, null);
  assert.deepEqual(result.items[1].pending, { proposalId: '901', adding: true, ownProposal: false });
  assert.equal(result.proposals[0].adds[0].familyName, 'Kopi bubuk');
  assert.equal('proposedBy' in result.proposals[0], false);
});

test('one proposal carries every toggled product with a reason, and a decision carries only its reason', async () => {
  const seen: Request[] = [];
  const client = gateway(async request => { seen.push(request.clone()); return Response.json({ proposalId: '901', state: 'pending' }); });
  await client.propose(actorId, { adds: ['8', '7'], removes: ['3'] }, 'Contoh barang umum', 'sample-key-1');
  await client.decide(actorId, '901', false, 'Masih ada barang khusus kontrak', 'sample-key-2');
  await client.decide(actorId, '901', true, 'Diperiksa', 'sample-key-3');
  assert.equal(new URL(seen[0].url).pathname, '/api/v1/catalog/public-samples/proposals');
  assert.equal(seen[0].headers.get('Idempotency-Key'), 'sample-key-1');
  assert.deepEqual(await seen[0].json(), { adds: ['7', '8'], removes: ['3'], reason: 'Contoh barang umum' });
  assert.equal(new URL(seen[1].url).pathname, '/api/v1/catalog/public-samples/proposals/901/reject');
  assert.deepEqual(await seen[1].json(), { reason: 'Masih ada barang khusus kontrak' });
  assert.equal(new URL(seen[2].url).pathname, '/api/v1/catalog/public-samples/proposals/901/approve');
});

test('a refused command keeps the server code so the page can explain it', async () => {
  const client = gateway(async () => Response.json({ code: 'self_approval' }, { status: 403 }));
  await assert.rejects(() => client.decide(actorId, '901', true, 'Sesuai', 'sample-key-4'),
    (error: unknown) => (error as { status?: number }).status === 403 && (error as { code?: string }).code === 'self_approval');
});

test('a malformed staff answer is refused instead of shown', () => {
  for (const broken of [{ ...page, items: [{ ...item, familyId: '0' }] }, { ...page, items: [{ ...item, isSample: 'no' }] },
    { ...page, items: [{ ...item, packages: 2 }] }, { ...page, proposals: [{ ...proposal, reason: '' }] },
    { ...page, items: [{ ...waiting, pending: { ...waiting.pending, ownProposal: 'yes' } }] }, { ...page, total: undefined }])
    assert.throws(() => parsePublicSamplePage(broken), (error: unknown) => (error as { code?: string }).code === 'catalog_public_response_invalid');
});
