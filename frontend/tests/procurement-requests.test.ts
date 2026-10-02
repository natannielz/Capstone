import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createProcurementGateway, parseProcurementRequest, parseProcurementResult, parseProcurementTargets, prepareProcurement,
  prepareProcurementDecision, procurementInput, procurementKeyOwner, procurementQuantity } from '../src/modules/procurement/procurement-request-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', requestId = '9007199254740994';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '2', profileVersion: '3' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, warehouse: { ...organization, id: '102', code: 'WARE', name: 'Gudang' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } };
const selected = { location, ...source };
const choice = { skuId: '500', skuRevision: '2', definitionId: '501', definitionVersion: '1', skuCode: 'PAPER-BOX', familyName: 'Kertas', packageLabel: 'Karton 12', packageKind: 'sealed' as const, baseUnitCode: 'RIM', baseUnitsPerPackage: 12 };
const input = { location, route: 'ddo' as const, kind: 'purchase_request' as const, businessDate: '2026-10-03', externalReference: null, reason: 'Kebutuhan toko yang telah diperiksa',
  lines: [{ skuId: choice.skuId, expectedSkuRevision: choice.skuRevision, definitionId: choice.definitionId, definitionVersion: choice.definitionVersion, packages: 30, specification: null }] };
const lines = [{ ...choice, packages: 30, specification: null }];
const preview = { input, source, lines, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:00:00Z' };
const request = { id: requestId, version: '1' as const, input, source, lines, submittedAt: '2026-10-03T01:01:00Z', state: 'submitted' as const, decision: null,
  downstream: { supplier: 'unresolved' as const, commercial: 'unpriced' as const, payment: 'not_recorded' as const, receipt: 'not_recorded' as const } };
const decisionPreview = { request, decision: 'approve' as const, binding: 'b'.repeat(64), observedAt: '2026-10-03T01:02:00Z' };
const submitted = { requestId, requestVersion: '1' as const, decisionId: null, status: 'submitted' as const };
const approved = { ...submitted, decisionId: '9007199254740995', status: 'approved' as const };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', submitKey = `pr.100.101.102.103.${uuid}`, approveKey = `prd.approve.${requestId}.${uuid}`;
const command = () => prepareProcurement(actor, selected, [choice], input, preview);
const decisionCommand = () => prepareProcurementDecision(actor, request, decisionPreview, 'Pemeriksaan independen kebutuhan barang');

test('all four routes require deliberate kind, date and catalog quantities without fabricated commercial or receipt facts', () => {
  for (const route of ['ddo', 'smart', 'pasar_kering'] as const) assert.equal(procurementInput({ ...input, route }).route, route);
  assert.equal(procurementInput({ ...input, route: 'omi_regular', kind: 'stock_need' }).kind, 'stock_need');
  assert.equal(procurementInput({ ...input, route: 'omi_regular', kind: 'incoming_notice', externalReference: 'OMI / 12' }).externalReference, 'OMI / 12');
  assert.equal(procurementInput({ ...input, businessDate: '0001-01-02' }).businessDate, '0001-01-02');
  const exact = '\uFEFFＲencana\u200d';
  assert.equal(procurementInput({ ...input, reason: exact, lines: [{ ...input.lines[0], specification: exact }] }).lines[0].specification, exact);
  assert.equal(procurementQuantity('1000000'), 1_000_000);
  for (const value of ['', '0', '01', '1.5', '1e3', '1000001', ' 2', '2\n']) assert.throws(() => procurementQuantity(value), RequestNotSentError);
  for (const changed of [{ ...input, route: 'omi_regular', kind: 'purchase_request' }, { ...input, route: 'smart', kind: 'stock_need' },
    { ...input, route: 'omi_regular', kind: 'incoming_notice' }, { ...input, businessDate: '0001-01-01' }, { ...input, businessDate: '2026-02-30' },
    { ...input, businessDate: '' }, { ...input, reason: '' }, { ...input, reason: ' baris' }, { ...input, reason: 'dua\nbaris' },
    { ...input, lines: [] }, { ...input, lines: [input.lines[0], input.lines[0]] }, { ...input, supplierId: '99' }])
    assert.throws(() => procurementInput(changed as never), RequestNotSentError);
});

test('named target and review bind exact source, SKU revision, package definition, units, quantity and specifications before freezing', () => {
  assert.deepEqual(parseProcurementTargets({ items: [selected] }).items, [selected]);
  assert.throws(() => parseProcurementTargets({ items: [{ ...selected, warehouse: { ...source.warehouse, id: '202' } }] }));
  assert.throws(() => parseProcurementTargets({ items: [selected, selected] }));
  const frozen = command(); assert.ok(Object.isFrozen(frozen.body.input.lines[0])); assert.ok(Object.isFrozen(frozen.body.input.location));
  for (const changed of [{ ...preview, source: { ...source, shop: { ...source.shop, profileVersion: '4' } } },
    { ...preview, lines: [{ ...lines[0], baseUnitsPerPackage: 24 }] }, { ...preview, lines: [{ ...lines[0], familyName: 'Barang lain' }] },
    { ...preview, lines: [{ ...lines[0], skuRevision: '3' }] }, { ...preview, lines: [{ ...lines[0], definitionVersion: '2' }] },
    { ...preview, lines: [{ ...lines[0], packages: 10 }] }, { ...preview, lines: [{ ...lines[0], specification: 'Logo lain' }] },
    { ...preview, input: { ...input, businessDate: '2026-10-04' } }]) assert.throws(() => prepareProcurement(actor, selected, [choice], input, changed));
  assert.ok(Object.isFrozen(decisionCommand().body));
  assert.throws(() => prepareProcurementDecision(actor, request, { ...decisionPreview, request: { ...request, id: '8' } }, 'Alasan'));
  assert.throws(() => prepareProcurementDecision(actor, request, { ...decisionPreview, observedAt: '2026-10-03T00:00:00Z' }, 'Alasan'));
});

test('request projection rejects inconsistent decisions and private fields or false downstream completion', () => {
  assert.deepEqual(parseProcurementRequest(request, requestId), request);
  const approvedRequest = { ...request, state: 'approved' as const, decision: { id: approved.decisionId, decision: 'approve' as const, reason: 'Diperiksa', decidedAt: '2026-10-03T01:03:00Z' } };
  assert.deepEqual(parseProcurementRequest(approvedRequest), approvedRequest);
  for (const changed of [{ ...request, actorId: actor }, { ...request, state: 'approved' }, { ...request, decision: approvedRequest.decision },
    { ...approvedRequest, decision: { ...approvedRequest.decision, decision: 'reject' } }, { ...request, downstream: { ...request.downstream, receipt: 'received' } },
    { ...request, lines: [{ ...lines[0], packages: 29 }] }, { ...request, source: { ...source, warehouse: { ...source.warehouse, id: '202' } } }])
    assert.throws(() => parseProcurementRequest(changed, requestId));
  assert.throws(() => parseProcurementResult({ ...approved, requestId: '8' }, approveKey, 'procurement.request.approve'));
  assert.throws(() => parseProcurementResult({ ...approved, decisionId: null }, approveKey, 'procurement.request.approve'));
  assert.throws(() => procurementKeyOwner(approveKey, 'procurement.request.reject'));
});

test('procurement transport carries exact actor, CSRF, immutable review and If-Match and never retries denied or uncertain commands', async () => {
  let calls = 0; const gateway = createProcurementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf-memory' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/submissions')) { assert.equal(input.headers.get('Idempotency-Key'), submitKey); assert.deepEqual(await input.json(), command().body); return Response.json(submitted); }
    assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.equal(input.headers.get('If-Match'), '"1"'); assert.deepEqual(await input.json(), decisionCommand().body);
    return calls === 3 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(approved);
  } }));
  await assert.rejects(gateway.submit(command(), submitKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.submit(command(), submitKey, () => true), submitted); assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 3);
  let actorChecks = 0; await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => ++actorChecks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 4);
});

