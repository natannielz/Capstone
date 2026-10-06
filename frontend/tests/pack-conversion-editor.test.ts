import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PackConversionEditor } from '../src/modules/inventory/pack-conversion-editor-state';
import type { PackConversionGateway } from '../src/modules/inventory/pack-conversion-client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { parsePackRule, packProposeOperation, packDecideOperation, type PackResult } from '../src/modules/inventory/pack-conversion-workflow';
import { actorId, location, warehouse, source, destination, preview, proposal, reviewPreview, proposeResult, approveResult, rule, session, journalFixture } from './pack-conversion-fixtures';

// Synthetic draft checks. Execute after integrating this exact overlay; no stock execution acceptance.
const second = { ...warehouse, warehouseName: 'Gudang cadangan', location: { ...location, warehouseId: '109' } };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<PackConversionGateway> = {}) {
  const { journal, values } = journalFixture(); let now = 0, keys = 0;
  const gateway: PackConversionGateway = {
    warehouses: async () => [warehouse, second], packages: async (_actor, _location, role) => ({ total: '1', items: [role === 'source' ? source : destination] }),
    preview: async () => preview, proposals: async () => ({ items: [proposal], next: null }), proposal: async () => proposal,
    review: async () => reviewPreview, rules: async () => ({ items: [parsePackRule(rule)], next: null }),
    submit: async () => proposeResult, receipt: async () => null, ...overrides,
  };
  const editor = new PackConversionEditor({ actorId, actions: ['stock.convert'], session, observedAt: 0, now: () => now,
    gateway, journal, newKey: () => 'pack-' + ++keys });
  return { editor, journal, values, advance: (value: number) => { now = value; } };
}
async function prepare(editor: PackConversionEditor) {
  await editor.start(); await editor.chooseWarehouse(location);
  editor.choosePackage('source', source.skuId); editor.choosePackage('destination', destination.skuId);
  editor.changeDraft('sourceRatio', '1'); editor.changeDraft('destinationRatio', '12'); editor.changeDraft('reason', 'Rasio telah diperiksa');
  await editor.previewRule(); editor.reviewProposal();
}
const legacy = { lookup: async () => { assert.fail('pack recovery cannot fall back to generic lookup'); }, close: async () => { assert.fail('pack recovery cannot close or resubmit'); } };

test('reload with a pending reference loads warehouse choices only after recovery completes', async () => {
  let loads = 0, writes = 0;
  const data = setup({ warehouses: async () => { loads++; return [warehouse]; }, submit: async () => { writes++; return proposeResult; } });
  const entry = data.journal.remember(actorId, packProposeOperation, 'reloaded-original');
  await data.editor.start(); assert.equal(loads, 0); assert.equal(data.editor.locked, true);
  data.editor.setRecovering(true); data.journal.confirmed(entry); data.editor.reconciled();
  assert.equal(loads, 0); data.editor.setRecovering(false);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(loads, 1); assert.deepEqual(data.editor.snapshot().warehouses, [warehouse]);
  assert.equal(data.editor.snapshot().warehouse, null); assert.equal(writes, 0);
  await data.editor.chooseWarehouse(location); assert.deepEqual(data.editor.snapshot().warehouse?.location, location);
});

test('only StockConvert is required; choices and conserved ratio must be deliberate', async () => {
  const data = setup(); await data.editor.start();
  assert.equal(data.editor.snapshot().warehouse, null);
  await data.editor.chooseWarehouse({ ...location, warehouseId: '4' }); assert.equal(data.editor.snapshot().warehouse, null);
  await data.editor.chooseWarehouse(location); assert.equal(data.editor.snapshot().source, null); assert.equal(data.editor.snapshot().destination, null);
  data.editor.choosePackage('source', '4'); assert.equal(data.editor.snapshot().source, null);
  data.editor.choosePackage('source', source.skuId); data.editor.choosePackage('destination', destination.skuId);
  data.editor.changeDraft('sourceRatio', '1'); data.editor.changeDraft('destinationRatio', '11'); await data.editor.previewRule();
  assert.equal(data.editor.snapshot().preview, null);
  data.editor.changeDraft('destinationRatio', '12'); data.editor.changeDraft('reason', 'Rasio telah diperiksa'); await data.editor.previewRule();
  assert.deepEqual(data.editor.snapshot().preview, preview);
  data.editor.choosePackage('source', ''); assert.equal(data.editor.snapshot().source, null); assert.equal(data.editor.snapshot().preview, null);
  const { journal } = journalFixture();
  assert.throws(() => new PackConversionEditor({ actorId, actions: ['stock.count', 'catalog.manage'], session, observedAt: 0, now: () => 0,
    gateway: {} as PackConversionGateway, journal }), /access_denied/);
});

