import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createProcurementInspectionGateway, inspectionInput, parseInspectionChoices, parseProcurementInspectionRead, parseInspectionEvidence, inspectionEvidenceReady, inspectionFileAllowed, prepareInspection, prepareInspectionSubmission, parseProcurementInspectionResult, type InspectionUploadLimits } from '../src/modules/procurement/procurement-inspection-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', draftId = '9007199254740994', noticeId = '9007199254740995', intakeId = '9007199254740996';
const location = { organizationId: '100', shopId: '101', warehouseId: '102', channelId: '103' };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '1', profileVersion: '2' };
const source = { organization, shop: { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, warehouse: { ...organization, id: '102', code: 'WARE', name: 'Gudang' }, channel: { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' } }, target = { location, ...source };
const packageLine = { skuId: '500', skuRevision: '2', definitionId: '501', definitionVersion: '1', skuCode: 'PAPER-BOX', familyName: 'Kertas', packageLabel: 'Karton 12', packageKind: 'sealed' as const, baseUnitCode: 'RIM', baseUnitsPerPackage: 12, packages: 2, specification: 'Kemasan utuh', sourceSystem: 'supplier' as const, sourceAccountReference: '0007', externalItemCode: '00Ab-01', netPricePerPackage: '12.34', taxAmount: '1.23', supplierChargeAmount: '0.04', netAmount: '24.68', totalAmount: '25.95', baseUnits: '24' };
const order = { id: '710', version: '1' as const, approvalId: '700', proposalId: '701', route: 'ddo' as const, businessDate: '2026-10-03', currency: 'IDR' as const, supplier: { id: '600', versionId: '601', version: '1', subjectReference: 'SUP-001', displayName: 'Sumber Kertas' }, total: '25.95', requiredAdvance: '0.00', paymentTerms: { kind: 'on_receipt' as const, description: 'Dibayar setelah diterima', requiredAdvance: '0.00', balanceTrigger: 'on_receipt' as const, dueDate: null }, commercialReference: { kind: 'quote' as const, reference: '000QUOTE-A', issuedDate: '2026-10-02', validUntil: '2026-10-03T00:00:00.000Z' }, externalOrderReference: null, lines: [packageLine] };
const notice = { id: noticeId, version: '1' as const, input: { location, orderId: order.id, orderVersion: '1' as const, orderApprovalId: '700', noticeDate: '2026-10-03', supplierDocumentReference: '000DOC-Ab', ddoRequestReference: '000DDO-a', ddoTelephoneReference: 'Call-01', declaredContainers: 5, note: 'Lima koli sesuai dokumen pemasok' }, order, source, recordedAt: '2026-10-03T01:01:00.000Z', reason: 'Dokumen nyata telah diperiksa', state: 'notice_recorded' as const };
const intake = { id: intakeId, noticeId, version: '1', input: { receivedDate: '2026-10-03', intakeReference: '000INTAKE-a', matchedContainers: 3, excessContainers: 1, heldContainers: 1, condition: 'mixed' as const, discrepancy: 'Satu koli lebih ditahan untuk konfirmasi' }, recordedAt: '2026-10-03T01:03:00.000Z', reason: 'Hitungan fisik sudah diperiksa', state: 'pending_inspection' as const };
const capacity = { version: '0', receivedContainers: '4', inspectedContainers: '0', remainingContainers: '4' }, choice = { notice, intake, capacity };
const observedLine = { requestSkuId: '500', definitionId: '501', definitionVersion: '1', declaredPackages: 2, receivedPackages: 3, damagedPackages: 1, expiredPackages: 1, openedPackages: 2, lotCode: '000LOT-Ab', expiryDate: '2026-10-02', putawayReference: 'QUAR-0001', discrepancy: 'Kelebihan dan kondisi dapat mengenai kemasan yang sama' };
const input = { location, noticeId, intakeId, inspectionDate: '2026-10-03', inspectionReference: '000INSPECT-a', inspectedContainers: 2, lines: [observedLine], discrepancy: null };
const preview = { ...choice, input, binding: 'a'.repeat(64), observedAt: '2026-10-03T01:04:00.000Z' };
const draft = { ...choice, input, id: draftId, version: '1' as const, recordedAt: '2026-10-03T01:05:00.000Z', reason: 'Hasil pengamatan fisik dicatat', state: 'draft' as const };
const read = { draft, capacity, inspection: null };
const video = { reservationId: '900', revision: '1', expiresAt: '2026-10-03T02:00:00.000Z', state: 'quarantined' as const, reference: { id: '901', version: '3', sha256: 'c'.repeat(64) }, scanStatus: 'clean' as const, decoderStatus: 'decoded' as const, safeFailure: null };
const submitPreview = { draft, capacity, binding: 'b'.repeat(64), observedAt: '2026-10-03T01:06:00.000Z' };
const submission = { id: '950', draftId, version: '1' as const, input, before: capacity, after: { version: '1', receivedContainers: '4', inspectedContainers: '2', remainingContainers: '2' }, reason: 'Video asli dan hasil pembukaan telah ditinjau', recordedAt: '2026-10-03T01:07:00.000Z', state: 'submitted_quarantine' as const, reservationId: video.reservationId, evidence: video.reference };
const submittedRead = { draft: { ...draft, state: 'submitted_quarantine' as const }, capacity: submission.after, inspection: submission };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', draftKey = `pi.draft.${intakeId}.${uuid}`, submitKey = `pi.submit.${draftId}.${uuid}`, reserveKey = `pie.${draftId}.${uuid}`;
const draftResult = { operation: 'procurement.inspection.draft' as const, draftId, inspectionId: null, version: '1' as const, state: 'draft' as const, recordedAt: draft.recordedAt };
const submitResult = { operation: 'procurement.inspection.submit' as const, draftId, inspectionId: '950', version: '1' as const, state: 'submitted_quarantine' as const, recordedAt: submission.recordedAt };
const draftCommand = () => prepareInspection(actor, target, choice, input, preview, draft.reason);
const submitCommand = () => prepareInspectionSubmission(actor, read, submitPreview, video, submission.reason);

test('inspection preserves explicit partial observations, overlapping conditions, source codes and distinct lots without inferring healthy stock', () => {
  assert.deepEqual(inspectionInput(input), input); assert.equal(draftCommand().body.input.lines[0].receivedPackages, 3); assert.ok(Object.isFrozen(draftCommand().body.input.lines));
  const multiple = inspectionInput({ ...input, lines: [{ ...observedLine, lotCode: 'ZZ' }, { ...observedLine, lotCode: null, expiryDate: null, putawayReference: null }] });
  assert.equal(multiple.lines[0].lotCode, null); assert.equal(multiple.lines[0].expiryDate, null); assert.equal(multiple.lines[0].putawayReference, null);
  assert.equal(inspectionInput({ ...input, inspectionReference: 'A\u200bB' }).inspectionReference, 'A\u200bB');
  for (const value of [{ ...input, inspectedContainers: '2' }, { ...input, inspectedContainers: 0 }, { ...input, intakeId: Number(intakeId) }, { ...input, inspectionDate: '2026-02-30' }, { ...input, inspectionDate: '1999-01-01' }, { ...input, inspectionReference: ' A' }, { ...input, lines: [observedLine, observedLine] }, { ...input, availableStock: 12 }]) assert.throws(() => inspectionInput(value), RequestNotSentError);
  for (const row of [{ ...observedLine, declaredPackages: '2' }, { ...observedLine, damagedPackages: 4 }, { ...observedLine, expiryDate: '2026-10-03' }, { ...observedLine, discrepancy: null }, { ...observedLine, receivedPackages: 0, declaredPackages: 0 }, { ...observedLine, lotCode: 'x\n' }]) assert.throws(() => inspectionInput({ ...input, lines: [row] }), RequestNotSentError);
});

test('frozen review and history bind exact intake, package version, scope, capacity and quarantine state', () => {
  assert.deepEqual(parseInspectionChoices({ location, items: [choice], nextIntakeId: null }).items[0], choice);
  assert.deepEqual(parseProcurementInspectionRead(read), read); assert.deepEqual(parseProcurementInspectionRead(submittedRead), submittedRead);
  assert.equal(parseProcurementInspectionRead({ ...submittedRead, inspection: { ...submission, reservationId: null, evidence: null } }).inspection?.evidence, null);
  for (const changed of [{ ...preview, input: { ...input, lines: [{ ...observedLine, definitionVersion: '2' }] } }, { ...preview, input: { ...input, inspectedContainers: 5 } }, { ...preview, input: { ...input, inspectionDate: '2026-10-02' } }, { ...preview, capacity: { ...capacity, receivedContainers: '5', remainingContainers: '5' } }, { ...preview, notice: { ...notice, source: { ...source, warehouse: { ...source.warehouse, id: '999' } } } }]) assert.throws(() => prepareInspection(actor, target, choice, input, changed, draft.reason));
  for (const changed of [{ ...submittedRead, inspection: { ...submission, state: 'stock_available' } }, { ...submittedRead, inspection: { ...submission, reservationId: null } }, { ...submittedRead, capacity: { ...submission.after, remainingContainers: '3' } }, { ...read, privatePath: 'secret' }]) assert.throws(() => parseProcurementInspectionRead(changed), AccessFailure);
});

test('video uses the genuine advertised byte limit and exact positive evidence version; upload completion alone is not readiness', () => {
  const limits: InspectionUploadLimits = { maximumBytes: '20000000', acceptedMimeTypes: ['video/mp4', 'video/webm'] };
  inspectionFileAllowed({ size: 11 * 1024 * 1024, type: 'video/mp4' }, limits);
  for (const file of [{ size: 20_000_001, type: 'video/mp4' }, { size: 0, type: 'video/webm' }, { size: 50, type: 'image/png' }]) assert.throws(() => inspectionFileAllowed(file, limits), RequestNotSentError);
  assert.equal(parseInspectionEvidence(video).reference?.version, '3'); assert.equal(inspectionEvidenceReady(video), true);
  const pending = { ...video, scanStatus: 'unavailable' as const, decoderStatus: 'unavailable' as const };
  assert.equal(inspectionEvidenceReady(pending), false); assert.throws(() => prepareInspectionSubmission(actor, read, submitPreview, pending, submission.reason), RequestNotSentError);
  assert.throws(() => parseInspectionEvidence({ ...video, reference: { ...video.reference, version: 3 } }), AccessFailure);
});

test('submission sends actor, CSRF, revision and exact reviewed evidence once, with no hidden stock or financial values', async () => {
  const requests: Request[] = [];
  const gateway = createProcurementInspectionGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); return Response.json(submitResult); } }));
  assert.deepEqual(await gateway.submit(submitCommand(), submitKey, () => true), submitResult); assert.equal(requests.length, 1);
  assert.equal(requests[0].headers.get('If-Match'), '"1"'); assert.equal(requests[0].headers.get('X-Expected-Actor-Id'), actor); assert.equal(requests[0].headers.get('X-CSRF-Token'), 'csrf'); assert.equal(requests[0].headers.get('Idempotency-Key'), submitKey); assert.deepEqual(await requests[0].json(), submitCommand().body);
  await assert.rejects(() => gateway.submit(submitCommand(), submitKey, () => false), RequestNotSentError); assert.equal(requests.length, 1);
  assert.throws(() => parseProcurementInspectionResult(submitResult, `pi.submit.99.${uuid}`, submitResult.operation));
});

