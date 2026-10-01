import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { canApproveReturnFinancial, createReturnFinancialApprovalGateway, parseReturnFinancialApprovalResult, parseReturnFinancialApprovalView,
  prepareReturnFinancialApproval, returnFinancialApprovalInput, returnFinancialApprovalOwner } from '../src/modules/fulfillment/return-financial-approval-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { MutationLedger } from '../src/modules/administration/workflow';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', invoiceId = '9007199254740994', caseId = '9223372036854775807', returnId = '9007199254740996';
const key = 'fa.' + caseId + '.' + returnId + '.refund.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const creditKey = key.replace('.refund.', '.credit.');
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines: reported,
  decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const, decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const request = { returnId, caseId, invoiceId, action: 'refund' as const, version: '1', requestedAt: '2026-10-01T03:06:00Z', net: '1000.02', tax: '0.00', charges: '0.00', credit: '1000.02',
  lines: [{ receiptId: '93', skuId: '94', attributedBaseUnits: '1', net: '1000.02', tax: '0.00', charges: '0.00' }], state: 'financial_review_requested' as const, operation: 'billing.return.entitlement.request' as const };
const invoice = { invoiceId, version: '7', issued: '6000.12', paid: '1000.00', credited: '0.00', outstanding: '5000.12', paymentState: 'partial' as const, buyerName: source.buyerName };
const receipt = { receiptId: '9007199254740997', version: '3', received: '1200.00', allocated: '1000.00', reservedForRefund: '100.00', available: '100.00' };
const candidates = [{ originalAllocationId: '9007199254740999', receiptId: receipt.receiptId, receiptVersion: receipt.version, originallyAllocated: '700.00', previouslyReleased: '100.00', available: '600.00' },
  { originalAllocationId: '9007199254740998', receiptId: receipt.receiptId, receiptVersion: receipt.version, originallyAllocated: '400.00', previouslyReleased: '0.00', available: '400.00' }];
const view = { request, invoice, receipts: [receipt], allocations: candidates, hasMore: false, beforeAllocationId: candidates[1].originalAllocationId, approval: null };
const input = { expectedInvoiceVersion: invoice.version, releases: [{ originalAllocationId: candidates[1].originalAllocationId, expectedReceiptVersion: receipt.version, amount: '400.00' },
  { originalAllocationId: candidates[0].originalAllocationId, expectedReceiptVersion: receipt.version, amount: '500.00' }] };
const effect = { invoiceVersion: '8', credit: request.credit, reservedRefund: '900.00', remainingInvoice: '4900.10',
  receipts: [{ receiptId: receipt.receiptId, version: '4', released: '900.00', reservedForRefund: '1000.00', available: '100.00' }],
  releases: input.releases.map(line => ({ originalAllocationId: line.originalAllocationId, invoiceId, amount: line.amount })) };
const preview = { view, binding: 'e'.repeat(64), creditNoteProcedureReference: 'Prosedur nota kredit', taxTreatmentReference: 'Prosedur pajak', chargeTreatmentReference: 'Prosedur biaya', refundReservationProcedureReference: 'Prosedur reservasi refund', ...effect };
const result = { financialApprovalId: '9007199254741000', caseId, returnId, invoiceId, action: 'refund' as const, ...effect, recordedAt: '2026-10-01T03:07:00Z', operation: 'billing.return.financial.approve' as const, state: 'refund_reserved' as const };
const creditView = { ...view, request: { ...request, action: 'credit' as const }, receipts: [], allocations: [], beforeAllocationId: null };
const creditInput = { expectedInvoiceVersion: invoice.version, releases: [] };
const creditEffect = { invoiceVersion: '8', credit: request.credit, reservedRefund: '0.00', remainingInvoice: '4000.10', receipts: [], releases: [] };
const creditPreview = { ...preview, view: creditView, ...creditEffect };
const creditResult = { ...result, action: 'credit' as const, state: 'invoice_credited' as const, ...creditEffect };
const command = () => prepareReturnFinancialApproval(actor, owner, returnId, 'refund', preview, input, 'Persetujuan Finance privat');
function journalFixture() {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null, getItem: (name: string) => values.get(name) ?? null,
    setItem: (name: string, value: string) => { values.set(name, value); }, removeItem: (name: string) => { values.delete(name); } };
  return { values, journal: new OperationJournal(() => storage), storage };
}

