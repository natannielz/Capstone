import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { createReturnEntitlementGateway, prepareReturnEntitlement, parseReturnEntitlementView, parseReturnEntitlementResult, returnEntitlementOwner, returnEntitlementInput } from '../src/modules/fulfillment/return-entitlement-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { MutationLedger } from '../src/modules/administration/workflow';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', invoiceId = '9007199254740994', caseId = '9223372036854775807', returnId = '9007199254740996';
const key = 'er.' + caseId + '.' + returnId + '.credit.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines: reported,
  decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const, decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const assessment = { inspectedAt: '2026-10-01T03:04:00Z', publicNote: 'Barang sudah diperiksa', lines: [{ receiptId: '93', skuId: '94', inspectedBaseUnits: '1', identity: 'matched' as const, condition: 'opened' as const }] };
const resolution = { caseId, invoiceId, returnId, revision: '1' as const, binding: 'd'.repeat(64), inspection: assessment,
  request: { requestedAt: '2026-10-01T03:05:00Z', action: 'credit' as const, publicNote: 'Ajukan kredit', state: 'resolution_requested' as const } };
const capacity = { receiptId: '93', skuId: '94', inspectedBaseUnits: '1', identity: 'matched' as const, condition: 'opened' as const,
  issuedBaseUnits: '6', issuedNet: '10.00', issuedTax: '1.00', issuedCharges: '2.00', previouslyAttributedBaseUnits: '0', previouslyAttributedNet: '0.00', previouslyAttributedTax: '0.00', previouslyAttributedCharges: '0.00' };
const view = { caseId, invoiceId, returnId, action: 'credit' as const, version: '0', buyerName: source.buyerName, lines: [capacity], request: null };
const input = { lines: [{ receiptId: '93', skuId: '94', tax: '0.10', charges: '0.20' }] };
const attributed = { receiptId: '93', skuId: '94', attributedBaseUnits: '1', net: '1.67', tax: '0.10', charges: '0.20' };
const preview = { view, binding: 'e'.repeat(64), attributionProcedureReference: 'Prosedur nilai barang', taxTreatmentReference: 'Prosedur pajak', chargeTreatmentReference: 'Prosedur biaya',
  creditNoteProcedureReference: 'Prosedur nota kredit', net: '1.67', tax: '0.10', charges: '0.20', credit: '1.97', lines: [attributed] };
const result = { returnId, caseId, invoiceId, action: 'credit' as const, version: '1', requestedAt: '2026-10-01T03:06:00Z', net: preview.net, tax: preview.tax, charges: preview.charges,
  credit: preview.credit, lines: [attributed], state: 'financial_review_requested' as const, operation: 'billing.return.entitlement.request' as const };
const savedView = { ...view, version: '1', lines: [{ ...capacity, previouslyAttributedBaseUnits: '1', previouslyAttributedNet: result.net, previouslyAttributedTax: result.tax, previouslyAttributedCharges: result.charges }], request: result };
const command = () => prepareReturnEntitlement(actor, owner, resolution, preview, input, 'Pertimbangan finansial privat');
function journalFixture() {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null, getItem: (name: string) => values.get(name) ?? null,
    setItem: (name: string, value: string) => { values.set(name, value); }, removeItem: (name: string) => { values.delete(name); } };
  return { values, journal: new OperationJournal(() => storage), storage };
}

