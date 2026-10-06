import { test } from 'node:test';
import assert from 'node:assert/strict';
import { StockSourceActivationEditor, handoffFromDraft } from '../src/modules/inventory/stock-source-activation-editor-state';
import type { StockSourceActivationGateway } from '../src/modules/inventory/stock-source-activation-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { AccessFailure } from '../src/modules/administration/workflow';
import { parseActivationPreview } from '../src/modules/inventory/stock-source-activation-workflow';
import { SourceMemoryStorage as MemoryStorage, sourceSession } from './stock-source-fixtures';
import { preview, pool, proposedResult, approvedResult } from './stock-source-activation-fixtures';

function setup(actions = ['stock.source.read', 'stock.source.propose', 'stock.source.approve'], override: Partial<StockSourceActivationGateway> = {}) {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage, () => new Date('2026-09-29T00:01:00.000Z')); let now = 0, sends = 0;
  const gateway: StockSourceActivationGateway = { preview: async () => parseActivationPreview(preview), history: async () => ({ total: '0', items: [] }),
    detail: async () => null, review: async () => { throw new Error('unused'); }, submit: async () => { sends++; return proposedResult as never; }, receipt: async () => null, ...override };
  const editor = new StockSourceActivationEditor({ actorId: sourceSession.account!.userId, actions, session: sourceSession, observedAt: 0, now: () => now,
    journal, gateway, newKey: () => 'activation-key' });
  return { editor, journal, storage, sends: () => sends, advance: (value: number) => { now = value; } };
}
test('operator facts start blank/unchecked; absence of explicit zeros or confirmations cannot create a reviewed write', async () => {
  const { editor, sends } = setup(); await editor.start(); editor.changePool(pool); await editor.loadPool();
  const draft = editor.snapshot().draft;
  assert.equal(draft.physicalPackages, ''); assert.equal(draft.carriedValueMinorUnits, ''); assert.equal(draft.emptyPoolConfirmed, false);
  assert.equal(draft.previousWriterStopped, false); assert.equal(draft.noUnrecordedStock, false);
  editor.changeReason('Pemeriksaan'); editor.reviewProposal(); assert.equal(editor.snapshot().review, null); assert.equal(sends(), 0); editor.dispose();
  assert.throws(() => handoffFromDraft({ ...draft, observedAtLocal: '2026-02-30T08:00:00' }), AccessFailure);
});
test('approve-only user does not receive a proposer preview or a read-grant history; expired views conceal all private data', async () => {
  let previews = 0, histories = 0; const fixture = setup(['stock.source.approve'], { preview: async () => { previews++; return parseActivationPreview(preview); },
    history: async () => { histories++; return { total: '0', items: [] }; } });
  await fixture.editor.start(); fixture.editor.changePool(pool); await fixture.editor.loadPool();
  assert.equal(previews, 0); assert.equal(histories, 0); fixture.advance(24 * 60 * 60 * 1000); fixture.editor.checkSession();
  assert.equal(fixture.editor.snapshot().expired, true); assert.equal(fixture.editor.snapshot().poolId, ''); assert.equal(fixture.editor.snapshot().preview, null); fixture.editor.dispose();
});
// Requires the real shared journal's activation registration during integration.
test('unknown committed-response attempt retains original metadata and prevents any second submit', async () => {
  const fixture = setup(undefined, { submit: async () => { throw new AccessFailure(503, 'commit_outcome_unknown'); } });
  await fixture.editor.start(); fixture.editor.changePool(pool); await fixture.editor.loadPool();
  const observed = new Date('2026-09-29T00:01:00.000Z'), pad = (n: number) => String(n).padStart(2, '0');
  const local = observed.getFullYear() + '-' + pad(observed.getMonth() + 1) + '-' + pad(observed.getDate()) + 'T' + pad(observed.getHours()) + ':' + pad(observed.getMinutes()) + ':00';
  fixture.editor.changeDraft({ reference: preview.requiredHandoffReference, observedAtLocal: local, physicalPackages: '0', carriedValueMinorUnits: '0',
    emptyPoolConfirmed: true, previousWriterStopped: true, noUnrecordedStock: true }); fixture.editor.changeReason('Serah terima'); fixture.editor.reviewProposal();
  assert.ok(fixture.editor.snapshot().review); await fixture.editor.submitReviewed(true);
  assert.ok(fixture.editor.snapshot().attempt); assert.equal(fixture.journal.list(sourceSession.account!.userId).length, 1);
  const saved = Array.from(fixture.storage.values.values()).join(''); assert.equal(saved.includes(preview.requiredHandoffReference), false); assert.equal(saved.includes('emptyPoolConfirmed'), false);
  fixture.editor.changeReason('Tidak boleh berubah'); fixture.editor.reviewProposal(); assert.equal(fixture.editor.snapshot().review, null);
  await fixture.editor.submitReviewed(true); assert.equal(fixture.journal.list(sourceSession.account!.userId).length, 1); fixture.editor.dispose();
});
test('activation recovery retains unresolved or stale references and confirms only the original operation without closing or resending', async () => {
  const { journal } = setup();
  const entry = journal.remember(sourceSession.account!.userId, 'stock.source.activation.propose', 'original-activation');
  const legacy = { lookup: async () => { throw new Error('No legacy lookup'); }, close: async () => { throw new Error('No manual close'); } };
  const adapter = (result: typeof proposedResult | typeof approvedResult | null) => ({ 'stock.source.activation': {
    lookup: async (reference: typeof entry) => { assert.deepEqual(reference, entry); return result === null ? null : { state: 'committed' as const, result: result as never }; }
  } });
  assert.deepEqual(operationRecoveryCapabilities(entry.operation, adapter(null)), { canCheck: true, canClose: false, unavailable: null });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapter(null)), { state: 'unconfirmed' });
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, adapter(proposedResult), 'Close'), /recovery_close_unsupported/);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, adapter(approvedResult)));
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, adapter(proposedResult), undefined, () => false), /recovery_request_stale/);
  assert.equal(journal.list(entry.actorId).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapter(proposedResult)), {
    state: 'committed', result: { id: proposedResult.proposalId, version: '1' }
  });
  assert.equal(journal.list(entry.actorId).length, 0);
  assert.equal(recoveryPage(entry.operation).href, '/inventory/sources/activations');
});
