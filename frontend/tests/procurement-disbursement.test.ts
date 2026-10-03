import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createDisbursementGateway, disbursementInput, disbursementToday, disbursementPolicyCurrent, disbursementEvidenceReady, disbursementKeyOwner, disbursementReservationOwner, parseDisbursementDraft, parseDisbursementOptions, parseDisbursementResult, prepareDisbursement, prepareDisbursementRecord } from '../src/modules/procurement/procurement-disbursement-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', authorizationId = '9007199254740994', draftId = '9007199254740995';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' }, label = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '1', profileVersion: '1' };
const target = { location, organization: label, shop: { ...label, id: '101', code: 'SHOP', name: 'Toko' }, warehouse: { ...label, id: '102', code: 'WH', name: 'Gudang' }, channel: { ...label, id: '103', code: 'RETAIL', name: 'Ritel' } };
const authorization = { id: authorizationId, requestId: '701', decisionId: '702', orderId: '703', orderVersion: '1' as const, orderApprovalId: '704', route: 'ddo' as const, supplier: { id: '600', versionId: '601', version: '2', subjectReference: '00SUP-1', displayName: 'Sumber Kertas' }, authorizedAmount: '100.00', businessDate: '2026-10-03', neededBy: '2026-10-04', approvedAt: '2026-10-03T01:00:00.000Z' };
const capacity = { authorizationId, authorizedAmount: '100.00', recordedAmount: '20.00', remainingAmount: '80.00', version: '1' }, choice = { authorization, capacity };
const beneficiary = { supplierId: '600', beneficiaryId: '610', versionId: '611', version: '2', bankCode: 'BNI', maskedAccount: '****0001', currency: 'IDR' as const };
const payingAccount = { id: '620', versionId: '621', version: '3', bankCode: 'BNI', bankName: 'Bank BNI', maskedAccount: '****0002', currency: 'IDR' as const };
const policy = { id: '800', location, revision: '1', payingAccountId: '620', payingAccountVersionId: '621', allowPartialRecordings: true, reserveUnverified: true as const, payoutRecordingProcedureReference: 'Prosedur catat001', beneficiaryVerificationProcedureReference: 'Prosedur penerima001', operatingOwnerReference: 'Finance koperasi', approvalReference: 'Persetujuan 001', approvedAt: '2026-10-01T00:00:00.000Z', expiresAt: '2026-11-01T00:00:00.000Z', active: true };
const option = { policy, payingAccount }, options = { authorization, capacity, beneficiary, items: [option] };
const input = { location, fundingRequestId: '701', fundingDecisionId: '702', authorizationId, supplierId: '600', beneficiaryVersionId: '611', beneficiaryVersion: '2', payingAccountId: '620', payingAccountVersionId: '621', policyId: '800', policyRevision: '1', amount: '30.01', transferDate: '2026-10-03', transferReference: '000TRANSFER-Ab' };
const facts = { input, authorization, capacity, beneficiary, payingAccount, policy }, preview = { ...facts, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:01:00.000Z' };
const draft = { ...facts, id: draftId, version: '1' as const, reason: 'Catatan dari bukti asli', createdAt: '2026-10-03T01:02:00.000Z', state: 'draft' as const, disbursement: null };
const evidence = { reservationId: '900', revision: '1', state: 'quarantined' as const, expiresAt: '2026-10-03T02:00:00.000Z', reference: { id: '901', version: '1' as const, sha256: 'd'.repeat(64) }, scanStatus: 'clean' as const, decoderStatus: 'decoded' as const, safeFailure: null };
const recordPreview = { draft, capacity: { ...capacity, recordedAmount: '40.00', remainingAmount: '60.00', version: '2' }, binding: 'b'.repeat(64), observedAt: '2026-10-03T01:03:00.000Z' };
const draftResult = { draftId, draftVersion: '1' as const, authorizationId, disbursementId: null, amount: '30.01', status: 'draft' as const }, recorded = { ...draftResult, disbursementId: '902', status: 'recorded_unverified' as const };
const recordedDraft = { ...draft, state: 'recorded_unverified' as const, disbursement: { id: '902', amount: '30.01', recordedAt: recordPreview.observedAt, reservationId: evidence.reservationId, evidence: evidence.reference } };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', draftKey = `pd.draft.${authorizationId}.${uuid}`, recordKey = `pd.record.${draftId}.${uuid}`, reserveKey = `pde.${draftId}.${uuid}`;
const command = () => prepareDisbursement(actor, target, choice, options, option, input, preview, draft.reason);
const recordCommand = () => prepareDisbursementRecord(actor, draft, recordPreview, evidence, 'Bukti asli diperiksa');

test('disbursement inputs preserve exact amounts, identifiers and references; transfer dates follow prior approval and current WIB day', () => {
  assert.deepEqual(disbursementInput(input), input); assert.equal(command().body.input.transferReference, '000TRANSFER-Ab'); assert.ok(Object.isFrozen(command().body.input));
  for (const amount of ['', '0.00', '01.00', '1e2', '1.001', '1000000000000000.00']) assert.throws(() => disbursementInput({ ...input, amount }), RequestNotSentError);
  for (const changed of [{ ...input, transferDate: '1999-12-31' }, { ...input, transferDate: '2026-02-30' }, { ...input, transferReference: ' REF ' }, { ...input, beneficiaryVersion: '02' }]) assert.throws(() => disbursementInput(changed));
  for (const hidden of ['\u0000', '\u200d', '\ufeff']) {
    assert.throws(() => disbursementInput({ ...input, transferReference: 'REF' + hidden + '001' }), RequestNotSentError);
    assert.throws(() => prepareDisbursement(actor, target, choice, options, option, input, preview, 'Reason' + hidden + '001'), RequestNotSentError);
    assert.throws(() => prepareDisbursementRecord(actor, draft, recordPreview, evidence, 'Reason' + hidden + '001'), RequestNotSentError);
    assert.throws(() => parseDisbursementDraft({ ...draft, reason: 'Reason' + hidden + '001' }), AccessFailure);
    assert.throws(() => parseDisbursementOptions({ ...options, items: [{ ...option, policy: { ...policy, approvalReference: 'REF' + hidden + '001' } }] }), AccessFailure);
  }
  assert.equal(disbursementInput({ ...input, transferReference: '000Cafe\u0301' }).transferReference, '000Cafe\u0301');
  for (const transferDate of ['2026-10-02', '2026-10-04']) assert.throws(() => prepareDisbursement(actor, target, choice, options, option, { ...input, transferDate }, { ...preview, input: { ...input, transferDate } }, draft.reason));
  assert.equal(disbursementToday(Date.parse('2026-10-03T17:00:00.000Z')), '2026-10-04');
});

test('draft preview is bound to exact scoped authorization, current beneficiary, paying-account version and explicit finite policy', () => {
  for (const changed of [{ ...preview, beneficiary: { ...beneficiary, supplierId: '999' } }, { ...preview, payingAccount: { ...payingAccount, versionId: '999' } }, { ...preview, authorization: { ...authorization, decisionId: '999' } }, { ...preview, policy: { ...policy, location: { ...location, shopId: '999' } } }, { ...preview, input: { ...input, amount: '30.00' } }]) assert.throws(() => prepareDisbursement(actor, target, choice, options, option, input, changed, draft.reason));
  for (const changed of [{ ...policy, active: false }, { ...policy, expiresAt: preview.observedAt }, { ...policy, allowPartialRecordings: false }]) assert.throws(() => prepareDisbursement(actor, target, choice, { ...options, items: [{ policy: changed, payingAccount }] }, { policy: changed, payingAccount }, input, { ...preview, policy: changed }, draft.reason));
  assert.equal(disbursementPolicyCurrent(policy, Date.parse(policy.expiresAt)), false); assert.equal(parseDisbursementOptions({ ...options, beneficiary: null }).beneficiary, null);
  assert.throws(() => parseDisbursementOptions({ ...options, beneficiary: { ...beneficiary, accountNumber: '00010001' } })); assert.throws(() => parseDisbursementOptions({ ...options, items: [{ policy, payingAccount: { ...payingAccount, holderName: 'Private' } }] }));
});

test('recording uses fresh remaining capacity and mandatory safe evidence without silently reducing the immutable draft amount', () => {
  assert.equal(recordCommand().amount, input.amount); assert.ok(Object.isFrozen(recordCommand().body.reference));
  assert.throws(() => prepareDisbursementRecord(actor, draft, { ...recordPreview, capacity: { ...capacity, recordedAmount: '70.00', remainingAmount: '30.00', version: '3' } }, evidence, 'Over limit'));
  assert.throws(() => prepareDisbursementRecord(actor, draft, { ...recordPreview, draft: { ...draft, input: { ...input, amount: '30.00' } } }, evidence, 'Changed amount'));
  for (const changed of [{ ...evidence, state: 'fresh' as const, reference: null, scanStatus: 'unavailable' as const, decoderStatus: 'unavailable' as const }, { ...evidence, scanStatus: 'infected' as const, decoderStatus: 'unavailable' as const, safeFailure: 'infected_media' }, { ...evidence, decoderStatus: 'invalid' as const, safeFailure: 'invalid_media' }, { ...evidence, safeFailure: 'integrity_mismatch', decoderStatus: 'unavailable' as const }]) { assert.equal(disbursementEvidenceReady(changed), false); assert.throws(() => prepareDisbursementRecord(actor, draft, recordPreview, changed, 'Unsafe')); }
  assert.deepEqual(parseDisbursementDraft(recordedDraft), recordedDraft); assert.throws(() => parseDisbursementDraft({ ...recordedDraft, disbursement: null })); assert.throws(() => parseDisbursementDraft({ ...recordedDraft, state: 'bank_verified' })); assert.throws(() => parseDisbursementDraft({ ...recordedDraft, journalId: '999' }));
});

test('draft and final-record transport carries exact actor, CSRF, immutable revision and operation key without replaying on denial or actor change', async () => {
  let calls = 0; const gateway = createDisbursementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/drafts')) { assert.equal(input.headers.get('Idempotency-Key'), draftKey); assert.deepEqual(await input.json(), command().body); return Response.json(draftResult); }
    assert.ok(input.url.endsWith('/records')); assert.equal(input.headers.get('If-Match'), '"1"'); assert.equal(input.headers.get('Idempotency-Key'), recordKey); assert.deepEqual(await input.json(), recordCommand().body); return calls === 3 ? Response.json({ code: 'independent_person_required' }, { status: 403 }) : Response.json(recorded);
  } }));
  await assert.rejects(gateway.createDraft(command(), draftKey, () => false), RequestNotSentError); assert.equal(calls, 0); assert.deepEqual(await gateway.createDraft(command(), draftKey, () => true), draftResult); assert.deepEqual(await gateway.record(recordCommand(), recordKey, () => true), recorded); await assert.rejects(gateway.record(recordCommand(), recordKey, () => true), { code: 'independent_person_required' }); assert.equal(calls, 3);
  let checks = 0; await assert.rejects(gateway.record(recordCommand(), recordKey, () => ++checks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 4);
});

