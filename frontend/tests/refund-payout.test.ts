import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { canRecordRefundPayout, createRefundPayoutGateway, parseRefundPayoutDraftResult, parseRefundPayoutResult, parseRefundPayoutView,
  prepareRefundPayoutDraft, prepareRefundPayoutRecord, refundPayoutOwner } from '../src/modules/fulfillment/refund-payout-client';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { operationLabel, recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actor = '9007199254740993', caseId = '9223372036854775807', invoiceId = '9007199254740994', returnId = '9007199254740996', approvalId = '9007199254741000', draftId = '9007199254741001';
const suffix = '.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', draftKey = 'rpd.' + approvalId + suffix, recordKey = 'rpr.' + draftId + suffix, reserveKey = 'rpe.' + draftId + suffix;
const reported = [{ receiptId: '93', skuId: '94', reportedBaseUnits: '2' }];
const source = { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
  lines: [{ orderId: '91', shipmentId: '92', receiptId: '93', skuId: '94', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6' }] };
const owner = { id: caseId, revision: '2' as const, requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized' as const, issue: 'damaged' as const, reportedProblem: 'Barang rusak', source, lines: reported,
  decision: { caseId, invoiceId, revision: '2' as const, binding: 'b'.repeat(64), state: 'return_authorized' as const, decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Pengembalian untuk pemeriksaan', lines: reported } };
const approval = { financialApprovalId: approvalId, caseId, returnId, invoiceId, action: 'refund' as const, invoiceVersion: '8', credit: '1000.02', reservedRefund: '900.00', remainingInvoice: '4900.10',
  receipts: [{ receiptId: '9007199254740997', version: '4', released: '900.00', reservedForRefund: '1000.00', available: '100.00' }],
  releases: [{ originalAllocationId: '9007199254740998', invoiceId, amount: '400.00' }, { originalAllocationId: '9007199254740999', invoiceId, amount: '500.00' }],
  recordedAt: '2026-10-01T03:07:00Z', operation: 'billing.return.financial.approve' as const, state: 'refund_reserved' as const };
const input = { beneficiaryId: '100', expectedBeneficiaryRevision: '1', payingAccountId: '101', payingAccountVersionId: '102', amount: '900.00', transferDate: '2026-10-01', transferReference: 'TRANSFER-REFUND-01' };
const view = { approvalId, caseId, returnId, invoiceId, amount: input.amount, beneficiaryId: input.beneficiaryId, beneficiaryRevision: input.expectedBeneficiaryRevision,
  beneficiaryName: 'Koperasi Cabang Tebet', beneficiaryBankCode: 'BNI', beneficiaryAccountSuffix: '6789', payingAccountId: input.payingAccountId, payingAccountVersionId: input.payingAccountVersionId,
  payingBankCode: 'BNI', payingAccountSuffix: '4321', payoutRecordingProcedureReference: 'Prosedur bukti refund disetujui', beneficiaryVerificationProcedureReference: 'Prosedur verifikasi penerima independen', input,
  payout: null, draft: null, evidence: null, reservationId: null, settlement: null };
const preview = { view, binding: 'c'.repeat(64) }, draft = { draftId, revision: '1' as const, approvalId, state: 'awaiting_private_evidence' as const, amount: input.amount, createdAt: '2026-10-01T03:08:00Z' };
const evidence = { reservationId: '103', revision: '5', expiresAt: '2026-10-02T03:08:00Z', state: 'quarantined' as const,
  reference: { id: '104', version: '1' as const, sha256: 'd'.repeat(64) }, scanStatus: 'clean' as const, decoderStatus: 'decoded' as const, safeFailure: null };
const savedView = { ...view, draft }, recordPreview = { view: { ...savedView, reservationId: evidence.reservationId, evidence: evidence.reference }, binding: 'e'.repeat(64) };
const payout = { payoutId: '105', draftId, approvalId, state: 'recorded_unverified' as const, amount: input.amount, recordedAt: '2026-10-01T03:09:00Z' };
const command = () => prepareRefundPayoutRecord(actor, owner, approval, recordPreview, evidence, 'Bukti transfer privat untuk pemeriksaan bank independen');
function fixture() {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null, getItem: (name: string) => values.get(name) ?? null,
    setItem: (name: string, value: string) => { values.set(name, value); }, removeItem: (name: string) => { values.delete(name); } };
  return { values, journal: new OperationJournal(() => storage) };
}
test('recorder grants and actual owner reject approval-maker grants, changed source values and paid claims', () => {
  const grants = ['disbursement.record', 'case.read', 'invoice.read', 'payment.review.read']; assert.equal(canRecordRefundPayout(grants), true);
  for (const grant of grants) assert.equal(canRecordRefundPayout(grants.filter(value => value !== grant)), false);
  assert.equal(canRecordRefundPayout(['case.read', 'invoice.read', 'credit.approve', 'refund.approve', 'payment.review.read']), false);
  assert.deepEqual(parseRefundPayoutView(view, owner, approval), view);
  for (const changed of [{ ...view, caseId: '1' }, { ...view, returnId: '1' }, { ...view, invoiceId: '1' }, { ...view, approvalId: '1' }, { ...view, amount: '900.01' },
    { ...view, input: { ...input, amount: '900.01' } }, { ...view, input: { ...input, payingAccountVersionId: '1' } }, { ...view, beneficiaryAccountNumber: '123456789' },
    { ...view, beneficiaryAccountSuffix: '123456789' }, { ...view, draft: { ...draft, createdAt: source.issuedAt } }, { ...savedView, payout: { ...payout, state: 'paid' } },
    { ...savedView, payout: { ...payout, draftId: '1' } }, { ...view, reservationId: evidence.reservationId }]) assert.throws(() => parseRefundPayoutView(changed, owner, approval));
  assert.throws(() => parseRefundPayoutView(view, owner, { ...approval, action: 'credit' }));
  for (const changed of [draftKey + '\n', draftKey.replace('rpd.', 'rpr.'), draftKey.replace(approvalId, '9223372036854775808'), draftKey.replace('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '00000000-0000-0000-0000-000000000000')]) assert.throws(() => refundPayoutOwner(changed, 'draft'));
  assert.deepEqual(parseRefundPayoutDraftResult(draft, draftKey), draft); assert.deepEqual(parseRefundPayoutResult(payout, recordKey), payout);
  assert.throws(() => parseRefundPayoutResult({ ...payout, draftId: '1' }, recordKey));
});
test('frozen draft and record bind exact reserved money, source IDs and clean reviewed original without default transfer routes', () => {
  const preparedDraft = prepareRefundPayoutDraft(actor, owner, approval, preview, 'Draf bukti refund privat'), prepared = command();
  assert.ok(Object.isFrozen(preparedDraft.body.input)); assert.ok(Object.isFrozen(prepared.body.reference)); assert.ok(Object.isFrozen(prepared));
  assert.deepEqual(preparedDraft.body.input, input); assert.equal(prepared.amount, approval.reservedRefund); assert.equal(prepared.body.reference.sha256, evidence.reference.sha256);
  for (const changed of [{ ...evidence, scanStatus: 'unavailable' }, { ...evidence, scanStatus: 'infected' }, { ...evidence, decoderStatus: 'invalid' }, { ...evidence, safeFailure: 'invalid_media' },
    { ...evidence, reference: null }, { ...evidence, reservationId: '999' }, { ...evidence, reference: { ...evidence.reference, sha256: 'f'.repeat(64) } }])
    assert.throws(() => prepareRefundPayoutRecord(actor, owner, approval, recordPreview, changed as never, 'Pemeriksaan'));
  for (const changed of [{ ...preview, binding: '' }, { ...preview, view: savedView }, { ...preview, view: { ...view, amount: '899.99' } }]) assert.throws(() => prepareRefundPayoutDraft(actor, owner, approval, changed, 'Pemeriksaan'));
  for (const reason of ['', ' ', 'zero\u200bwidth', 'line\nfeed', 'x'.repeat(1001)]) assert.throws(() => prepareRefundPayoutRecord(actor, owner, approval, recordPreview, evidence, reason));
});
test('actual transport supplies CSRF and expected actor, queries only manual transfer fields and validates exact result without retry', async () => {
  const sent: Request[] = []; const gateway = createRefundPayoutGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    const actual = request as Request; if (actual.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf-memory-only' }); sent.push(actual);
    assert.equal(actual.headers.get('X-CSRF-Token'), 'csrf-memory-only'); assert.equal(actual.headers.get('X-Expected-Actor-Id'), actor); assert.equal(actual.credentials, 'same-origin'); assert.equal(actual.cache, 'no-store'); assert.equal(actual.redirect, 'error');
    if (actual.url.endsWith('/payout-drafts/preview')) { assert.deepEqual(await actual.json(), { transferDate: input.transferDate, transferReference: input.transferReference }); return Response.json(preview); }
    if (actual.url.endsWith('/payout-drafts')) return Response.json(draft);
    assert.equal(actual.headers.get('If-Match'), '"1"'); assert.equal(actual.headers.get('Idempotency-Key'), recordKey); assert.deepEqual(await actual.json(), command().body); return Response.json(payout);
  } }));
  const shown = await gateway.previewDraft(actor, owner, approval, input.transferDate, input.transferReference);
  assert.deepEqual(await gateway.createDraft(prepareRefundPayoutDraft(actor, owner, approval, shown, 'Draf bukti'), draftKey, () => true), draft);
  assert.deepEqual(await gateway.record(command(), recordKey, () => true), payout); assert.equal(sent.length, 3);
  await assert.rejects(gateway.record(command(), recordKey, () => false), RequestNotSentError); assert.equal(sent.length, 3);
});
test('lost record response retains only actor-operation-owner key and recovers actual unverified receipt; absence cannot clear attempt', async () => {
  const { values, journal } = fixture(), ledger = new MutationLedger(() => recordKey); let sends = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'billing.refund.payout.record', 'billing.refund.payout.record', command(), async () => { sends++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0]; assert.deepEqual(Object.keys(JSON.parse([...values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const secret of [input.transferReference, evidence.reference.sha256, command().body.reason, input.amount]) assert.equal([...values.values()].some(value => value.includes(secret)), false);
  const legacy = { lookup: async () => null, close: async () => null }, absent = { 'billing.refund.payout': { lookup: async () => null } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, absent), { state: 'unconfirmed' }); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'billing.refund.payout': { lookup: async () => ({ state: 'committed', result: { ...payout, draftId: '1' } }) } })); assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'billing.refund.payout': { lookup: async () => ({ state: 'committed', result: payout }) } }), { state: 'committed', result: { id: payout.payoutId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(sends, 1); assert.equal(operationLabel(entry.operation), 'Pencatatan refund belum diverifikasi'); assert.equal(recoveryPage(entry.operation).href, '/fulfillment/cases');
});
test('draft and evidence reservation recover through their own exact owner paths, never record adapter or automatic close', async () => {
  const { journal } = fixture(), reserve = { reservationId: evidence.reservationId, revision: '1' as const, expiresAt: evidence.expiresAt };
  const draftEntry = journal.remember(actor, 'billing.refund.payout.draft', draftKey), reservationEntry = journal.remember(actor, 'evidence.refund-payout-image.reserve', reserveKey);
  const legacy = { lookup: async () => null, close: async () => null };
  assert.deepEqual(await reconcileOperationRecovery(draftEntry, journal, legacy, { 'billing.refund.payout': { lookup: async () => ({ state: 'committed', result: draft }) } }), { state: 'committed', result: { id: draftId, version: '1' } });
  assert.deepEqual(await reconcileOperationRecovery(reservationEntry, journal, legacy, { 'evidence.refund-payout-image': { lookup: async () => ({ state: 'committed', result: reserve }) } }), { state: 'committed', result: { id: reserve.reservationId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(operationRecoveryCapabilities(reservationEntry.operation, { 'evidence.refund-payout-image': { lookup: async () => null } }).canClose, false);
});
test('original image view validates authenticated bytes, exact MIME and hash while invalid file never reaches transport', async () => {
  const bytes = new Uint8Array([1, 2, 3]), digest = await crypto.subtle.digest('SHA-256', bytes), sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
  let count = 0; const gateway = createRefundPayoutGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    if ((request as Request).url.endsWith('/csrf')) return Response.json({ requestToken: 'token' }); count++; return new Response(bytes, { headers: { 'Content-Type': 'image/png', 'Content-Length': '3' } });
  } }));
  assert.equal((await gateway.original(actor, draftId, '1', { ...evidence, reference: { ...evidence.reference, sha256 } }, () => true)).size, 3);
  await assert.rejects(gateway.original(actor, draftId, '1', evidence, () => true));
  await assert.rejects(gateway.upload(actor, draftId, '103', '1', new File(['not-image'], 'file.txt', { type: 'text/plain' }), () => true), RequestNotSentError); assert.equal(count, 2);
});

test('recorder summary accepts only actual bank-debit settlement matching the recorded payout and source chronology', async () => {
  const settlement = { settlementId: '106', payoutId: payout.payoutId, financialApprovalId: approvalId, caseId, returnId, invoiceId, amount: payout.amount,
    operation: 'billing.refund.payout.verify' as const, state: 'bank_debit_verified' as const, recordedAt: '2026-10-01T03:10:00Z',
    receipts: [{ receiptId: approval.receipts[0].receiptId, version: '5', paid: '900.00', pending: '100.00', reservedForRefund: '1000.00', available: '100.00' }] };
  const recorded = { ...recordPreview.view, payout, settlement };
  assert.deepEqual(parseRefundPayoutView(recorded, owner, approval).settlement, settlement); assert.equal(recorded.payout.state, 'recorded_unverified');
  for (const changed of [{ ...recorded, payout: null }, { ...recorded, settlement: { ...settlement, payoutId: '1' } }, { ...recorded, settlement: { ...settlement, financialApprovalId: '1' } },
    { ...recorded, settlement: { ...settlement, caseId: '1' } }, { ...recorded, settlement: { ...settlement, returnId: '1' } }, { ...recorded, settlement: { ...settlement, invoiceId: '1' } },
    { ...recorded, settlement: { ...settlement, amount: '899.99' } }, { ...recorded, settlement: { ...settlement, state: 'paid' } },
    { ...recorded, settlement: { ...settlement, recordedAt: '2026-10-01T03:08:00Z' } }, { ...recorded, settlement: { ...settlement, receipts: [{ ...settlement.receipts[0], pending: '99.99' }] } }])
    assert.throws(() => parseRefundPayoutView(changed, owner, approval));
  let response: unknown = { approval, payout, draft, settlement };
  const gateway = createRefundPayoutGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request =>
    (request as Request).url.endsWith('/csrf') ? Response.json({ requestToken: 'token' }) : Response.json(response) }));
  assert.deepEqual((await gateway.approved(actor, owner, returnId)).settlement, settlement);
  response = { approval, payout: null, draft, settlement }; await assert.rejects(gateway.approved(actor, owner, returnId));
  response = { approval, payout, draft, settlement: { ...settlement, returnId: '1' } }; await assert.rejects(gateway.approved(actor, owner, returnId));
  response = { approval, payout, draft, settlement: { ...settlement, recordedAt: '2026-10-01T03:08:00Z' } }; await assert.rejects(gateway.approved(actor, owner, returnId));
});
