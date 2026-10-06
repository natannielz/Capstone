import { test } from 'node:test';
import assert from 'node:assert/strict';
import { preparePackInput, parsePackPreview, parsePackProposal, parsePackRule, parsePackRules, parsePackProposals, parsePackPackages,
  preparePackProposal, preparePackDecision, parsePackDecisionPreview, packOutcome, packReceipt, packProposeOperation, packDecideOperation } from '../src/modules/inventory/pack-conversion-workflow';
import { parsePackWarehouses, packSearch } from '../src/modules/inventory/pack-conversion-workflow';
import { actorId, location, warehouse, source, destination, input, preview, proposal, reviewPreview, proposeResult, approveResult, rule } from './pack-conversion-fixtures';

// Focused synthetic contract cases, UNEXECUTED draft. No provider, stock or physical-conversion acceptance.
test('warehouse and package projections reject extra private fields and enforce the exact search contract', () => {
  assert.deepEqual(parsePackWarehouses({ items: [warehouse] }), [warehouse]);
  assert.throws(() => parsePackWarehouses({ items: [warehouse], actorId }));
  assert.throws(() => parsePackWarehouses({ items: [{ ...warehouse, people: [] }] }));
  assert.throws(() => parsePackWarehouses({ items: [{ ...warehouse, location: { ...location, privateProof: 'hidden' } }] }));
  assert.throws(() => parsePackPackages({ total: '1', items: [{ ...source, actorId }] }, 'source'));
  assert.equal(packSearch('  pena  '), 'pena'); assert.equal(packSearch(''), '');
  assert.equal(packSearch('a'.repeat(100)).length, 100); assert.throws(() => packSearch('a'.repeat(101)));
});
test('deliberate sealed-to-loose input conserves units and pins lossless catalog revisions in frozen server preview', () => {
  assert.deepEqual(input, preparePackInput(location, source, destination, '1', '12'));
  const command = preparePackProposal(actorId, preview, 'Siapkan aturan dus ke satuan');
  assert.equal(command.body.input.sourceSkuId, source.skuId); assert.equal(command.body.input.destinationSkuRevision, destination.skuRevision);
  assert.ok(Object.isFrozen(command)); assert.ok(Object.isFrozen(command.body.input)); assert.ok(Object.isFrozen(command.preview.source));
  assert.deepEqual(packOutcome(proposeResult, command), proposeResult);
  for (const ratio of ['', '0', '01', '1.0', '-1', '1000001', '1\n']) assert.throws(() => preparePackInput(location, source, destination, ratio, '12'));
  assert.throws(() => preparePackInput(location, source, destination, '1', '11'));
  assert.throws(() => preparePackInput(location, destination, source, '12', '1'));
});
test('malformed quantities, wrong catalog identity, binding and claimed physical execution cannot be accepted', () => {
  for (const changed of [{ physicalConversionAvailable: true }, { sourcePackages: '2' }, { baseUnits: '13' }, { binding: 'A'.repeat(64) },
    { binding: 'a'.repeat(64) + '\n' }, { source: { ...source, skuId: '5' } }, { source: { ...source, skuRevision: 9007199254740999 } },
    { destination: { ...destination, packageKind: 'sealed' } }, { input: { ...input, location: { ...location, warehouseId: '2' } } }])
    assert.throws(() => parsePackPreview({ ...preview, ...changed }, input));
  assert.throws(() => parsePackPackages({ total: '1', items: [destination] }, 'source'));
  assert.throws(() => parsePackPackages({ total: '2', items: [source, source] }, 'source'));
});
test('independent-review capability comes only from current server detail and decision preview must match its immutable proposal', () => {
  assert.throws(() => parsePackProposal({ ...proposal, canReview: undefined }));
  const missing: Record<string, unknown> = { ...proposal }; delete missing.canReview; assert.throws(() => parsePackProposal(missing));
  assert.equal(parsePackProposal({ ...proposal, canReview: false }).canReview, false);
  assert.throws(() => preparePackDecision(actorId, { ...reviewPreview, proposal: { ...proposal, canReview: false } }, 'Disetujui'));
  assert.throws(() => parsePackDecisionPreview({ ...reviewPreview, proposal: { ...proposal, reason: 'Changed' } }, proposal, 'approve'));
  const command = preparePackDecision(actorId, reviewPreview, 'Definisi dan rasio telah diperiksa');
  assert.ok(Object.isFrozen(command.preview.proposal.input)); assert.equal(command.expectedRevision, '1'); assert.deepEqual(packOutcome(approveResult, command), approveResult);
  assert.throws(() => packOutcome({ ...approveResult, state: 'reject', ruleId: null }, command));
  assert.throws(() => packOutcome({ ...approveResult, proposalId: '8', ruleId: '8' }, command));
});
test('history and approved rules are scoped and strictly descending; stored approval never admits physical conversion', () => {
  assert.deepEqual(parsePackProposals({ items: [proposal], nextProposalId: null, physicalConversionAvailable: false }, location).items, [proposal]);
  assert.deepEqual(parsePackRules({ items: [rule], nextRuleId: null, physicalConversionAvailable: false }, location).items, [parsePackRule(rule)]);
  assert.throws(() => parsePackProposals({ items: [proposal], nextProposalId: null, physicalConversionAvailable: false }, { ...location, channelId: '4' }));
  assert.throws(() => parsePackProposals({ items: [proposal, proposal], nextProposalId: null, physicalConversionAvailable: false }, location));
  assert.throws(() => parsePackRules({ items: [rule], nextRuleId: proposal.id, physicalConversionAvailable: false }, location));
  assert.throws(() => parsePackRule({ ...rule, id: '2' })); assert.throws(() => parsePackRule({ ...rule, physicalConversionAvailable: true }));
  assert.throws(() => packReceipt({ state: 'committed', result: approveResult }, packProposeOperation));
  assert.deepEqual(packReceipt({ state: 'committed', result: approveResult }, packDecideOperation), { state: 'committed', result: approveResult });
});