test('private original viewer checks digest, content length and MIME; upload sends original bytes with no unsafe type conversion', async () => {
  const bytes = new Uint8Array([1, 2, 3]), sha256 = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join('');
  const image = { ...evidence, reference: { ...evidence.reference, sha256 } }; let calls = 0, wrongLength = false;
  const gateway = createDisbursementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => { assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('If-Match'), '"1"');
    if (input.url.endsWith('/original/query')) { assert.deepEqual(await input.json(), { purpose: 'procurement-disbursement-evidence-view', reference: image.reference }); return new Response(bytes, { headers: { 'Content-Type': 'image/png', 'Content-Length': wrongLength ? '4' : '3' } }); }
    assert.ok(input.url.endsWith('/content')); assert.equal(input.headers.get('Content-Type'), 'application/octet-stream'); assert.equal(input.headers.get('Idempotency-Key'), 'upload.900'); assert.deepEqual(new Uint8Array(await input.arrayBuffer()), bytes); return Response.json({ state: 'quarantined', reference: image.reference });
  } }));
  assert.equal((await gateway.original(actor, draftId, image, () => true)).size, 3); wrongLength = true; await assert.rejects(gateway.original(actor, draftId, image, () => true)); wrongLength = false;
  const changedGateway = createDisbursementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => { assert.ok(input instanceof Request); return input.url.endsWith('/csrf') ? Response.json({ requestToken: 'csrf' }) : new Response(bytes, { headers: { 'Content-Type': 'image/png', 'Content-Length': '3' } }); } }));
  await assert.rejects(changedGateway.original(actor, draftId, evidence, () => true));
  await assert.rejects(gateway.upload(actor, draftId, '900', '1', new File([bytes], 'not-image.txt', { type: 'text/plain' }), () => true), RequestNotSentError); assert.equal(calls, 2);
  assert.equal((await gateway.upload(actor, draftId, '900', '1', new File([bytes], 'proof.png', { type: 'image/png' }), () => true)).state, 'quarantined'); assert.equal(calls, 3);
});

