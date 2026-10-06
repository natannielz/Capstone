import { test } from 'node:test';
import assert from 'node:assert/strict';
import { activationHandoff, activationExpected, parseActivationPreview, parseActivationDetail, parseActivationReview, prepareActivationProposal,
  prepareActivationDecision, activationOutcome, activationReceipt } from '../src/modules/inventory/stock-source-activation-workflow';
import { AccessFailure } from '../src/modules/administration/workflow';
import { pool, handoff, expected, preview, proposal, review, proposedResult, approvedResult } from './stock-source-activation-fixtures';

test('explicit handoff requires canonical UTC milliseconds, string 0/0 and all operator confirmations', () => {
  assert.equal(activationHandoff(handoff).observedAt, handoff.observedAt);
  for (const change of [{ physicalPackages: 0 }, { physicalPackages: '00' }, { carriedValueMinorUnits: '1' }, { emptyPoolConfirmed: false },
    { previousWriterStopped: false }, { noUnrecordedStock: false }, { observedAt: '2026-09-29T00:01:00Z' },
    { observedAt: '2026-02-30T00:01:00.000Z' }, { observedAt: '2026-09-29T00:01:00.000+00:00' }, { actorId: '1' }])
    assert.throws(() => activationHandoff({ ...handoff, ...change }), AccessFailure);
  assert.equal(activationExpected(expected).poolRevision, '9007199254740995');
  assert.throws(() => activationExpected({ ...expected, poolRevision: 9007199254740995 }), AccessFailure);
  assert.throws(() => activationExpected({ ...expected, poolFence: '9223372036854775805' }), AccessFailure);
});
test('preview refuses missing/duplicate/unknown domains, open work, incomplete coverage and private projection leakage', () => {
  assert.equal(parseActivationPreview(preview).obligations.length, 7);
  for (const obligations of [preview.obligations.slice(1), [...preview.obligations.slice(1), preview.obligations[1]],
    preview.obligations.map((item, index) => index ? item : { ...item, domain: 'Other' }),
    preview.obligations.map((item, index) => index ? item : { ...item, complete: false }),
    preview.obligations.map((item, index) => index ? item : { ...item, openCount: '1' })])
    assert.throws(() => parseActivationPreview({ ...preview, obligations }), AccessFailure);
  assert.throws(() => parseActivationPreview({ ...preview, actorGrantVersion: '1' }), AccessFailure);
  assert.throws(() => parseActivationPreview({ ...preview, requiredHandoffReference: 'Other' }), AccessFailure);
});
test('human review binds source/proposal/current policy and independent handoff facts; command snapshots are immutable', () => {
  const command = prepareActivationProposal('1', preview, handoff, ' Pemeriksaan saya ');
  assert.equal(command.poolId, pool); assert.equal(command.body.reason, 'Pemeriksaan saya'); assert.ok(Object.isFrozen(command.body.handoff));
  assert.equal(activationOutcome(proposedResult, command).proposalId, proposal.id);
  assert.throws(() => activationOutcome(approvedResult, command), AccessFailure);
  const decision = prepareActivationDecision('2', review, 'Tinjauan berbeda');
  assert.equal(activationOutcome(approvedResult, decision).status, 'activated');
  for (const change of [{ physicalPoolId: '8' }, { proposalId: '8' }, { expected: { ...review.expected, policyId: '8' } },
    { handoff: { ...handoff, observedAt: '2026-09-29T00:00:59.000Z' } }, { action: 'reject' }])
    assert.throws(() => parseActivationReview({ ...review, ...change }), AccessFailure);
  assert.equal(parseActivationDetail(proposal).status, 'proposed');
  assert.throws(() => parseActivationDetail({ ...proposal, status: 'activated' }), AccessFailure);
  assert.equal(activationReceipt({ state: 'not_confirmed', result: null }, command.operation), null);
  assert.throws(() => activationReceipt({ state: 'committed', result: approvedResult }, command.operation), AccessFailure);
});
