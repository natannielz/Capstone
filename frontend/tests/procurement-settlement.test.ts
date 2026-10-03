import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createProcurementSettlementGateway, parseProcurementSettlementChoices, parseProcurementSettlementOptions, parseProcurementSettlementRead, parseProcurementSettlementResult, prepareProcurementSettlement, procurementSettlementInput, procurementSettlementOwner, settlementBusinessDate, settlementOriginalKey, settlementPolicyCurrent } from '../src/modules/procurement/procurement-settlement-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', disbursementId = '9007199254740994', key = `ps.verify.${disbursementId}.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`;
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const supplier = { id: '600', versionId: '601', version: '2', subjectReference: '00SUP-1', displayName: 'Sumber Kertas' };
const authorization = { id: '700', requestId: '701', decisionId: '702', orderId: '703', orderVersion: '1' as const, orderApprovalId: '704', route: 'ddo' as const, supplier, authorizedAmount: '100.00', businessDate: '2026-10-03', neededBy: '2026-10-04', approvedAt: '2026-10-03T01:00:00.000Z' };
const beneficiary = { supplierId: '600', beneficiaryId: '610', versionId: '611', version: '2', bankCode: 'BNI', maskedAccount: '****0001', currency: 'IDR' as const };
const payingAccount = { id: '620', versionId: '621', version: '3', bankCode: 'BNI', bankName: 'Bank BNI', maskedAccount: '****0002', currency: 'IDR' as const };
const original = { draftId: '900', draftRevision: '1' as const, reservationId: '901', reference: { id: '902', version: '3', sha256: 'a'.repeat(64) } };
const disbursement = { id: disbursementId, version: '1' as const, draftId: '900', location, authorization, amount: '30.01', transferDate: '2026-10-03', transferReference: '000TRANSFER-Ab', recordedAt: '2026-10-03T01:02:00.000Z', beneficiary, payingAccount, original };
const policy = { id: '800', location, revision: '1', active: true, approvedAt: '2026-10-01T00:00:00.000Z', expiresAt: '2026-11-01T00:00:00.000Z', rule: 'independent_exact_bank_debit' as const, allowReferenceOnly: true, payingAccountId: '620', payingAccountVersionId: '621', currency: 'IDR' as const, bookId: '810', chartVersionId: '811', supplierAdvanceMapping: 'supplier-advance', supplierAdvanceAccountId: '812', supplierAdvanceAccountCode: 'AADV', cashMapping: 'cash', cashAccountId: '813', cashAccountCode: 'ABANK', bankDebitVerificationProcedureReference: 'Debit SOP001', beneficiaryVerificationProcedureReference: 'Penerima SOP001', trustedBankChannelReference: 'Bank channel001', operatingOwnerReference: 'Finance owner001', approvalReference: 'Approval001' };
const input = { policyId: '800', policyRevision: '1', bankImportId: '950', bankRowNumber: 2, observedBeneficiary: { bankCode: 'BNI', accountNumber: '00000001', holderName: 'PT Sumber Kertas', currency: 'IDR' as const }, beneficiaryVerificationReference: 'Bank recipient observation001', transferVerificationReference: 'Bank debit observation001' };
const debit = { importId: '950', rowNumber: 2, transactionId: '000BANK-TX', bookedDate: '2026-10-03', amount: '30.01', direction: 'debit' as const, reference: '000TRANSFER-Ab' };
const accounting = { bookId: '810', chartVersionId: '811', periodId: '814', periodMonth: '2026-10', supplierAdvance: { mappingKey: 'supplier-advance', accountId: '812', accountCode: 'AADV' }, cash: { mappingKey: 'cash', accountId: '813', accountCode: 'ABANK' } };
const preview = { disbursement, input, policy, debit, accounting, postingDate: '2026-10-03', binding: 'b'.repeat(64), observedAt: '2026-10-03T01:03:00.000Z' };
const options = { disbursement, items: [policy] }, view = { disbursement, settlement: null };
const rows = { disbursementId, importId: '950', payingAccountId: '620', payingAccountVersionId: '621', items: [{ rowNumber: 2, transactionId: debit.transactionId, bookedDate: debit.bookedDate, amount: debit.amount, direction: debit.direction, reference: debit.reference, eligible: true, issue: null }] };
const imports = { disbursementId, payingAccountId: '620', payingAccountVersionId: '621', items: [{ id: '950', recordedAt: '2026-10-03T01:02:30.000Z', rowCount: 1, filename: 'statement-0001.csv', profileName: 'BNI corporate debit' }], nextImportId: null };
const result = { id: '960', disbursementId, amount: '30.01', status: 'bank_debit_verified' as const, recordedAt: '2026-10-03T01:04:00.000Z', postingDate: '2026-10-03', journalId: '961' };
const settled = { ...result, debit, policy, accounting, beneficiaryVerificationReference: input.beneficiaryVerificationReference, transferVerificationReference: input.transferVerificationReference, reason: 'Pemeriksaan independen atas sumber bank' };
const command = () => prepareProcurementSettlement(actor, view, options, policy, rows, input, preview, settled.reason, settlementOriginalKey(disbursement));

