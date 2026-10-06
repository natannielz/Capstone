import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AccessFailure } from '../src/modules/administration/workflow';
import { OperationJournal } from '../src/modules/administration/operation-journal';
import { StockReleaseEditor } from '../src/modules/inventory/stock-release-editor-state';
import type { StockReleaseGateway } from '../src/modules/inventory/stock-release-client';
import { releaseChoiceKey, releaseProposeOperation } from '../src/modules/inventory/stock-release-workflow';
import { actorId, warehouse, location, choice, preview, detail, review, proposed, session, MemoryStorage } from './stock-release-fixtures';

function setup(changes: Partial<StockReleaseGateway> = {}) {
  const storage = new MemoryStorage(); let now = 0, submissions = 0;
  const gateway: StockReleaseGateway = { warehouses: async () => [warehouse], sources: async () => ({ items: [choice], unavailableCount: '0' }),
    history: async () => ({ items: [{ proposalId: detail.proposal.id, status: 'proposed', receiptReference: choice.receiptReference, productName: choice.productName,
      packagingLabel: choice.packagingLabel, lotReference: choice.lotReference, packages: '2', policy: choice.policy, recordedAt: detail.proposal.proposedAt, decidedAt: null }], nextAfter: null }),
    detail: async () => detail, preview: async () => preview, review: async () => review, submit: async () => { submissions++; return proposed; }, receipt: async () => null, ...changes };
  const journal = new OperationJournal(() => storage), editor = new StockReleaseEditor({ actorId, actions: ['stock.release.propose', 'stock.release.approve'],
    session, observedAt: 0, now: () => now, gateway, journal, newKey: () => 'synthetic-release-original-key' });
  return { editor, journal, storage, gateway, submissions: () => submissions, advance: (time: number) => { now = time; } };
}
async function ready(editor: StockReleaseEditor) {
  await editor.start(); await editor.chooseWarehouse(location); editor.chooseSource(releaseChoiceKey(choice)); editor.changePackages('2');
  editor.changeReason('Pengajuan terpilih'); await editor.previewProposal(); editor.reviewProposal();
}

test('warehouse source/history reads do not compete and a changed selection cannot start stale history', async () => {
  let release!: () => void, reading = false, historyReads = 0;
  const s = setup({ sources: async () => { reading = true; await new Promise<void>(resolve => { release = resolve; }); reading = false; return { items: [choice], unavailableCount: '0' }; },
    history: async () => { assert.equal(reading, false); historyReads++; return { items: [], nextAfter: null }; } });
  await s.editor.start(); const first = s.editor.chooseWarehouse(location);
  assert.equal(historyReads, 0); await s.editor.refreshWarehouse(); assert.equal(historyReads, 0);
  release(); await first; assert.equal(historyReads, 1);
  const changed = s.editor.refreshWarehouse(); await s.editor.chooseWarehouse(null); release(); await changed;
  assert.equal(historyReads, 1); assert.equal(s.editor.snapshot().warehouse, null); s.editor.dispose();
});
test('selection needs real warehouse/source and deliberate confirmation; refresh keeps persistent metadata only', async () => {
  const s = setup(); await ready(s.editor); await s.editor.confirm(false); assert.equal(s.submissions(), 0);
  await s.editor.confirm(true); assert.equal(s.submissions(), 1); assert.equal(s.editor.snapshot().result?.status, 'proposed');
  assert.equal(s.journal.list(actorId).length, 0); s.editor.dispose();
});
test('unknown result locks immutable original intent and lookup never resubmits or closes it', async () => {
  let sends = 0, lookups = 0;
  const s = setup({ submit: async () => { sends++; throw new TypeError('lost response'); }, receipt: async () => { lookups++; return null; } });
  await ready(s.editor); await s.editor.confirm(true); assert.equal(sends, 1); assert.equal(s.editor.locked, true);
  const original = s.editor.snapshot().attempt; s.editor.changePackages('1'); assert.equal(s.editor.snapshot().attempt, original);
  assert.equal(s.editor.snapshot().packages, '2'); const entry = s.journal.list(actorId)[0];
  assert.deepEqual(Object.keys(JSON.parse(s.storage.getItem(s.storage.key(0)!)!)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(await s.editor.receipt(entry), null); assert.equal(lookups, 1); assert.equal(sends, 1); assert.equal(s.journal.list(actorId).length, 1);
  await assert.rejects(s.editor.receipt({ ...entry, actorId: '99' }), AccessFailure); s.editor.dispose();
});
test('reload with original pending reference blocks discovery and every new write', async () => {
  let reads = 0; const s = setup({ warehouses: async () => { reads++; return [warehouse]; } });
  s.journal.remember(actorId, releaseProposeOperation, 'old-original-reference'); await s.editor.start();
  assert.equal(reads, 0); assert.equal(s.editor.locked, true); assert.equal(s.editor.snapshot().references.length, 1); s.editor.dispose();
});
test('MFA expiry removes a pending independent decision review and session expiry conceals private choices', async () => {
  const s = setup(); await s.editor.start(); await s.editor.chooseWarehouse(location); await s.editor.loadDetail(detail.proposal.id);
  await s.editor.previewDecision('approve'); s.editor.changeDecisionReason('Periksa secara mandiri'); s.editor.reviewDecision();
  assert.ok(s.editor.snapshot().review); s.advance(301000); s.editor.checkSession(); assert.equal(s.editor.snapshot().review, null);
  assert.equal(s.editor.snapshot().decision, null); assert.equal(s.editor.snapshot().fresh, false);
  s.advance(1801000); s.editor.checkSession(); assert.equal(s.editor.snapshot().expired, true); assert.equal(s.editor.snapshot().warehouses.length, 0); assert.equal(s.editor.snapshot().detail, null); s.editor.dispose();
});
test('revoked-session antiforgery denial conceals private facts while retaining an uncertain original reference', async () => {
  const denied = new AccessFailure(403, 'antiforgery_failed');
  const s = setup({ submit: async () => { throw new TypeError('lost response'); }, receipt: async () => { throw denied; } });
  await ready(s.editor); await s.editor.confirm(true); const entry = s.journal.list(actorId)[0];
  assert.ok(s.editor.snapshot().choice); assert.ok(s.editor.snapshot().attempt);
  await assert.rejects(s.editor.receipt(entry), error => error === denied); s.editor.recoveryFailed(denied);
  assert.equal(s.editor.snapshot().suspended, true); assert.equal(s.editor.snapshot().warehouses.length, 0);
  assert.equal(s.editor.snapshot().choice, null); assert.equal(s.editor.snapshot().preview, null); assert.equal(s.editor.snapshot().review, null);
  assert.equal(s.editor.snapshot().attempt, null); assert.equal(s.editor.locked, true);
  assert.deepEqual(s.journal.list(actorId), [entry]); assert.equal(s.editor.snapshot().references[0].key, entry.key);
  s.editor.changePackages('1'); assert.equal(s.editor.snapshot().packages, ''); s.editor.dispose();
});
