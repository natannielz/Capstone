import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderDraftGateway, draftAccessLost, parseDraftInput, prepareDraftCommand } from '../src/modules/ordering/order-draft-client';
import { draftMessage } from '../src/modules/ordering/DraftSave';
import { AccessFailure } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath } from '../src/modules/identity/identity-workflow';
const actor = '9007199254740993', id = '9007199254740999';
const input = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: '100', definitionId: '200', definitionVersion: '1', priceId: '300', priceVersion: '1', packages: 2 } ],
  delivery: { recipientName: 'Penerima fixture', phone: '+62 812-3456-7890', address: 'Jalan Contoh 1\nLantai 2', city: 'Jakarta', postalCode: '12810', instructions: null }, purchaseReference: null };
const result = { draftId: id, versionId: '9007199254741000', revision: '1', recordedAt: '2026-09-29T01:00:00Z' };
const draft = { id, revision: '1', createdAt: result.recordedAt, updatedAt: result.recordedAt, input };
function gateway(handler: (request: Request) => Promise<Response>) { return createOrderDraftGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async value => {
  assert.ok(value instanceof Request); if (value.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
  assert.equal(value.headers.get('X-Expected-Actor-Id'), actor); assert.equal(value.headers.get('X-CSRF-Token'), 'csrf'); return handler(value);
} })); }
test('draft command freezes a copy of explicit selections and preserves private delivery only in the request', async () => {
  const raw = structuredClone(input), command = prepareDraftCommand(actor, null, '0', raw, 'Save draft'); raw.selections[0].packages = 999;
  assert.equal(command.input.selections[0].packages, 2); assert.throws(() => parseDraftInput({ ...input, selections: [...input.selections, input.selections[0]] }));
  const saved = await gateway(async request => {
    assert.equal(request.headers.get('Idempotency-Key'), 'original'); assert.equal(request.headers.get('If-Match'), '"0"');
    assert.deepEqual(await request.json(), { draftId: null, input, reason: 'Save draft' }); return Response.json(result);
  }).submit(command, 'original'); assert.deepEqual(saved, result);
});
test('draft list and reads bind exact branch, identity, cursor and revision while failed saves are never automatically retried', async () => {
  assert.equal((await gateway(async () => Response.json({ items: [draft], hasMore: false })).list(actor, input.context)).items[0].id, id);
  await assert.rejects(() => gateway(async () => Response.json({ items: [{ ...draft, input: { ...input, context: { ...input.context, branchId: '12' } } }], hasMore: false })).list(actor, input.context));
  await assert.rejects(() => gateway(async () => Response.json(draft)).read(actor, '7'));
  let calls = 0; const failed = gateway(async () => { calls++; throw new Error('Response lost'); });
  await assert.rejects(() => failed.submit(prepareDraftCommand(actor, null, '0', input, 'Save draft'), 'original')); assert.equal(calls, 1);
});
test('draft recovery has a dedicated lookup-only adapter and never closes or reconstructs the original save', async () => {
  assert.equal(recoveryPage('ordering.draft.save').href, '/ordering/drafts');
  assert.equal(operationLabel('ordering.draft.save'), 'Penyimpanan draf pesanan');
  assert.equal(identityReturnPath('/ordering/drafts'), '/ordering/drafts');
  assert.ok(identityDestinations(['order.submit']).some(item => item.href === '/ordering/drafts'));
  assert.ok(!identityDestinations(['catalog.read']).some(item => item.href === '/ordering/drafts'));
  const storage = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const entry = journal.remember(actor, 'ordering.draft.save', 'original');
  assert.deepEqual(Object.keys(JSON.parse([...storage.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  const legacy = { lookup: async () => { throw new Error('Legacy fallback used'); }, close: async () => { throw new Error('Legacy close used'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy), { code: 'recovery_adapter_unavailable' });
  const pending = { 'ordering.draft': { lookup: async () => null } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, pending), { state: 'unconfirmed' }); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, pending, 'Close it'), { code: 'recovery_close_unsupported' });
  const receipt = await gateway(async request => { assert.ok(request.url.endsWith('/operations/query')); assert.deepEqual(await request.json(), { purpose: 'operation-recovery', key: 'original' }); return Response.json({ status: 'committed', result }); }).recover(actor, 'original');
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'ordering.draft': { lookup: async () => receipt } }), { state: 'committed', result: { id, version: '1' } });
  assert.equal(journal.list(actor).length, 0);
});

test('a package taken out of the branch catalog keeps the draft open; only lost access closes it', () => {
  assert.equal(draftAccessLost(new AccessFailure(403, 'order_package_unavailable')), false);
  assert.match(draftMessage(new AccessFailure(403, 'order_package_unavailable')), /^Sebagian kemasan di draf ini tidak lagi tersedia untuk cabang ini\./);
  assert.equal(draftAccessLost(new AccessFailure(403, 'forbidden')), true);
  assert.equal(draftAccessLost(new AccessFailure(403, 'order_scope_unavailable')), true);
  assert.equal(draftAccessLost(new AccessFailure(401, 'unauthenticated')), true);
  assert.equal(draftAccessLost(new AccessFailure(409, 'actor_changed')), true);
  assert.equal(draftAccessLost(new AccessFailure(409, 'order_draft_changed')), false);
  assert.equal(draftAccessLost(new Error('offline')), false);
});