test('independent Finance grants and exact owner/subtype reject maker permissions, wrong money/state and private projections', () => {
  const finance = ['case.read', 'invoice.read', 'credit.approve'];
  assert.equal(canApproveReturnFinancial(finance, 'credit'), true); assert.equal(canApproveReturnFinancial(finance, 'refund'), false);
  assert.equal(canApproveReturnFinancial(finance, 'replacement' as never), false);
  assert.equal(canApproveReturnFinancial([...finance, 'refund.approve', 'payment.review.read'], 'refund'), true);
  assert.equal(canApproveReturnFinancial(['case.read', 'invoice.read', 'case.manage', 'credit.request', 'refund.request'], 'credit'), false);
  for (const permission of finance) assert.equal(canApproveReturnFinancial(finance.filter(value => value !== permission), 'credit'), false);
  assert.deepEqual(returnFinancialApprovalOwner(key), { caseId, returnId, action: 'refund' }); assert.deepEqual(parseReturnFinancialApprovalResult(result, key), result);
  assert.deepEqual(parseReturnFinancialApprovalResult(creditResult, creditKey), creditResult);
  assert.deepEqual(parseReturnFinancialApprovalView(view, owner, returnId, 'refund'), view);
  for (const changed of [key + '\n', key.replace('fa.', 'er.'), key.replace('.refund.', '.replacement.'), key.replace(caseId, '9223372036854775808'), key.replace('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'original')]) assert.throws(() => returnFinancialApprovalOwner(changed));
  for (const changed of [{ ...result, caseId: '1' }, { ...result, returnId: '1' }, { ...result, action: 'credit' }, { ...result, invoiceId: Number(invoiceId) }, { ...result, state: 'paid' },
    { ...result, operation: request.operation }, { ...result, reservedRefund: '900.01' }, { ...result, receipts: [{ ...result.receipts[0], released: '899.99' }] },
    { ...result, releases: [result.releases[0], result.releases[0]] }, { ...result, people: [] }]) assert.throws(() => parseReturnFinancialApprovalResult(changed, key));
  for (const changed of [{ ...view, request: { ...request, invoiceId: '1' } }, { ...view, request: { ...request, credit: '1000.03' } }, { ...view, invoice: { ...invoice, buyerName: 'Other buyer' } },
    { ...view, invoice: { ...invoice, outstanding: '5000.13' } }, { ...view, invoice: { ...invoice, paymentState: 'paid' } }, { ...view, receipts: [{ ...receipt, available: '100.01' }] },
    { ...view, allocations: [...candidates].reverse() }, { ...view, beforeAllocationId: null }, { ...view, hasMore: true }, { ...view, request: { ...request, actorId: actor } },
    { ...view, request: { ...request, requestedAt: source.issuedAt } }, { ...view, approval: { ...result, recordedAt: source.issuedAt } }]) assert.throws(() => parseReturnFinancialApprovalView(changed, owner, returnId, 'refund'));
  assert.throws(() => parseReturnFinancialApprovalView(view, owner, returnId, 'refund', candidates[0].originalAllocationId));
  assert.deepEqual(recoveryPage(result.operation), { href: '/fulfillment/cases', label: 'halaman persetujuan Finance retur' }); assert.equal(operationLabel(result.operation), 'Persetujuan Finance retur');
});

test('refund preview reserves grouped receipt funds atomically with credit and freezes exact selected allocations without freeing cash', () => {
  const prepared = command(); assert.ok(Object.isFrozen(prepared)); assert.ok(Object.isFrozen(prepared.body)); assert.ok(Object.isFrozen(prepared.body.input));
  assert.ok(Object.isFrozen(prepared.body.input.releases)); assert.ok(Object.isFrozen(prepared.body.input.releases[0])); assert.ok(Object.isFrozen(prepared.expectedResult.receipts[0]));
  assert.ok(Object.isFrozen(prepared.expectedResult.releases[0])); assert.deepEqual(prepared.body.input, input);
  assert.equal(prepared.expectedResult.receipts.length, 1); assert.equal(prepared.expectedResult.receipts[0].available, receipt.available);
  assert.equal(prepareReturnFinancialApproval(actor, owner, returnId, 'credit', creditPreview, creditInput, 'Nota kredit').expectedResult.reservedRefund, '0.00');
  for (const invalid of [{ ...input, expectedInvoiceVersion: '6' }, { ...input, releases: [] }, { ...input, releases: [{ ...input.releases[0], amount: '400.01' }] },
    { ...input, releases: [{ ...input.releases[0], expectedReceiptVersion: '2' }] }, { ...input, releases: [input.releases[0], input.releases[0]] },
    { ...input, releases: [{ ...input.releases[0], originalAllocationId: '1' }] }]) assert.throws(() => returnFinancialApprovalInput(view, invalid));
  for (const amount of ['', '0', '0.1', '-0.01', ' 0.10', '1e0', '0.100', '0.00', '1000000000000000.00']) assert.throws(() => returnFinancialApprovalInput(view, { ...input, releases: [{ ...input.releases[0], amount }] }));
  assert.throws(() => returnFinancialApprovalInput(creditView, input)); assert.throws(() => returnFinancialApprovalInput({ ...view, approval: result }, input));
  for (const changed of [{ ...preview, credit: '1000.03' }, { ...preview, invoiceVersion: '9' }, { ...preview, remainingInvoice: '4900.11' },
    { ...preview, receipts: [{ ...effect.receipts[0], available: '1000.00' }] }, { ...preview, receipts: [{ ...effect.receipts[0], reservedForRefund: '999.99' }] },
    { ...preview, view: { ...view, approval: result } }, { ...preview, taxTreatmentReference: '' }, { ...preview, releases: [...effect.releases].reverse().map((line, index) => ({ ...line, amount: index ? '499.99' : '400.01' })) }])
    assert.throws(() => prepareReturnFinancialApproval(actor, owner, returnId, 'refund', changed, input, 'Privat'));
  for (const reason of ['', ' ', ' leading', 'trailing ', 'x'.repeat(1001), 'zero\u200bwidth', 'line\nfeed']) assert.throws(() => prepareReturnFinancialApproval(actor, owner, returnId, 'refund', preview, input, reason));
});

test('exact cents and grouped original allocation limits remain lossless above Number precision', () => {
  const huge = { ...creditResult, credit: '9007199254740.01', remainingInvoice: '0.01' };
  assert.equal(parseReturnFinancialApprovalResult(huge, creditKey).credit, huge.credit);
  const hugeRequest = { ...creditView.request, net: '9007199254740.01', credit: '9007199254740.01', lines: [{ ...request.lines[0], net: '9007199254740.01' }] };
  const hugeView = { ...creditView, request: hugeRequest, invoice: { ...invoice, issued: '9007199254740.02', paid: '0.00', credited: '0.00', outstanding: '9007199254740.02', paymentState: 'unpaid' as const } };
  assert.equal(prepareReturnFinancialApproval(actor, owner, returnId, 'credit', { ...creditPreview, view: hugeView, credit: huge.credit, remainingInvoice: '0.01' }, creditInput, 'Privat').expectedResult.remainingInvoice, '0.01');
  assert.throws(() => parseReturnFinancialApprovalView({ ...hugeView, invoice: { ...hugeView.invoice, outstanding: '9007199254740.03' } }, owner, returnId, 'credit'));
  assert.throws(() => parseReturnFinancialApprovalView({ ...view, allocations: [{ ...candidates[0], originallyAllocated: '800.01', available: '700.01' }, candidates[1]] }, owner, returnId, 'refund'));
});

test('protected Finance query/preview/approval preserves exact owners, actor, CSRF, original key and invoice/receipt versions', async () => {
  const requests: Request[] = [];
  const gateway = createReturnFinancialApprovalGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    assert.ok(request.url.includes('/billing/cases/' + caseId + '/returns/' + returnId + '/financial-approval')); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf');
    if (request.url.endsWith('/query')) { assert.deepEqual(await request.clone().json(), { purpose: 'return-financial-approval', action: 'refund', beforeAllocationId: null }); return Response.json(view); }
    if (request.url.endsWith('/preview')) { assert.deepEqual(await request.clone().json(), { action: 'refund', input }); return Response.json(preview); }
    assert.equal(request.headers.get('If-Match'), '"7"'); assert.equal(request.headers.get('Idempotency-Key'), key); assert.deepEqual(await request.clone().json(), command().body); return Response.json(result);
  } }));
  assert.deepEqual(await gateway.read(actor, owner, returnId, 'refund'), view); assert.deepEqual(await gateway.preview(actor, owner, view, input), preview);
  await assert.rejects(() => gateway.approve(command(), key, () => false), RequestNotSentError); assert.equal(requests.length, 2);
  await assert.rejects(() => gateway.approve(command(), creditKey, () => true), RequestNotSentError); assert.equal(requests.length, 2);
  assert.deepEqual(await gateway.approve(command(), key, () => true), result); assert.equal(requests.length, 3);
});