test('record and reservation recovery persist only original actor references and remain lookup-only until the exact result is confirmed', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => recordKey); let sends = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'procurement-disbursement-record', 'procurement.disbursement.record', recordCommand().body, async () => { sends++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0], serialized = [...storage.values.values()][0]; assert.equal(journal.list('99').length, 0); assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']); for (const privateValue of [input.transferReference, input.amount, evidence.reference.sha256, recordCommand().body.reason]) assert.equal(serialized.includes(privateValue), false);
  const legacy = { lookup: async () => { throw new Error('No generic recovery'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities(entry.operation), { canCheck: false, canClose: false, unavailable: 'adapter' }); assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.disbursement': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.disbursement': { lookup: async () => ({ state: 'committed', result: recorded }) } }, 'Close')); await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'procurement.disbursement': { lookup: async () => ({ state: 'committed', result: { ...recorded, draftId: '999' } }) } }));
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'procurement.disbursement': { lookup: async () => ({ state: 'committed', result: recorded }) } }), { state: 'committed', result: { id: draftId, version: '1' } }); assert.equal(sends, 1);
  const reservation = journal.remember(actor, 'evidence.procurement-disbursement-image.reserve', reserveKey); assert.equal(disbursementReservationOwner(reserveKey), draftId); assert.deepEqual(await reconcileOperationRecovery(reservation, journal, legacy, { 'evidence.procurement-disbursement-image': { lookup: async () => ({ state: 'committed', result: { reservationId: '900', revision: '1', expiresAt: evidence.expiresAt } }) } }), { state: 'committed', result: { id: '900', version: '1' } }); assert.equal(journal.list(actor).length, 0);
});