test('return financial requests bind exact inspected lines, cumulative issued capacity and lossless owner/action IDs', () => {
  assert.deepEqual(returnEntitlementOwner(key), { caseId, returnId, action: 'credit' }); assert.deepEqual(parseReturnEntitlementView(view, owner, resolution), view);
  assert.deepEqual(parseReturnEntitlementView(savedView, owner, resolution), savedView); assert.deepEqual(parseReturnEntitlementResult(result, key), result);
  assert.deepEqual(parseReturnEntitlementView({ ...savedView, version: '2' }, owner, resolution).request, result);
  for (const changed of [{ ...view, invoiceId: '1' }, { ...view, caseId: '1' }, { ...view, returnId: '1' }, { ...view, action: 'refund' }, { ...view, buyerName: 'Other buyer' },
    { ...view, lines: [{ ...capacity, inspectedBaseUnits: '2' }] }, { ...view, lines: [{ ...capacity, identity: 'different' }] }, { ...view, lines: [{ ...capacity, condition: 'sealed' }] },
    { ...view, lines: [{ ...capacity, issuedBaseUnits: '7' }] }, { ...view, lines: [{ ...capacity, previouslyAttributedBaseUnits: '7' }] }, { ...view, lines: [{ ...capacity, previouslyAttributedTax: '1.01' }] },
    { ...view, lines: [{ ...capacity, previouslyAttributedNet: '0.01' }] }, { ...view, lines: [capacity, capacity] }, { ...view, approvedCredit: '1.97' },
    { ...savedView, version: '0' }, { ...savedView, request: { ...result, requestedAt: source.issuedAt } }]) assert.throws(() => parseReturnEntitlementView(changed, owner, resolution));
  for (const changed of [{ ...result, action: 'refund' }, { ...result, caseId: '1' }, { ...result, returnId: '1' }, { ...result, invoiceId: Number(invoiceId) }, { ...result, version: '0' },
    { ...result, state: 'approved' }, { ...result, state: 'paid' }, { ...result, operation: 'fulfillment.case.resolution_request' }, { ...result, credit: '1.98' }, { ...result, lines: [attributed, attributed] }]) assert.throws(() => parseReturnEntitlementResult(changed, key));
  for (const changed of [key + '\n', key.replace('.credit.', '.replacement.'), key.replace(caseId, '9223372036854775808'), key.replace('er.', 'cq.'), key.replace('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'original')]) assert.throws(() => returnEntitlementOwner(changed));
  assert.deepEqual(recoveryPage(result.operation), { href: '/fulfillment/cases', label: 'halaman pengajuan nilai finansial retur' }); assert.equal(operationLabel(result.operation), 'Pengajuan nilai finansial retur');
});

test('explicit components are never silently zeroed and frozen consent includes the exact preview/version/amounts', () => {
  const prepared = command(); assert.ok(Object.isFrozen(prepared)); assert.ok(Object.isFrozen(prepared.body)); assert.ok(Object.isFrozen(prepared.body.input));
  assert.ok(Object.isFrozen(prepared.body.input.lines)); assert.ok(Object.isFrozen(prepared.body.input.lines[0])); assert.ok(Object.isFrozen(prepared.expectedResult.lines[0]));
  assert.deepEqual(prepared.body, { action: 'credit', expectedVersion: '0', binding: preview.binding, input, reason: 'Pertimbangan finansial privat' });
  assert.deepEqual(returnEntitlementInput(view, { lines: [{ ...input.lines[0], tax: '0.00', charges: '0.00' }] }).lines[0], { ...input.lines[0], tax: '0.00', charges: '0.00' });
  for (const amount of ['', '0', '0.1', '-0.01', ' 0.10', '1e0', '0.100', '1000000000000000.00']) assert.throws(() => returnEntitlementInput(view, { lines: [{ ...input.lines[0], tax: amount }] }));
  for (const invalid of [{ lines: [] }, { lines: [input.lines[0], input.lines[0]] }, { lines: [{ ...input.lines[0], skuId: '95' }] }, { lines: [{ ...input.lines[0], tax: '1.01' }] },
    { lines: [{ ...input.lines[0], charges: '2.01' }] }]) assert.throws(() => returnEntitlementInput(view, invalid));
  assert.throws(() => returnEntitlementInput({ ...view, lines: [{ ...capacity, previouslyAttributedBaseUnits: '6', previouslyAttributedNet: '10.00' }] }, input));
  assert.throws(() => returnEntitlementInput(savedView, input));
  for (const reason of ['', ' ', ' leading', 'trailing ', 'x'.repeat(1001), 'zero\u200bwidth', 'line\nfeed']) assert.throws(() => prepareReturnEntitlement(actor, owner, resolution, preview, input, reason));
  for (const changed of [{ ...preview, credit: '1.98' }, { ...preview, lines: [{ ...attributed, tax: '0.11' }], tax: '0.11', credit: '1.98' },
    { ...preview, lines: [{ ...attributed, attributedBaseUnits: '2' }] }, { ...preview, view: savedView }, { ...preview, attributionProcedureReference: '' }]) assert.throws(() => prepareReturnEntitlement(actor, owner, resolution, changed, input, 'Privat'));
  assert.throws(() => prepareReturnEntitlement(actor, owner, { ...resolution, request: { ...resolution.request, action: 'replacement' } }, preview, input, 'Privat'));
});

test('return financial totals preserve exact cents above browser integer precision and allow zero net with explicitly positive tax', () => {
  const huge = { ...result, net: '9007199254740.01', tax: '0.00', charges: '0.01', credit: '9007199254740.02', lines: [{ ...attributed, net: '9007199254740.01', tax: '0.00', charges: '0.01' }] };
  assert.equal(parseReturnEntitlementResult(huge, key).credit, huge.credit);
  assert.throws(() => parseReturnEntitlementResult({ ...huge, credit: '9007199254740.03' }, key));
  const zero = { ...result, net: '0.00', tax: '0.01', charges: '0.00', credit: '0.01', lines: [{ ...attributed, net: '0.00', tax: '0.01', charges: '0.00' }] };
  assert.deepEqual(parseReturnEntitlementResult(zero, key), zero); assert.throws(() => parseReturnEntitlementResult({ ...zero, tax: '0.00', credit: '0.00', lines: [{ ...zero.lines[0], tax: '0.00' }] }, key));
});

test('protected return financial queries/preview/request send exact action, owners, actor, CSRF, version and original key', async () => {
  const requests: Request[] = [];
  const gateway = createReturnEntitlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    assert.ok(request.url.includes('/billing/cases/' + caseId + '/returns/' + returnId + '/entitlement')); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    if (request.url.endsWith('/query')) { assert.deepEqual(await request.clone().json(), { purpose: 'return-entitlement', action: 'credit' }); return Response.json(view); }
    if (request.url.endsWith('/preview')) { assert.deepEqual(await request.clone().json(), { action: 'credit', input }); return Response.json(preview); }
    assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.clone().json(), command().body); return Response.json(result);
  } }));
  assert.deepEqual(await gateway.read(actor, owner, resolution), view); assert.deepEqual(await gateway.preview(actor, owner, resolution, view, input), preview);
  await assert.rejects(() => gateway.request(command(), key, () => false), RequestNotSentError); assert.equal(requests.length, 2);
  await assert.rejects(() => gateway.request(command(), key.replace('.credit.', '.refund.'), () => true), RequestNotSentError); assert.equal(requests.length, 2);
  assert.deepEqual(await gateway.request(command(), key, () => true), result); assert.equal(requests.length, 3);
});