test('stale context, changed source balances, wrong preview effects and committed envelope cannot pass frozen consent', async () => {
  let response: unknown = result; const gateway = createReturnFinancialApprovalGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); return Response.json(response);
  } }));
  let observed = 0; await assert.rejects(() => gateway.approve(command(), key, () => ++observed === 1), { status: 503, code: 'commit_outcome_unknown' });
  for (const changed of [{ ...result, invoiceId: '1', releases: result.releases.map(line => ({ ...line, invoiceId: '1' })) }, { ...result, invoiceVersion: '9' },
    { ...result, receipts: [{ ...effect.receipts[0], available: '100.01' }] }, { ...result, credit: '1000.03' }]) { response = changed; await assert.rejects(() => gateway.approve(command(), key, () => true)); }
  response = { ...preview, view: { ...view, invoice: { ...invoice, issued: '6000.13', outstanding: '5000.13' } }, remainingInvoice: '4900.11' };
  await assert.rejects(() => gateway.preview(actor, owner, view, input));
  response = { ...preview, view: { ...view, receipts: [{ ...receipt, received: '1200.01', available: '100.01' }] }, receipts: [{ ...effect.receipts[0], available: '100.01' }] };
  await assert.rejects(() => gateway.preview(actor, owner, view, input));
  for (const changed of [{ status: 'not_confirmed', result }, { status: 'committed', result: null }, { status: 'committed', result, approved: true }, { status: 'closed', result: null }]) { response = changed; await assert.rejects(() => gateway.recover(actor, key)); }
});