test('lost draft, video reservation and submission replies have original-actor lookup-only recovery and retain unresolved references', async () => {
  for (const mode of ['draft', 'reserve', 'submit'] as const) {
    const key = mode === 'draft' ? draftKey : mode === 'submit' ? submitKey : reserveKey, op = mode === 'reserve' ? 'evidence.procurement-unboxing-video.reserve' : 'procurement.inspection.' + mode;
    const saved = mode === 'draft' ? draftResult : submitResult, storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key), requests: Request[] = [];
    let confirmed = false;
    const gateway = createProcurementInspectionGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input), path = new URL(request.url).pathname; if (path.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); if (path.endsWith('/operations/query')) return Response.json(mode === 'reserve' ? { status: confirmed ? 'committed' : 'not_confirmed', reservation: confirmed ? { reservationId: video.reservationId, revision: '1', expiresAt: video.expiresAt } : null } : { status: confirmed ? 'committed' : 'not_confirmed', result: confirmed ? saved : null }); if (path.endsWith('/query')) return Response.json(mode === 'submit' ? submittedRead : read); throw new TypeError('Lost committed response'); } }));
    const send = async () => mode === 'draft' ? gateway.createDraft(draftCommand(), key, () => true) : mode === 'submit' ? gateway.submit(submitCommand(), key, () => true) : gateway.reserve(actor, draftId, key, () => true);
    await assert.rejects(() => submitTrackedRequest(ledger, journal, actor, 'inspection', op, { target: draftId }, send));
    const [entry] = journal.list(actor); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(journal.list('99').length, 0);
    const adapters = { 'procurement.inspection': { lookup: (item: typeof entry) => gateway.recover(item.actorId, item.operation, item.key) }, 'evidence.procurement-unboxing-video': { lookup: (item: typeof entry) => gateway.recoverReservation(item.actorId, item.key) } };
    assert.equal(operationRecoveryCapabilities(op, adapters).canClose, false);
    const generic = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
    assert.equal((await reconcileOperationRecovery(entry, journal, generic, adapters)).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1);
    confirmed = true; assert.equal((await reconcileOperationRecovery(entry, journal, generic, adapters)).state, 'committed'); assert.equal(journal.list(actor).length, 0);
    assert.equal(requests.filter(request => !new URL(request.url).pathname.endsWith('/query')).length, 1); assert.ok(requests.every(request => request.headers.get('X-Expected-Actor-Id') === actor));
  }
});