test('review freezes body and duplicate clicks send one request with its original key', async () => {
  const reply = deferred<PackResult>(), sent: { key: string; reason: string }[] = [];
  const data = setup({ submit: async (command, key) => { sent.push({ key, reason: command.body.reason }); return reply.promise; } });
  await prepare(data.editor); const command = data.editor.snapshot().review!;
  assert.ok(Object.isFrozen(command.body)); assert.ok(Object.isFrozen(command.preview));
  data.editor.changeDraft('reason', 'Changed after review'); assert.equal(data.editor.snapshot().review!.body.reason, 'Rasio telah diperiksa');
  await data.editor.submitReviewed(false); assert.equal(sent.length, 0);
  const first = data.editor.submitReviewed(true); await data.editor.submitReviewed(true); assert.equal(sent.length, 1);
  reply.resolve(proposeResult); await first; await data.editor.submitReviewed(true);
  assert.equal(sent.length, 1); assert.deepEqual(sent[0], { key: 'pack-1', reason: 'Rasio telah diperiksa' });
  assert.equal(data.editor.snapshot().attempt, null); assert.equal(data.journal.list(actorId).length, 0);
});

test('lost result stays locked; recovery checks the original reference without another write or stored body', async () => {
  for (const failure of [new TypeError('reply lost'), new AccessFailure(503, 'commit_outcome_unknown')]) {
  let writes = 0, lookups = 0;
  const data = setup({ submit: async () => { writes++; throw failure; }, receipt: async entry => {
    lookups++; assert.equal(entry.actorId, actorId); assert.equal(entry.key, 'pack-1'); assert.equal(entry.operation, packProposeOperation);
    return { state: 'committed', result: proposeResult };
  } });
  await prepare(data.editor); await data.editor.submitReviewed(true);
  assert.equal(data.editor.locked, true); assert.equal('retry' in data.editor, false);
  assert.notEqual(data.editor.snapshot().attempt, null);
  data.editor.changeDraft('reason', 'Changed'); await data.editor.changeMode('rules'); await data.editor.submitReviewed(true);
  assert.equal(writes, 1); assert.equal(data.editor.snapshot().mode, 'prepare');
  const [entry] = data.journal.list(actorId), persisted = JSON.parse([...data.values.values()][0]);
  assert.deepEqual(Object.keys(persisted).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.deepEqual(operationRecoveryCapabilities(packProposeOperation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  const adapters = { 'inventory.pack-conversion.rule': { lookup: data.editor.receipt } };
  assert.deepEqual(operationRecoveryCapabilities(packDecideOperation, adapters), { canCheck: true, canClose: false, unavailable: null });
  const outcome = await reconcileOperationRecovery(entry, data.journal, legacy, adapters, undefined, data.editor.isCurrent);
  assert.deepEqual(outcome, { state: 'committed', result: { id: proposeResult.proposalId, version: '1' } });
  data.editor.reconciled(); assert.equal(data.editor.locked, false); assert.equal(data.editor.snapshot().attempt, null);
  assert.equal(lookups, 1); assert.equal(writes, 1); assert.equal(data.journal.list(actorId).length, 0);
  assert.equal(recoveryPage(packDecideOperation).href, '/inventory/pack-conversions'); assert.match(operationLabel(packDecideOperation), /aturan konversi kemasan/);
  }
});

test('unconfirmed and malformed receipts retain the reference; unknown operations cannot be closed', async () => {
  const data = setup({ submit: async () => { throw new TypeError('reply lost'); } });
  await prepare(data.editor); await data.editor.submitReviewed(true); const [entry] = data.journal.list(actorId);
  const adapters = { 'inventory.pack-conversion.rule': { lookup: data.editor.receipt } };
  assert.deepEqual(await reconcileOperationRecovery(entry, data.journal, legacy, adapters), { state: 'unconfirmed' });
  assert.equal(data.journal.list(actorId).length, 1); assert.equal(data.editor.locked, true);
  await assert.rejects(() => reconcileOperationRecovery(entry, data.journal, legacy, adapters, 'Close'), /recovery_close_unsupported/);
  await assert.rejects(() => reconcileOperationRecovery(entry, data.journal, legacy,
    { 'inventory.pack-conversion.rule': { lookup: async () => ({ state: 'committed', result: approveResult }) } }));
  assert.equal(data.journal.list(actorId).length, 1);
});

test('latest authoritative detail canReview controls review, independent of list capability', async () => {
  const denied = setup({ proposal: async () => ({ ...proposal, canReview: false }) });
  await denied.editor.start(); await denied.editor.chooseWarehouse(location); await denied.editor.changeMode('proposals'); await denied.editor.chooseProposal(proposal.id);
  await denied.editor.previewDecision('approve'); assert.equal(denied.editor.snapshot().decisionPreview, null);
  const allowed = setup({ proposals: async () => ({ items: [{ ...proposal, canReview: false }], next: null }), submit: async () => approveResult });
  await allowed.editor.start(); await allowed.editor.chooseWarehouse(location); await allowed.editor.changeMode('proposals'); await allowed.editor.chooseProposal(proposal.id);
  allowed.editor.changeDraft('reason', 'Rasio telah diperiksa orang berbeda'); await allowed.editor.previewDecision('approve'); allowed.editor.reviewDecision();
  assert.equal(allowed.editor.snapshot().review?.operation, packDecideOperation);
  await allowed.editor.submitReviewed(true); assert.deepEqual(allowed.editor.snapshot().result, approveResult);
});

test('ordinary MFA remains usable beyond five minutes but exact session expiry hides data and preserves references', async () => {
  const data = setup(); data.advance(5 * 60_000); await prepare(data.editor); assert.equal(data.editor.snapshot().expired, false);
  data.advance(10 * 60_000); await data.editor.submitReviewed(true); assert.equal(data.editor.snapshot().expired, true);
  assert.equal(data.editor.snapshot().warehouse, null); assert.equal(data.editor.snapshot().review, null); assert.equal(data.editor.snapshot().result, null);
  const lost = setup({ submit: async () => { throw new TypeError('reply lost'); } }); await prepare(lost.editor); await lost.editor.submitReviewed(true);
  lost.advance(10 * 60_000); lost.editor.checkSession(); assert.equal(lost.editor.snapshot().attempt, null); assert.equal(lost.journal.list(actorId).length, 1);
  await assert.rejects(() => lost.editor.receipt(lost.journal.list(actorId)[0]), /recovery_request_stale/);
});

test('warehouse replacement and expiry abort stale responses; actor or permission denial suspends the view', async () => {
  const pending = deferred<{ total: string; items: typeof source[] }>(); const signals: AbortSignal[] = []; let calls = 0;
  const data = setup({ packages: async (_actor, _location, role, _search, _offset, signal) => {
    if (role === 'destination') return { total: '1', items: [destination] }; signals.push(signal!);
    return ++calls === 1 ? pending.promise : { total: '1', items: [{ ...source, skuId: '88' }] };
  } });
  await data.editor.start(); const first = data.editor.chooseWarehouse(location); await data.editor.chooseWarehouse(second.location);
  pending.resolve({ total: '1', items: [source] }); await first; assert.equal(signals[0].aborted, true);
  assert.equal(data.editor.snapshot().sourcePage.items[0].skuId, '88');
  const late = deferred<typeof preview>(); const expiry = setup({ preview: async () => late.promise });
  await expiry.editor.start(); await expiry.editor.chooseWarehouse(location); expiry.editor.choosePackage('source', source.skuId); expiry.editor.choosePackage('destination', destination.skuId);
  expiry.editor.changeDraft('sourceRatio', '1'); expiry.editor.changeDraft('destinationRatio', '12'); const request = expiry.editor.previewRule();
  expiry.advance(10 * 60_000); expiry.editor.checkSession(); late.resolve(preview); await request; assert.equal(expiry.editor.snapshot().preview, null);
  for (const failure of [new AccessFailure(409, 'actor_changed'), new AccessFailure(403, 'scope_denied')]) {
    const revoked = setup({ warehouses: async () => { throw failure; } }); await revoked.editor.start();
    assert.equal(revoked.editor.snapshot().suspended, true); assert.equal(revoked.editor.snapshot().warehouses.length, 0);
  }
});