test('catalog package queries are scoped reads with exact paging and reject unexpected costs or mismatched page responses', async () => {
  let response: unknown = { items: [choice], offset: 25, hasMore: false }, calls = 0;
  const gateway = createProcurementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.url.endsWith('/packages/query'), true); assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    assert.deepEqual(await input.json(), { purpose: 'procurement-request-review', location, search: 'Kertas', offset: 25, limit: 25 }); return Response.json(response);
  } }));
  assert.deepEqual((await gateway.packages(actor, location, 'Kertas', 25)).items, [choice]);
  response = { items: [choice], offset: 0, hasMore: false }; await assert.rejects(gateway.packages(actor, location, 'Kertas', 25));
  response = { items: [{ ...choice, supplierPrice: '12000.00' }], offset: 25, hasMore: false }; await assert.rejects(gateway.packages(actor, location, 'Kertas', 25));
  await assert.rejects(gateway.packages(actor, location, 'Kertas', -1), RequestNotSentError); assert.equal(calls, 3);
});

test('original-actor recovery retains only references, remains lookup-only and cannot clear uncertain or foreign decisions', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey); let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'procurement-decision', 'procurement.request.approve', decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0]; assert.equal(journal.list('99').length, 0);
  const serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [decisionCommand().body.reason, decisionPreview.binding, 'PAPER-BOX', input.reason]) assert.equal(serialized.includes(privateValue), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities('procurement.request.approve'), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.request': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.request': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.request': { lookup: async () => ({ state: 'committed', result: { ...approved, requestId: '8' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.request': { lookup: async () => ({ state: 'committed', result: approved }) } }), { state: 'committed', result: { id: requestId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
});

test('submission recovery rechecks the exact location and both write permissions require explicit step-up navigation', async () => {
  let mismatch = false, calls = 0; const gateway = createProcurementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: 'procurement.request.submit', key: submitKey }); return Response.json({ status: 'confirmed', result: submitted }); }
    return Response.json(mismatch ? { ...request, input: { ...request.input, location: { ...location, warehouseId: '202' } }, source: { ...source, warehouse: { ...source.warehouse, id: '202' } } } : request);
  } }));
  assert.deepEqual(await gateway.recover(actor, 'procurement.request.submit', submitKey), { state: 'committed', result: submitted }); mismatch = true;
  await assert.rejects(gateway.recover(actor, 'procurement.request.submit', submitKey)); assert.equal(calls, 4);
  const path = '/procurement/requests'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  for (const action of ['read', 'prepare', 'approve']) assert.equal(identityDestinations(['procurement.' + action]).some(item => item.href === path), true);
  assert.equal(identityDestinations(['supplier.claim']).some(item => item.href === path), false);
  assert.equal(recoveryPage('procurement.request.submit').href, path); assert.equal(operationLabel('procurement.request.approve'), 'Persetujuan permintaan pengadaan');
  for (const permission of ['procurement.prepare', 'procurement.approve']) {
    const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] });
    assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path });
    assert.throws(() => stepUpFields({ ...session, stepUpOperations: [] }, permission, path));
  }
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/requests']) assert.throws(() => loginFields(invalid, '', ''));
});
