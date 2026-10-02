import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { canVerifyRefundSettlement, createRefundSettlementGateway, parseRefundSettlementResult, parseRefundSettlementView,
  prepareRefundSettlement, refundSettlementInput, refundSettlementOwner } from '../src/modules/fulfillment/refund-settlement-client';

const actor = '9007199254740993', caseId = '9007199254740994', invoiceId = '9007199254740995', returnId = '9007199254740996', payoutId = '9007199254740997';
const key = 'rsv.' + payoutId + '.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const,
  reportedProblem: 'Barang rusak', source, lines: reported, decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const,
    decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const payout = { payoutId, draftId: '105', approvalId: '106', state: 'recorded_unverified' as const, amount: '900.02', recordedAt: '2026-10-01T03:09:00Z' };
const view = { payout, caseId, returnId, invoiceId, amount: payout.amount, draftRevision: '1' as const, draftId: payout.draftId, reservationId: '107',
  evidence: { id: '108', version: '1' as const, sha256: 'd'.repeat(64) }, settlement: null,
  candidateInstructions: 'Pilih baris debit dari impor mutasi bank asli.', beneficiaryName: 'Koperasi Cabang Tebet', beneficiaryBankCode: 'BNI', beneficiaryAccountSuffix: '6789',
  payingBankCode: 'BNI', payingAccountSuffix: '4321', transferDate: '2026-10-01', transferReference: 'TRANSFER-REFUND-01' };
const input = { bankImportId: '9007199254740998', bankRowNumber: 3, beneficiaryVerificationReference: 'Penerima dicocokkan dengan bukti asli', transferVerificationReference: 'Debit dicocokkan dengan mutasi asli' };
const preview = { binding: 'e'.repeat(64), view, input, debit: { bankImportId: input.bankImportId, bankRowNumber: input.bankRowNumber, transactionId: 'BANK-REFUND-1', bookedDate: '2026-10-01', amount: payout.amount, accountDisplay: '4321', importedBy: '110' },
  policy: { bankDebitVerificationProcedureReference: 'Prosedur debit bank disetujui', beneficiaryVerificationProcedureReference: 'Prosedur penerima disetujui' },
  receipts: [{ receiptId: '111', versionBefore: '8', versionAfter: '9', paidBefore: '100.00', paidAfter: '500.01', pending: '100.00', reservedForRefund: '600.01', available: '200.00', received: '1000.01', allocated: '200.00' },
    { receiptId: '112', versionBefore: '5', versionAfter: '6', paidBefore: '0.00', paidAfter: '500.01', pending: '0.00', reservedForRefund: '500.01', available: '0.00', received: '700.01', allocated: '200.00' }] };
const saved = { settlementId: '113', payoutId, financialApprovalId: payout.approvalId, caseId, returnId, invoiceId, amount: payout.amount,
  operation: 'billing.refund.payout.verify' as const, state: 'bank_debit_verified' as const, recordedAt: '2026-10-01T03:11:00Z',
  receipts: preview.receipts.map(row => ({ receiptId: row.receiptId, version: row.versionAfter, paid: row.paidAfter, pending: row.pending, reservedForRefund: row.reservedForRefund, available: row.available })) };
const command = () => prepareRefundSettlement(actor, owner, view, preview, 'Pemeriksaan debit bank independen', view.evidence.sha256);
function fixture() {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (name: string) => values.get(name) ?? null, setItem: (name: string, value: string) => { values.set(name, value); }, removeItem: (name: string) => { values.delete(name); } };
  return { values, journal: new OperationJournal(() => storage) };
}