test('owning recovery re-reads immutable draft and rejects foreign records; only actual disbursement read grants discover the task', async () => {
  let foreign = false, calls = 0; const gateway = createDisbursementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => { assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: 'procurement.disbursement.record', key: recordKey }); return Response.json({ status: 'confirmed', result: recorded }); }
    assert.deepEqual(await input.json(), { purpose: 'disbursement-review' }); return Response.json(foreign ? { ...recordedDraft, disbursement: { ...recordedDraft.disbursement, id: '999' } } : recordedDraft);
  } }));
  assert.deepEqual(await gateway.recover(actor, 'procurement.disbursement.record', recordKey), { state: 'committed', result: recorded }); foreign = true; await assert.rejects(gateway.recover(actor, 'procurement.disbursement.record', recordKey)); assert.equal(calls, 4);
  assert.throws(() => parseDisbursementResult({ ...recorded, amount: '0.00' }, recordKey, 'procurement.disbursement.record')); assert.throws(() => disbursementKeyOwner(draftKey, 'procurement.disbursement.record'));
  const path = '/procurement/disbursements'; assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path }); assert.equal(identityDestinations(['procurement.disbursement.read']).some(value => value.href === path), true);
  for (const permission of ['procurement.read', 'funding.approve', 'receiving.account.approve', 'disbursement.record']) assert.equal(identityDestinations([permission]).some(value => value.href === path), false);
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: ['disbursement.record'] }); assert.deepEqual(stepUpFields(session, 'disbursement.record', path), { expectedActorId: session.account!.userId, operation: 'disbursement.record', returnPath: path });
  assert.equal(recoveryPage('procurement.disbursement.record').href, path); assert.equal(recoveryPage('evidence.procurement-disbursement-image.reserve').href, path); assert.equal(operationLabel('procurement.disbursement.record'), 'Pencatatan uang muka belum diverifikasi');
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//procurement/disbursements']) assert.throws(() => loginFields(invalid, '', ''));
});