test('independent observation preserves exact bank strings and rejects missing observations, hidden characters, numeric IDs and invented commercial fields', () => {
  assert.deepEqual(procurementSettlementInput(input), input); assert.equal(command().body.input.observedBeneficiary.accountNumber, '00000001');
  assert.equal(command().amount, '30.01'); assert.ok(Object.isFrozen(command().body.input.observedBeneficiary));
  for (const changed of [{ ...input, bankImportId: 950 }, { ...input, bankRowNumber: 1 }, { ...input, amount: '29.00' }, { ...input, observedBeneficiary: { ...input.observedBeneficiary, accountNumber: '1' } }, { ...input, observedBeneficiary: { ...input.observedBeneficiary, holderName: '' } }]) assert.throws(() => procurementSettlementInput(changed), RequestNotSentError);
  for (const hidden of ['\u0000', '\u200d', '\ufeff']) {
    assert.throws(() => procurementSettlementInput({ ...input, beneficiaryVerificationReference: 'REF' + hidden + '001' }), RequestNotSentError);
    assert.throws(() => procurementSettlementInput({ ...input, observedBeneficiary: { ...input.observedBeneficiary, holderName: 'Name' + hidden } }), RequestNotSentError);
  }
  assert.equal(procurementSettlementInput({ ...input, transferVerificationReference: '000Cafe\u0301' }).transferVerificationReference, '000Cafe\u0301');
});

test('frozen verification binds exact source, policy, independent input, eligible debit and viewed original tuple', () => {
  for (const shown of [{ ...preview, disbursement: { ...disbursement, amount: '30.00' } }, { ...preview, debit: { ...debit, amount: '30.00' } }, { ...preview, debit: { ...debit, transactionId: 'FOREIGN' } }, { ...preview, input: { ...input, observedBeneficiary: { ...input.observedBeneficiary, accountNumber: '99999999' } } }, { ...preview, accounting: { ...accounting, cash: { ...accounting.cash, accountId: '999' } } }, { ...preview, postingDate: '2026-10-04' }]) assert.throws(() => prepareProcurementSettlement(actor, view, options, policy, rows, input, shown, settled.reason, settlementOriginalKey(disbursement)));
  assert.throws(() => prepareProcurementSettlement(actor, view, options, policy, rows, input, preview, settled.reason, ''));
  assert.throws(() => prepareProcurementSettlement(actor, view, options, policy, { ...rows, payingAccountVersionId: '999' }, input, preview, settled.reason, settlementOriginalKey(disbursement)));
  assert.throws(() => prepareProcurementSettlement(actor, view, options, policy, { ...rows, items: [{ ...rows.items[0], eligible: false, issue: 'already_claimed' }] }, input, preview, settled.reason, settlementOriginalKey(disbursement)));
  assert.throws(() => prepareProcurementSettlement(actor, { disbursement, settlement: settled }, options, policy, rows, input, preview, settled.reason, settlementOriginalKey(disbursement)));
  for (const changed of [{ ...policy, allowReferenceOnly: false }, { ...policy, active: false }, { ...policy, expiresAt: preview.observedAt }]) assert.throws(() => prepareProcurementSettlement(actor, view, { ...options, items: [changed] }, changed, rows, input, { ...preview, policy: changed }, settled.reason, settlementOriginalKey(disbursement)));
  assert.equal(settlementPolicyCurrent(policy, Date.parse(policy.expiresAt)), false); assert.equal(settlementBusinessDate(Date.parse('2026-10-03T17:00:00.000Z')), '2026-10-04');
});

