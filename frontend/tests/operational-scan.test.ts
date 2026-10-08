import test from 'node:test';
import assert from 'node:assert/strict';
import {createScanGateway, parseScanResolution, sameScanPackage, validateScanSelections, type ScanContext} from '../src/modules/inventory/scan-client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';

const context: ScanContext = {purpose: 'picking', location: null, documentId: '9007199254741001', documentRevision: '2', auxiliaryId: null, auxiliaryRevision: null, sourceReview: 'a'.repeat(64)};
const proof = {identifier: {identifierId: '101', identifierRevision: '2', skuId: '31', definitionId: '32', definitionVersion: '1',
  kind: 'skuCode' as const, namespace: 'internal', canonicalValue: 'PENCIL-12'}, source: null};
const item = {targetReference: '31', skuId: '31', definitionId: '32', definitionVersion: '1', skuCode: 'PENCIL-12', packageLabel: 'Dus isi 12', packageKind: 'sealed' as const,
  baseUnitCode: 'piece', baseUnitsPerPackage: 12, selection: proof, currentPrimaryCode: 'PENCIL-CORRECTED'};
const result = (kind = 'match', candidates = [item]) => ({kind, reason: 'exact_code', purpose: context.purpose, documentId: context.documentId,
  documentRevision: context.documentRevision, auxiliaryId: null, auxiliaryRevision: null, sourceReview: context.sourceReview, candidates});
test('only exactly one explicitly matched candidate can be confirmed; malformed or wrong-context responses fail closed', () => {
  assert.equal(parseScanResolution(result(), context).candidates[0].baseUnitsPerPackage, 12);
  for (const invalid of [result('match', []), result('match', [item, {...item, targetReference: '41'}]), result('unknown'),
    result('ambiguous'), {...result(), documentId: '21'}, {...result(), documentRevision: '3'}, {...result(), sourceReview: 'b'.repeat(64)}])
    assert.throws(() => parseScanResolution(invalid, context), AccessFailure);
  assert.equal(parseScanResolution(result('unknown', []), context).kind, 'unknown');
  assert.throws(() => parseScanResolution(result('match', [{...item, selection: {...proof, identifier: {...proof.identifier, definitionId: '99'}}}]), context), AccessFailure);
});
test('reviewed scan references remain detached and bound to the actual selected package; manual keeps its original shape', () => {
  assert.equal(validateScanSelections(undefined, [item]), undefined);
  assert.equal(validateScanSelections([], [item]), undefined);
  const selected = validateScanSelections([proof], [item])!;
  assert.notEqual(selected[0], proof); assert.notEqual(selected[0].identifier, proof.identifier);
  assert.ok(Object.isFrozen(selected)); assert.ok(Object.isFrozen(selected[0].identifier));
  assert.throws(() => validateScanSelections([proof, proof], [item]), RequestNotSentError);
  assert.throws(() => validateScanSelections([proof], [{...item, definitionVersion: '2'}]), RequestNotSentError);
});
test('a scan cannot treat same label as same sealed or loose definition', () => {
  assert.equal(sameScanPackage(item, item), true);
  for (const changed of [{definitionId: '99'}, {packageKind: 'loose'}, {baseUnitsPerPackage: 1}, {skuId: '42'}, {definitionVersion: '2'}])
    assert.equal(sameScanPackage(item, {...item, ...changed}), false);
});
test('changed context while obtaining CSRF prevents request; resolver calls no command endpoint', async () => {
  let live = true, posts = 0;
  const stale = createScanGateway(async () => {live = false; return new Response(JSON.stringify({requestToken: 'fixture'}));}, 'http://localhost/api/v1/identity/csrf');
  await assert.rejects(stale.resolve('10', context, {codeKind: 'sku', rawCode: 'PENCIL-12', sourceNamespace: null, sourceAccount: null}, () => live), RequestNotSentError);
  const gateway = createScanGateway(async (input, options) => {
    if (input instanceof Request) return new Response(JSON.stringify({requestToken: 'fixture'})); posts++;
    assert.equal(input, '/api/v1/catalog/scans/query'); assert.equal(new Headers(options?.headers).has('Idempotency-Key'), false);
    const body = JSON.parse(String(options?.body)); assert.equal(body.rawCode, 'PENCIL-12'); assert.equal(body.documentId, context.documentId);
    assert.equal(Object.hasOwn(body, 'packages'), false); return new Response(JSON.stringify(result()));
  }, 'http://localhost/api/v1/identity/csrf');
  await gateway.resolve('10', context, {codeKind: 'sku', rawCode: 'PENCIL-12', sourceNamespace: null, sourceAccount: null}, () => true); assert.equal(posts, 1);
});
