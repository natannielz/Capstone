import assert from 'node:assert/strict';
import test from 'node:test';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSourceReconciliationGateway, parseSourceReconciliationBaseline, parseSourceReconciliationRead, parseSourceReconciliationResult, parseReconciliationDecisionPreview, prepareSourceReconciliation, prepareSourceReconciliationDecision, reconciliationDifferences, reconciliationObservationCurrent, reconciliationObservationTime, reconciliationKeyOwner, sourceReconciliationObservation } from '../src/modules/inventory/stock-source-reconciliation-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';
import { sourcePurpose } from '../src/modules/inventory/stock-source-workflow';

const actor = '740', checker = '741', poolId = '9007199254741001', proposalId = '9007199254741002', balanceId = '9007199254741003';
const location = { kind: 'fulfillmentWarehouse' as const, organizationId: '100', branchId: null, divisionId: null, shopId: '101', warehouseId: '102', channelId: '103' };
const row = { balanceId, receiptId: '810', lotId: '820', location, skuId: '830', skuCode: 'PENCIL-12', definitionId: '840', definitionVersion: '1', packageLabel: 'Kotak 12', packageKind: 'sealed' as const, baseUnitCode: 'PCS', baseUnitsPerPackage: 12, lotReference: '000LOT', expiresOn: null, onHand: '2', reserved: '0', held: '2', expired: '0', version: '1' };
const baseline = { poolId, poolRevision: '2', poolFence: '7', poolLabel: 'Sumber gudang utama', physicalKey: 'reviewed-stock', ownerLocation: location, policyId: '850', policyRevision: '1', reconciliationProcedureReference: 'COUNT-001', configurationHash: 'a'.repeat(64), maximumAgeMilliseconds: '900000', latestSourceAt: '2026-10-03T01:00:00Z', balances: [row], capturedAt: '2026-10-03T01:00:01Z', baselineHash: 'b'.repeat(64), binding: 'c'.repeat(64) };
const observed = { observedAt: '2026-10-03T01:00:01Z', sourceReference: '000MAKER', entries: [{ balanceId, packages: '2' }] };
const independentlyObserved = { ...observed, observedAt: '2026-10-03T01:00:03Z', sourceReference: '000CHECKER' };
const proposal = { id: proposalId, version: '1' as const, poolId, baseline, observation: observed, differences: [], recordedBy: actor, recordedAt: '2026-10-03T01:00:02Z', reason: 'Pengamatan lengkap seluruh lot', status: 'proposed' as const };
const original = { proposal, decision: null };
const currentBaseline = { ...baseline, poolFence: '8', capturedAt: '2026-10-03T01:00:04Z', binding: 'd'.repeat(64) };
const preview = { proposal, decision: 'confirm' as const, baseline: currentBaseline, checkerDifferences: [], canConfirm: true, binding: 'e'.repeat(64), observedAt: '2026-10-03T01:00:04Z' };
const decision = { id: '860', decision: 'confirm' as const, observation: independentlyObserved, recordedBy: checker, recordedAt: '2026-10-03T01:00:05Z', reason: 'Seluruh jumlah diperiksa sendiri' };
const proposed = { proposalId, decisionId: null, poolId, revision: '1' as const, status: 'proposed' as const, recordedAt: proposal.recordedAt };
const decided = { proposalId, decisionId: decision.id, poolId, revision: '1' as const, status: 'reconciled' as const, recordedAt: decision.recordedAt };
const proposeOp = 'stock.source.reconciliation.propose', decideOp = 'stock.source.reconciliation.decide';
const proposeKey = `recon.propose.${poolId}.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`, decideKey = `recon.confirm.${proposalId}.bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`;
const now = Date.parse(preview.observedAt);
const command = () => prepareSourceReconciliation(actor, baseline, observed, proposal.reason, now);
const decisionCommand = () => prepareSourceReconciliationDecision(checker, original, preview, independentlyObserved, decision.reason, now);