test('scoped read projections reject leaked full bank observations and inconsistent records while preserving positive evidence versions', () => {
  assert.equal(parseProcurementSettlementRead(view).disbursement.original.reference.version, '3'); assert.deepEqual(parseProcurementSettlementRead({ disbursement, settlement: settled }).settlement, settled);
  for (const malformed of [{ ...view, disbursement: { ...disbursement, original: { ...original, draftId: '999' } } }, { ...view, disbursement: { ...disbursement, beneficiary: { ...beneficiary, accountNumber: '00000001' } } }, { ...view, disbursement: { ...disbursement, original: { ...original, reference: { ...original.reference, version: '0' } } } }, { disbursement, settlement: { ...settled, observedBeneficiary: input.observedBeneficiary } }, { disbursement, settlement: { ...settled, disbursementId: '999' } }]) assert.throws(() => parseProcurementSettlementRead(malformed), AccessFailure);
  assert.throws(() => parseProcurementSettlementChoices({ location: { ...location, shopId: '999' }, items: [view], nextDisbursementId: null }), AccessFailure);
  for (const changed of [{ ...policy, payingAccountVersionId: '999' }, { ...policy, supplierAdvanceMapping: 'cash' }, { ...policy, supplierAdvanceAccountId: policy.cashAccountId }]) assert.throws(() => parseProcurementSettlementOptions({ disbursement, items: [changed] }));
  assert.throws(() => parseProcurementSettlementResult({ ...result, disbursementId: '999' }, key)); assert.throws(() => procurementSettlementOwner('ps.verify.01.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'));
});

test('named bank imports and debit selectors retain exact owning account and reject foreign or falsely eligible choices', async () => {
  const calls: Request[] = []; let foreign = false, invalidDebit = false, zeroDebit = false;
  const gateway = createProcurementSettlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async sent => {
    assert.ok(sent instanceof Request); if (sent.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls.push(sent); assert.equal(sent.headers.get('X-Expected-Actor-Id'), actor); assert.equal(sent.headers.get('Idempotency-Key'), null);
    if (sent.url.endsWith('/bank-imports/query')) { assert.deepEqual(await sent.json(), { purpose: 'settlement-review', beforeId: null }); return Response.json(foreign ? { ...imports, payingAccountVersionId: '999' } : imports); }
    assert.match(sent.url, /\/bank-imports\/950\/debits\/query$/); assert.deepEqual(await sent.json(), { purpose: 'settlement-review' }); return Response.json(zeroDebit ? { ...rows, items: [{ ...rows.items[0], amount: '0.00', eligible: false, issue: 'amount_mismatch' }] } : invalidDebit ? { ...rows, items: [{ ...rows.items[0], amount: '30.00' }] } : rows);
  } }));
  assert.equal((await gateway.imports(actor, disbursement)).items[0].filename, 'statement-0001.csv'); assert.deepEqual(await gateway.debits(actor, disbursement, '950'), rows);
  foreign = true; await assert.rejects(gateway.imports(actor, disbursement), AccessFailure); invalidDebit = true; await assert.rejects(gateway.debits(actor, disbursement, '950'), AccessFailure);
  zeroDebit = true; assert.equal((await gateway.debits(actor, disbursement, '950')).items[0].eligible, false); assert.equal(calls.length, 5);
});