test('changed context, action, financial result or preview cannot be accepted as the frozen financial request', async () => {
  let response: unknown = result; const gateway = createReturnEntitlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); return Response.json(response);
  } }));
  let observed = 0; await assert.rejects(() => gateway.request(command(), key, () => ++observed === 1), { status: 503, code: 'commit_outcome_unknown' });
  for (const changed of [{ ...result, invoiceId: '1' }, { ...result, version: '2' }, { ...result, action: 'refund' },
    { ...result, tax: '0.11', credit: '1.98', lines: [{ ...attributed, tax: '0.11' }] }]) { response = changed; await assert.rejects(() => gateway.request(command(), key, () => true)); }
  response = { ...preview, tax: '0.11', credit: '1.98', lines: [{ ...attributed, tax: '0.11' }] }; await assert.rejects(() => gateway.preview(actor, owner, resolution, view, input));
  response = { ...preview, view: { ...view, lines: [{ ...capacity, issuedNet: '10.01' }] } }; await assert.rejects(() => gateway.preview(actor, owner, resolution, view, input));
  response = { ...preview, view: { ...view, lines: [{ ...capacity, previouslyAttributedBaseUnits: '1', previouslyAttributedNet: '1.00' }] } }; await assert.rejects(() => gateway.preview(actor, owner, resolution, view, input));
  for (const changed of [{ status: 'not_confirmed', result }, { status: 'committed', result: null }, { status: 'committed', result, approved: true }]) { response = changed; await assert.rejects(() => gateway.recover(actor, key)); }
});