test('independent verifier requires its own grants and never accepts a recorder or a paid claim in the original payout', () => {
  const grants = ['disbursement.verify', 'case.read', 'invoice.read', 'payment.review.read']; assert.equal(canVerifyRefundSettlement(grants), true);
  for (const grant of grants) assert.equal(canVerifyRefundSettlement(grants.filter(value => value !== grant)), false);
  assert.equal(canVerifyRefundSettlement(['disbursement.record', 'case.read', 'invoice.read', 'payment.review.read']), false);
  assert.deepEqual(parseRefundSettlementView(view, owner, payoutId), view);
  for (const changed of [{ ...view, caseId: '1' }, { ...view, invoiceId: '1' }, { ...view, amount: '900.03' }, { ...view, payout: { ...payout, state: 'paid' } },
    { ...view, beneficiaryAccountNumber: '123456789' }, { ...view, evidence: { ...view.evidence, version: '2' } }, { ...view, settlement: { ...saved, payoutId: '1' } }])
    assert.throws(() => parseRefundSettlementView(changed, owner, payoutId));
  assert.deepEqual(parseRefundSettlementView({ ...view, settlement: saved }, owner, payoutId).settlement, saved);
  for (const changed of [key + '\n', key.replace('rsv.', 'rpr.'), key.replace(payoutId, '9223372036854775808'), key.replace('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '00000000-0000-0000-0000-000000000000')]) assert.throws(() => refundSettlementOwner(changed));
  assert.deepEqual(parseRefundSettlementResult(saved, key), saved); assert.throws(() => parseRefundSettlementResult({ ...saved, payoutId: '1' }, key));
});

test('frozen debit review conserves exact cents, blocked funds, changed versions and reviewed original without permitting synthetic account inputs', () => {
  const prepared = command(); assert.ok(Object.isFrozen(prepared)); assert.ok(Object.isFrozen(prepared.body.input)); assert.ok(Object.isFrozen(prepared.expected)); assert.ok(Object.isFrozen(prepared.expected[0]));
  assert.deepEqual(prepared.body.input, input); assert.deepEqual(prepared.expected, saved.receipts);
  for (const changed of [{ ...preview, input: { ...input, bankImportId: '1' } }, { ...preview, debit: { ...preview.debit, amount: '900.03' } },
    { ...preview, debit: { ...preview.debit, accountDisplay: '9999' } }, { ...preview, receipts: [{ ...preview.receipts[0], available: '200.01' }, preview.receipts[1]] },
    { ...preview, receipts: [{ ...preview.receipts[0], paidAfter: '500.00', pending: '100.01' }, preview.receipts[1]] },
    { ...preview, receipts: [{ ...preview.receipts[0], versionAfter: '8' }, preview.receipts[1]] }, { ...preview, receipts: preview.receipts.toReversed() }])
    assert.throws(() => prepareRefundSettlement(actor, owner, view, changed, 'Pemeriksaan', view.evidence.sha256));
  assert.throws(() => prepareRefundSettlement(actor, owner, view, preview, 'Pemeriksaan', 'f'.repeat(64)), RequestNotSentError);
  for (const changed of [{ ...input, bankRowNumber: 1 }, { ...input, bankRowNumber: 502 }, { ...input, bankRowNumber: 2.5 }, { ...input, bankImportId: '0' },
    { ...input, beneficiaryVerificationReference: '' }, { ...input, payingAccountId: '1' }]) assert.throws(() => refundSettlementInput(changed), RequestNotSentError);
});

test('actual transport verifies only explicit debit references with actor and CSRF and validates exact saved receipt changes without retry', async () => {
  const requests: Request[] = [], prepared = command(); const gateway = createRefundSettlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    const actual = request as Request; if (actual.url.endsWith('/csrf')) return Response.json({ requestToken: 'memory-only-token' }); requests.push(actual);
    assert.equal(actual.headers.get('X-Expected-Actor-Id'), actor); assert.equal(actual.headers.get('X-CSRF-Token'), 'memory-only-token'); assert.equal(actual.cache, 'no-store'); assert.equal(actual.redirect, 'error'); assert.equal(actual.credentials, 'same-origin');
    if (actual.url.endsWith('/settlement/query')) { assert.deepEqual(await actual.json(), { purpose: 'refund-settlement' }); return Response.json(view); }
    if (actual.url.endsWith('/settlement/preview')) { assert.deepEqual(await actual.json(), input); return Response.json(preview); }
    assert.equal(actual.url.endsWith('/settlement/verify'), true); assert.equal(actual.headers.get('Idempotency-Key'), key); assert.deepEqual(await actual.json(), prepared.body); return Response.json(saved);
  } }));
  const read = await gateway.read(actor, owner, payoutId); assert.deepEqual(await gateway.preview(actor, owner, read, input), preview); assert.deepEqual(await gateway.verify(prepared, key, () => true), saved);
  await assert.rejects(gateway.verify(prepared, key, () => false), RequestNotSentError); assert.equal(requests.length, 3);
});

test('unknown verification response keeps only metadata and exact payout recovery; absence cannot close or repeat the transfer', async () => {
  const { journal, values } = fixture(), ledger = new MutationLedger(() => key); let sends = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'billing.refund.payout.verify', 'billing.refund.payout.verify', command(), async () => { sends++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], stored = [...values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(stored)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [input.bankImportId, input.beneficiaryVerificationReference, view.evidence.sha256, command().body.reason]) assert.equal(stored.includes(privateValue), false);
  const legacy = { lookup: async () => null, close: async () => null };
  assert.equal(operationRecoveryCapabilities(entry.operation).canCheck, false);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'billing.refund.payout.verify': { lookup: async () => null } }), { state: 'unconfirmed' }); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'billing.refund.payout.verify': { lookup: async () => ({ state: 'committed', result: { ...saved, payoutId: '1' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'billing.refund.payout.verify': { lookup: async () => ({ state: 'committed', result: saved }) } }), { state: 'committed', result: { id: saved.settlementId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(sends, 1); assert.equal(operationRecoveryCapabilities(entry.operation, { 'billing.refund.payout.verify': { lookup: async () => null } }).canClose, false);
  assert.equal(operationLabel(entry.operation), 'Verifikasi debit bank refund'); assert.equal(recoveryPage(entry.operation).href, '/fulfillment/cases');
});

test('independent original viewer uses its own purpose and exact original hash; stale viewer sends nothing', async () => {
  const bytes = new Uint8Array([1, 2, 3]), digest = await crypto.subtle.digest('SHA-256', bytes), sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join(''); let sends = 0;
  const gateway = createRefundSettlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    const actual = request as Request; if (actual.url.endsWith('/csrf')) return Response.json({ requestToken: 'token' }); sends++;
    assert.equal(actual.url.endsWith('/settlement/evidence/107/original/query'), true); assert.deepEqual(await actual.json(), { purpose: 'refund-settlement-evidence-view', reference: { ...view.evidence, sha256 } });
    return new Response(bytes, { headers: { 'Content-Type': 'image/png', 'Content-Length': '3' } });
  } }));
  const actual = { ...view, evidence: { ...view.evidence, sha256 } }; assert.equal((await gateway.original(actor, actual, () => true)).size, 3);
  await assert.rejects(gateway.original(actor, actual, () => false), RequestNotSentError); assert.equal(sends, 1);
});