test('complete canonical baseline keeps exact package counts and rejects wrong scope, overflow and hidden fields', () => {
  assert.equal(parseSourceReconciliationBaseline(baseline).balances[0].balanceId, balanceId);
  const huge = { ...baseline, balances: [{ ...row, onHand: '9007199254740993', held: '9007199254740993' }] };
  assert.equal(parseSourceReconciliationBaseline(huge).balances[0].held, '9007199254740993');
  for (const value of [{ ...baseline, poolId: Number(poolId) }, { ...baseline, balances: [row, row] }, { ...baseline, balances: [{ ...row, onHand: '9223372036854775808' }] }, { ...baseline, balances: [{ ...row, onHand: '02' }] }, { ...baseline, balances: [{ ...row, held: '3' }] }, { ...baseline, balances: [{ ...row, location: { ...location, warehouseId: '999' } }] }, { ...baseline, balances: [{ ...row, baseUnitsPerPackage: '12' }] }, { ...baseline, secret: 'private' }, { ...baseline, capturedAt: '2026-02-30T00:00:00Z' }]) assert.throws(() => parseSourceReconciliationBaseline(value), AccessFailure);
});

test('maker observations cover every real balance, preserve explicit zero and show mismatch without pretending it reconciles', () => {
  assert.ok(Object.isFrozen(command().body.input.observation));
  const mismatch = { ...observed, entries: [{ balanceId, packages: '0' }] };
  assert.equal(prepareSourceReconciliation(actor, baseline, mismatch, 'Perlu tindak lanjut', now).body.input.observation.entries[0].packages, '0');
  assert.deepEqual(reconciliationDifferences(baseline, mismatch), [{ balanceId, baselinePackages: '2', observedPackages: '0' }]);
  for (const value of [{ ...observed, entries: [] }, { ...observed, entries: [{ balanceId: '999', packages: '2' }] }, { ...observed, entries: [{ balanceId, packages: '' }] }, { ...observed, entries: [{ balanceId, packages: 2 }] }, { ...observed, entries: [{ balanceId, packages: '-1' }] }, { ...observed, entries: [{ balanceId, packages: '0.0' }] }, { ...observed, sourceReference: ' text ' }, { ...observed, sourceReference: 'a\nb' }]) assert.throws(() => prepareSourceReconciliation(actor, baseline, sourceReconciliationObservation(value), 'reason', now));
  assert.equal(sourceReconciliationObservation({ ...observed, sourceReference: 'Exact\u200dtext' }).sourceReference, 'Exact\u200dtext');
  assert.equal(sourceReconciliationObservation({ ...observed, sourceReference: '\uFEFFExact' }).sourceReference, '\uFEFFExact');
});

test('explicit WIB time is timezone-independent; future, pre-source and stale observations are not renewed', () => {
  assert.equal(reconciliationObservationTime('2026-10-03T08:00:01'), '2026-10-03T01:00:01.000Z');
  for (const value of ['', '2026-02-30T08:00', '2026-10-03T25:00', '2026-10-03T08:00Z']) assert.throws(() => reconciliationObservationTime(value), RequestNotSentError);
  for (const at of ['2026-10-03T00:59:59Z', '2026-10-03T01:00:05Z']) assert.equal(reconciliationObservationCurrent(baseline, { ...observed, observedAt: at }, now), false);
  assert.equal(reconciliationObservationCurrent(baseline, observed, Date.parse(observed.observedAt) + 900000), false);
  assert.throws(() => prepareSourceReconciliation(actor, baseline, observed, 'reason', Date.parse(observed.observedAt) + 900000));
});

