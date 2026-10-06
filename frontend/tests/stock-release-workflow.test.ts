import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AccessFailure } from '../src/modules/administration/workflow';
import { parseReleasePreview, parseReleaseReview, parseReleaseSources, parseReleaseHistory, parseReleaseResult, prepareReleaseInput, prepareReleaseProposal,
  releaseReceipt, releaseProposeOperation, releaseDecideOperation } from '../src/modules/inventory/stock-release-workflow';
import { actorId, location, choice, preview, detail, review, before, after, proposed, approved, expected, selection, input } from './stock-release-fixtures';

test('release picks exact real selection and packaging quantity without numeric IDs or inferred transfer estimates', () => {
  assert.equal(prepareReleaseInput(location, choice, '2').receiptId, choice.receiptId);
  for (const quantity of ['0', '01', '1.5', '4', '1000001']) assert.throws(() => prepareReleaseInput(location, choice, quantity), AccessFailure);
  assert.equal(parseReleasePreview(preview).expected.balanceRevision, '9007199254740993');
  for (const changed of [{ source: {} }, { input: { ...input, receiptId: Number(choice.receiptId) } }, { stockAfter: { ...after, heldPackages: '2' } },
    { stockAfter: { ...after, onHandPackages: '4', availablePackages: '3' } }, { selection: { ...selection, capitalizedValue: 0.03 } }])
    assert.throws(() => parseReleasePreview({ ...preview, ...changed }), AccessFailure);
  const command = prepareReleaseProposal(actorId, preview, 'Periksa jumlah dan lot'); assert.ok(Object.isFrozen(command.body)); assert.equal(command.revision, expected.balanceRevision);
});
test('independent review uses fresh stock facts while retaining exact original proposal', () => {
  const freshBefore = { ...before, revision: '9007199254740998', heldPackages: '2', availablePackages: '1' };
  const freshAfter = { ...freshBefore, revision: '9007199254740999', heldPackages: '0', availablePackages: '3' };
  const fresh = { ...review, expected: { ...expected, balanceRevision: freshBefore.revision, poolFence: '20', heldValueRevision: '1', lotValueRevision: '1' }, stockBefore: freshBefore, stockAfter: freshAfter };
  assert.equal(parseReleaseReview(fresh, detail, 'approve').stockBefore.availablePackages, '1');
  assert.throws(() => parseReleaseReview({ ...fresh, expected }, detail, 'approve'), AccessFailure);
  assert.throws(() => parseReleaseReview(fresh, { ...detail, status: 'approved' }, 'approve'), AccessFailure);
  assert.equal(parseReleaseReview({ ...review, action: 'reject', stockAfter: before }, detail, 'reject').stockAfter.heldPackages, before.heldPackages);
});
test('clean acquisition value and approved quality subset remain independent finite entitlements', () => {
  const subset = { ...choice, acceptedPackages: '1', valuedPackages: '3', releaseablePackages: '1' };
  assert.equal(parseReleaseSources({ items: [subset], unavailableCount: '0' }).items[0].valuedPackages, '3');
  const selected = prepareReleaseInput(location, subset, '1');
  assert.equal(parseReleasePreview({ ...preview, input: selected, selection: { ...selection, acceptedPackages: '1' },
    stockAfter: { ...after, heldPackages: '2', availablePackages: '1' } }).input.packages, 1);
  assert.throws(() => prepareReleaseInput(location, subset, '2'), AccessFailure);
  assert.throws(() => parseReleasePreview({ ...preview, selection: { ...selection, acceptedPackages: '1' } }), AccessFailure);
});
test('strict public choices/history reject private fields, duplicate source edges and inconsistent chronology', () => {
  assert.equal(parseReleaseSources({ items: [choice], unavailableCount: '0' }).items.length, 1);
  for (const items of [[choice, choice], [{ ...choice, people: [] }], [{ ...choice, releaseablePackages: '4' }]])
    assert.throws(() => parseReleaseSources({ items, unavailableCount: '0' }), AccessFailure);
  const item = { proposalId: detail.proposal.id, status: 'proposed', receiptReference: choice.receiptReference, productName: choice.productName,
    packagingLabel: choice.packagingLabel, lotReference: choice.lotReference, packages: '2', policy: choice.policy, recordedAt: detail.proposal.proposedAt, decidedAt: null };
  assert.equal(parseReleaseHistory({ items: [item], nextAfter: null }).items[0].proposalId, item.proposalId);
  assert.throws(() => parseReleaseHistory({ items: [{ ...item, status: 'approved' }], nextAfter: null }), AccessFailure);
});
test('only committed complete owner effects confirm approval; unknown receipt stays unknown', () => {
  assert.equal(parseReleaseResult(proposed, releaseProposeOperation).status, 'proposed');
  assert.equal(parseReleaseResult(approved, releaseDecideOperation).transferredValue, '0.02');
  for (const result of [{ ...approved, valueTransitionId: null }, { ...approved, transferredValue: null }, { ...proposed, movementId: '8' }, { ...approved, actorId }])
    assert.throws(() => parseReleaseResult(result), AccessFailure);
  assert.equal(releaseReceipt({ state: 'not_confirmed', result: null }, releaseProposeOperation), null);
  assert.throws(() => releaseReceipt({ state: 'not_confirmed', result: proposed }, releaseProposeOperation), AccessFailure);
});