test('inspection original validates returned MIME, length and digest before producing a private blob', async () => {
  const bytes = new TextEncoder().encode('synthetic original fixture'), digest = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join('');
  let mime = 'video/mp4'; const requests: Request[] = [];
  const gateway = createProcurementInspectionGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); return new Response(bytes, { headers: { 'Content-Type': mime, 'Content-Length': String(bytes.length) } }); } }));
  const item = { ...video, reference: { ...video.reference, sha256: digest } };
  assert.equal((await gateway.original(actor, draftId, item, () => true)).type, 'video/mp4');
  assert.deepEqual(await requests[0].json(), { purpose: 'procurement-inspection-evidence-view', reference: item.reference });
  mime = 'image/png'; await assert.rejects(() => gateway.original(actor, draftId, item, () => true), AccessFailure);
  mime = 'video/webm'; await assert.rejects(() => gateway.original(actor, draftId, video, () => true), AccessFailure);
});

test('inspection navigation uses actual procurement read grants and exact fresh operation return path', () => {
  assert.equal(identityReturnPath('/procurement/inspections'), '/procurement/inspections');
  assert.equal(identityDestinations(['procurement.read']).some(item => item.href === '/procurement/inspections'), true);
  assert.equal(identityDestinations(['funding.read']).some(item => item.href === '/procurement/inspections'), false);
  assert.deepEqual(recoveryPage('evidence.procurement-unboxing-video.reserve'), { href: '/procurement/inspections', label: 'halaman pemeriksaan barang pengadaan' });
  assert.equal(stepUpFields(parseIdentitySession({ ...sourceSession, stepUpOperations: ['procurement.inspection.record'] }), 'procurement.inspection.record', '/procurement/inspections').returnPath, '/procurement/inspections');
});