test('independent review validates both counts and current binding; mismatch and expired maker remain visibly not confirmable', () => {
  assert.equal(decisionCommand().body.observation?.sourceReference, independentlyObserved.sourceReference);
  const mismatch = { ...independentlyObserved, entries: [{ balanceId, packages: '1' }] }, differing = { ...preview, canConfirm: false, checkerDifferences: [{ balanceId, baselinePackages: '2', observedPackages: '1' }] };
  assert.equal(parseReconciliationDecisionPreview(differing, original, 'confirm', mismatch).canConfirm, false);
  assert.throws(() => prepareSourceReconciliationDecision(checker, original, differing, mismatch, 'reason', now));
  assert.throws(() => parseReconciliationDecisionPreview({ ...differing, canConfirm: true }, original, 'confirm', mismatch));
  const later = Date.parse(observed.observedAt) + 900000, laterObserved = { ...independentlyObserved, observedAt: new Date(later).toISOString() }, expired = { ...preview, observedAt: laterObserved.observedAt, baseline: { ...currentBaseline, capturedAt: laterObserved.observedAt }, canConfirm: false };
  assert.equal(parseReconciliationDecisionPreview(expired, original, 'confirm', laterObserved).canConfirm, false);
  assert.throws(() => prepareSourceReconciliationDecision(checker, original, expired, laterObserved, 'fresh checker cannot renew maker', later));
  assert.throws(() => prepareSourceReconciliationDecision(actor, original, preview, independentlyObserved, 'same account', now));
  assert.throws(() => parseReconciliationDecisionPreview({ ...preview, baseline: { ...currentBaseline, poolFence: '10' } }, original, 'confirm', independentlyObserved));
});

test('saved decision and recovery require exact actor, outcome, source and original reference without pretending stock moved', () => {
  assert.equal(parseSourceReconciliationRead({ proposal, decision }).decision?.decision, 'confirm');
  for (const value of [{ proposal, decision: { ...decision, recordedBy: actor } }, { proposal, decision: { ...decision, observation: null } }, { proposal, decision: { ...decision, observation: { ...independentlyObserved, entries: [{ balanceId, packages: '1' }] } } }, { proposal: { ...proposal, differences: [{ balanceId, baselinePackages: '2', observedPackages: '1' }] }, decision }]) assert.throws(() => parseSourceReconciliationRead(value));
  const rejectionPreview = { ...preview, decision: 'reject' as const, baseline: null, checkerDifferences: [], canConfirm: false };
  assert.equal(prepareSourceReconciliationDecision(checker, original, rejectionPreview, null, 'Selisih perlu pengamatan baru', now).body.observation, null);
  assert.throws(() => parseSourceReconciliationResult({ ...decided, status: 'rejected' }, decideKey, decideOp));
  assert.throws(() => reconciliationKeyOwner(decideKey + '\n', decideOp));
});

test('decision transport binds expected actor, CSRF, frozen If-Match and original key; stale review never dispatches', async () => {
  let calls = 0;
  const gateway = createSourceReconciliationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(new URL(request.url).pathname, `/api/v1/inventory/source-reconciliations/proposals/${proposalId}/decide`);
    assert.equal(request.headers.get('X-Expected-Actor-Id'), checker); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"1"'); assert.equal(request.headers.get('Idempotency-Key'), decideKey); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error'); assert.deepEqual(await request.json(), decisionCommand().body);
    return Response.json(decided);
  } }));
  await assert.rejects(gateway.decide(decisionCommand(), decideKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.equal((await gateway.decide(decisionCommand(), decideKey, () => true)).status, 'reconciled');
  let active = 0; await assert.rejects(gateway.decide(decisionCommand(), decideKey, () => ++active === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 2);
});

test('lost proposal/decision results use original actor and key lookup plus exact owner read without resending', async () => {
  for (const [who, key, operation, body, outcome, actual] of [[actor, proposeKey, proposeOp, command().body, proposed, original], [checker, decideKey, decideOp, decisionCommand().body, decided, { proposal, decision }]] as const) {
    const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key); let attempts = 0, wrong = true, committed = true;
    await assert.rejects(submitTrackedRequest(ledger, journal, who, 'reconciliation', operation, body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
    const entry = journal.list(who)[0]; assert.ok(entry); assert.equal(journal.list('999').length, 0);
    assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    const gateway = createSourceReconciliationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
      assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.headers.get('X-Expected-Actor-Id'), who);
      if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.json(), { purpose: 'operation-recovery', operation, key }); return Response.json({ state: committed ? 'committed' : 'not_confirmed', result: committed ? outcome : null }); }
      return Response.json(wrong ? { ...actual, proposal: { ...proposal, id: '999' } } : actual);
    } }));
    await assert.rejects(gateway.recover(who, operation, key)); committed = false; assert.equal(await gateway.recover(who, operation, key), null); assert.equal(journal.list(who).length, 1); committed = true; wrong = false;
    const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
    assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'stock.source.reconciliation': { lookup: item => gateway.recover(item.actorId, item.operation, item.key) } }), { state: 'committed', result: { id: proposalId, version: '1' } }); assert.equal(journal.list(who).length, 0); assert.equal(attempts, 1);
  }
});