test('lost return financial replies retain only subtype metadata across reload until exact actor/owner/action lookup without resend or close', async () => {
  const { journal, values, storage } = journalFixture(); const ledger = new MutationLedger(() => key); let writes = 0, committed = false;
  const gateway = createReturnEntitlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.clone().json(), { purpose: 'operation-recovery', action: 'credit', requestKey: key }); return Response.json({ status: committed ? 'committed' : 'not_confirmed', result: committed ? result : null }); }
    writes++; committed = true; throw new TypeError('Committed reply lost');
  } }));
  await assert.rejects(() => submitTrackedRequest(ledger, journal, actor, result.operation, result.operation, command(), originalKey => gateway.request(command(), originalKey, () => true)));
  assert.equal(writes, 1); assert.equal(ledger.isUncertain(result.operation), true);
  const reloaded = new OperationJournal(() => storage), [entry] = reloaded.list(actor); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.ok(![...values.values()].join('').includes('finansial')); assert.ok(![...values.values()].join('').includes('1.97'));
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Must not close'); } }, adapters = { 'billing.return.entitlement.request': { lookup: async () => gateway.recover(actor, key) } };
  assert.equal(operationRecoveryCapabilities(entry.operation).unavailable, 'adapter'); assert.equal(operationRecoveryCapabilities(entry.operation, adapters).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy)); await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy, adapters, 'close'));
  committed = false; assert.equal((await reconcileOperationRecovery(entry, reloaded, legacy, adapters)).state, 'unconfirmed'); assert.equal(reloaded.list(actor).length, 1);
  committed = true; const snapshot = [...values.entries()];
  for (const changed of [{ ...result, caseId: '1' }, { ...result, returnId: '1' }, { ...result, action: 'refund' }, { ...result, state: 'paid' },
    { ...result, operation: 'fulfillment.case.resolution_request' }, { returnId, caseId, invoiceId, revision: '1', requestedAt: result.requestedAt, action: 'credit', state: 'resolution_requested', operation: 'fulfillment.case.resolution_request' }]) {
    await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy, { 'billing.return.entitlement.request': { lookup: async () => ({ state: 'committed', result: changed as never }) } })); assert.deepEqual([...values.entries()], snapshot);
  }
  await reconcileOperationRecovery(entry, reloaded, legacy, adapters); assert.equal(reloaded.list(actor).length, 0); assert.equal(writes, 1);
});

test('financial recovery preserves the original reference on revoked permission, timeout, late context or noncanonical envelope', async () => {
  const { journal, values } = journalFixture(), entry = journal.remember(actor, result.operation, key), snapshot = [...values.entries()];
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Wrong owner'); } };
  for (const failure of [new TypeError('Lost reply'), { status: 403, code: 'scope_denied' }]) {
    await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.return.entitlement.request': { lookup: async () => { throw failure; } } })); assert.deepEqual([...values.entries()], snapshot);
  }
  let current = true; await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.return.entitlement.request': { lookup: async () => { current = false; return { state: 'committed', result }; } } }, undefined, () => current));
  assert.deepEqual([...values.entries()], snapshot);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.return.entitlement.request': { lookup: async () => ({ state: 'committed', result, extra: true } as never) } }));
  assert.deepEqual([...values.entries()], snapshot);
});