test('original viewer reads only the pinned private image with exact revision, length, MIME and digest', async () => {
  const bytes = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join('');
  const source = { ...disbursement, original: { ...original, reference: { ...original.reference, sha256: hash } } }; let variant = 'valid', originalCalls = 0;
  const gateway = createProcurementSettlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async sent => {
    assert.ok(sent instanceof Request); if (sent.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); originalCalls++; assert.match(sent.url, /\/disbursements\/drafts\/900\/evidence\/901\/original\/query$/); assert.equal(sent.headers.get('If-Match'), '"1"'); assert.equal(sent.headers.get('X-Expected-Actor-Id'), actor); assert.equal(sent.headers.get('Range'), null); assert.deepEqual(await sent.json(), { purpose: 'procurement-disbursement-evidence-view', reference: source.original.reference });
    return new Response(variant === 'digest' ? new Uint8Array(8) : bytes, { headers: { 'content-type': variant === 'mime' ? 'text/html' : 'image/png', 'content-length': variant === 'length' ? '9' : '8' } });
  } }));
  assert.equal((await gateway.original(actor, source, () => true)).size, 8);
  for (const bad of ['digest', 'mime', 'length']) { variant = bad; await assert.rejects(gateway.original(actor, source, () => true), AccessFailure); }
  await assert.rejects(gateway.original(actor, source, () => false), RequestNotSentError); assert.equal(originalCalls, 4);
});

test('lost verification result retains only original actor and key; lookup cannot close or resend and requires immutable read-back', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key); let writes = 0, lookups = 0, readBacks = 0, foreign = false;
  const gateway = createProcurementSettlementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async sent => {
    assert.ok(sent instanceof Request); if (sent.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); assert.equal(sent.headers.get('X-Expected-Actor-Id'), actor);
    if (sent.url.endsWith('/verify')) { writes++; assert.equal(sent.headers.get('Idempotency-Key'), key); assert.equal(sent.headers.get('If-Match'), '"1"'); assert.deepEqual(await sent.json(), command().body); throw new TypeError('Committed response lost'); }
    assert.equal(sent.headers.get('Idempotency-Key'), null);
    if (sent.url.endsWith('/operations/query')) { lookups++; assert.deepEqual(await sent.json(), { purpose: 'operation-recovery', operation: 'procurement.settlement.verify', key }); return Response.json({ status: 'confirmed', result }); }
    readBacks++; assert.match(sent.url, new RegExp('/disbursements/' + disbursementId + '/query$')); assert.deepEqual(await sent.json(), { purpose: 'settlement-review' }); return Response.json({ disbursement, settlement: foreign ? { ...settled, journalId: '999' } : settled });
  } }));
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'settlement', 'procurement.settlement.verify', command().body, originalKey => gateway.verify(command(), originalKey, () => true)));
  const entry = journal.list(actor)[0]; assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(entry.key, key); assert.equal(journal.list('999').length, 0);
  for (const secret of [input.observedBeneficiary.accountNumber, input.observedBeneficiary.holderName, input.beneficiaryVerificationReference, original.reference.sha256]) assert.ok(!JSON.stringify(entry).includes(secret));
  const adapter = { 'procurement.settlement': { lookup: (item: typeof entry) => gateway.recover(item.actorId, item.key) } }, legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.equal(operationRecoveryCapabilities(entry.operation, adapter).canClose, false); await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapter, 'Close attempt')); assert.equal(lookups, 0);
  foreign = true; await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapter)); assert.equal(journal.list(actor).length, 1);
  foreign = false; assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapter), { state: 'committed', result: { id: result.id, version: '1' } }); assert.equal(journal.list(actor).length, 0); assert.equal(writes, 1); assert.equal(lookups, 2); assert.equal(readBacks, 2);
});

test('settlement task and safe return paths require actual scoped read discovery; step-up retains verification operation', () => {
  const path = '/procurement/settlements'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path }); assert.equal(identityDestinations(['procurement.settlement.read']).some(item => item.href === path), true);
  for (const permission of ['procurement.read', 'procurement.disbursement.read', 'disbursement.record', 'disbursement.verify']) assert.equal(identityDestinations([permission]).some(item => item.href === path), false);
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: ['disbursement.verify'] }); assert.deepEqual(stepUpFields(session, 'disbursement.verify', path), { expectedActorId: session.account!.userId, operation: 'disbursement.verify', returnPath: path });
  assert.equal(recoveryPage('procurement.settlement.verify').href, path); assert.equal(operationLabel('procurement.settlement.verify'), 'Verifikasi debit bank pemasok');
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/settlements']) assert.throws(() => loginFields(invalid, '', ''));
});