test('named source discovery and baseline reads preserve scope and do not dispatch writes', async () => {
  assert.equal(sourcePurpose('stock-source-reconciliation-review'), 'stock-source-reconciliation-review');
  for (const invalid of ['stock-source-reconciliation-review ', 'stock-source-reconciliation-approve', '*']) assert.throws(() => sourcePurpose(invalid));
  let wrong = false; const warehouse = { location, organizationName: 'Koperasi', shopName: 'Toko', warehouseName: 'Gudang', channelName: 'Ritel' };
  const gateway = createSourceReconciliationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    if (request.url.endsWith('/warehouses/query')) { assert.deepEqual(await request.json(), { purpose: 'stock-source-reconciliation-review' }); return Response.json({ items: [warehouse] }); }
    if (request.url.endsWith('/targets/query')) { assert.deepEqual(await request.json(), { purpose: 'stock-source-reconciliation-review', location, offset: 0, limit: 25 }); return Response.json({ total: '1', items: [{ poolId, label: baseline.poolLabel, physicalKey: baseline.physicalKey, ownerLocation: wrong ? { ...location, warehouseId: '999' } : location, organizationName: warehouse.organizationName, shopName: warehouse.shopName, warehouseName: warehouse.warehouseName, channelNames: [warehouse.channelName] }] }); }
    return Response.json(wrong ? { ...baseline, poolId: '999' } : baseline);
  } }));
  assert.equal((await gateway.warehouses(actor))[0].warehouseName, warehouse.warehouseName); assert.equal((await gateway.targets(actor, warehouse)).items[0].poolId, poolId); assert.equal((await gateway.baseline(actor, poolId)).poolId, poolId); wrong = true; await assert.rejects(gateway.targets(actor, warehouse)); await assert.rejects(gateway.baseline(actor, poolId));
});

test('original-key recovery remains usable with the operation grant alone and never calls ordinary source reads', async () => {
  let calls = 0;
  const gateway = createSourceReconciliationGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.ok(request.url.endsWith('/source-reconciliations/operations/query')); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('Idempotency-Key'), null);
    return Response.json({ state: 'committed', result: proposed });
  } }));
  assert.deepEqual(await gateway.recover(actor, proposeOp, proposeKey, false), { state: 'committed', result: proposed }); assert.equal(calls, 1);
});

test('directory, operation recovery and MFA use actual dedicated grants and safe return path', () => {
  const path = '/inventory/source-reconciliations'; assert.equal(identityReturnPath(path), path);
  assert.ok(identityDestinations(['stock.source.reconciliation.read']).some(item => item.href === path));
  for (const grants of [[], ['stock.read'], ['stock.source.read']]) assert.ok(!identityDestinations(grants).some(item => item.href === path));
  assert.match(identityDestinations([proposeOp]).find(item => item.href === path)?.description ?? '', /hasil permintaan/);
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [proposeOp, decideOp] }); assert.equal(stepUpFields(session, decideOp, path).returnPath, path);
  assert.equal(recoveryPage(decideOp).href, path); assert.equal(operationLabel(proposeOp), 'Pengajuan rekonsiliasi jumlah fisik');
});