test('lost Finance replies persist only exact subtype metadata until owning lookup, never resend or manual close', async () => {
  const { journal, values, storage } = journalFixture(); const ledger = new MutationLedger(() => key); let writes = 0, committed = false;
  const gateway = createReturnFinancialApprovalGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    if (request.url.endsWith('/operations/query')) { assert.deepEqual(await request.clone().json(), { purpose: 'operation-recovery', action: 'refund', requestKey: key }); return Response.json({ status: committed ? 'committed' : 'not_confirmed', result: committed ? result : null }); }
    writes++; committed = true; throw new TypeError('Committed reply lost');
  } }));
  await assert.rejects(() => submitTrackedRequest(ledger, journal, actor, result.operation, result.operation, command(), originalKey => gateway.approve(command(), originalKey, () => true)));
  assert.equal(writes, 1); assert.equal(ledger.isUncertain(result.operation), true);
  const reloaded = new OperationJournal(() => storage), [entry] = reloaded.list(actor); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.ok(![...values.values()].join('').includes('privat')); assert.ok(![...values.values()].join('').includes('1000.02'));
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Must not close'); } }, adapters = { 'billing.return.financial.approve': { lookup: async () => gateway.recover(actor, key) } };
  assert.equal(operationRecoveryCapabilities(entry.operation).unavailable, 'adapter'); assert.equal(operationRecoveryCapabilities(entry.operation, adapters).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy)); await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy, adapters, 'close'));
  committed = false; assert.equal((await reconcileOperationRecovery(entry, reloaded, legacy, adapters)).state, 'unconfirmed'); assert.equal(reloaded.list(actor).length, 1);
  const snapshot = [...values.entries()];
  for (const changed of [{ ...result, caseId: '1' }, { ...result, returnId: '1' }, { ...result, action: 'credit' }, { ...result, state: 'paid' }, { ...result, operation: request.operation }]) {
    await assert.rejects(() => reconcileOperationRecovery(entry, reloaded, legacy, { 'billing.return.financial.approve': { lookup: async () => ({ state: 'committed', result: changed as never }) } })); assert.deepEqual([...values.entries()], snapshot);
  }
  committed = true; await reconcileOperationRecovery(entry, reloaded, legacy, adapters); assert.equal(reloaded.list(actor).length, 0); assert.equal(writes, 1);
});

test('Finance recovery preserves exact references on revoked access, timeout, expired context and noncanonical receipts', async () => {
  const { journal, values } = journalFixture(), entry = journal.remember(actor, result.operation, key), snapshot = [...values.entries()];
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Wrong owner'); } };
  for (const failure of [new TypeError('Lost reply'), { status: 403, code: 'scope_denied' }]) {
    await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.return.financial.approve': { lookup: async () => { throw failure; } } })); assert.deepEqual([...values.entries()], snapshot);
  }
  let current = true; await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.return.financial.approve': { lookup: async () => { current = false; return { state: 'committed', result }; } } }, undefined, () => current));
  assert.deepEqual([...values.entries()], snapshot);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.return.financial.approve': { lookup: async () => ({ state: 'committed', result, extra: true } as never) } }));
  assert.deepEqual([...values.entries()], snapshot);
});
