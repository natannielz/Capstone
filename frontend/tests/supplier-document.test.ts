import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { createSupplierDocumentGateway, supplierDocumentInput, supplierDocumentMatchInput, parseSupplierDocumentRead, parseSupplierDocumentOcr, parseSupplierDocumentEvidence, prepareSupplierDocument, prepareSupplierDocumentSubmission, prepareSupplierDocumentMatch, parseSupplierDocumentResult, supplierDocumentSuggestions, supplierDocumentFileAllowed, supplierDocumentEvidenceReady } from '../src/modules/procurement/supplier-document-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';
import { actor, draftId, location, target, order, notice, intake, inspectionInput } from './supplier-document-fixtures';
import { parseSupplierDocumentQualityChoices } from '../src/modules/procurement/supplier-document-client';
import {rememberSupplierDocumentPdfObservation, type SupplierDocumentPdfMatchObservation} from '../src/modules/procurement/supplier-document-pdf-observations';
import type {PdfDocumentObservationSelection} from '../src/modules/documents/pdf-document-ocr-client';

const line = { sourceLineReference: '001', description: 'Kertas pada invoice', sourceSystem: 'supplier', sourceAccount: '0007', sourceCode: '00Ab-01', packages: 2, netPricePerPackage: '12.34', taxAmount: '1.23', chargeAmount: '0.04', relatedInvoiceReference: null, statementAmount: null };
const input = { location, anchorOrderId: order.id, anchorOrderVersion: '1' as const, anchorOrderApprovalId: order.approvalId, kind: 'invoice' as const, documentReference: '000INV-a', documentDate: '2026-10-03', currency: 'IDR' as const, documentTotal: '25.95', lines: [line], discrepancy: null };
const capacity = { version: '0', lines: [{ lineNumber: 1, packages: 2, matchedPackages: '0', remainingPackages: '2', matchedNetAmount: '0.00', matchedTaxAmount: '0.00', matchedChargeAmount: '0.00', matchedStatementAmount: '0.00' }] };
const preview = { input, order, supplierId: order.supplier.id, possibleDuplicate: false, binding: 'a'.repeat(64), observedAt: '2026-10-03T02:00:00.000Z' };
const draft = { id: draftId, version: '1' as const, input, supplierId: order.supplier.id, order, recordedAt: '2026-10-03T02:01:00.000Z', reason: 'Original invoice explicitly recorded', state: 'draft' as const };
const read = { draft, document: null, capacity, reviewedInput: null, possibleDuplicate: false, matches: [], nextMatchId: null };
const image = { reservationId: '900', revision: '1', expiresAt: '2026-10-03T03:00:00.000Z', state: 'quarantined' as const, reference: { id: '901', version: '3', sha256: 'c'.repeat(64) }, scanStatus: 'clean' as const, decoderStatus: 'decoded' as const, safeFailure: null };
const document = { id: '950', draftId, version: '1' as const, state: 'submitted_unmatched' as const, recordedAt: '2026-10-03T02:02:00.000Z', reason: 'Original reviewed', reservationId: image.reservationId, evidence: image.reference };
const submitted = { ...read, draft: { ...draft, state: 'submitted_unmatched' as const }, document };
const allocate = { documentLineNumber: 1, packages: 1, inspectionId: '960', inspectionLineNumber: 1, invoiceMatchId: null, invoiceMatchLineNumber: null, netAmount: '12.34', taxAmount: '0.60', chargeAmount: '0.02', statementAmount: null, discrepancy: null };
const reviewed = { documentId: document.id, documentRevision: '1' as const, reviewedInput: input, ocrResultId: null, lines: [allocate] };
const resolved = { ...allocate, state: 'matched' as const, order, inspectionLine: inspectionInput.lines[0], invoiceDocumentReference: null };
const matchedPreview = { input: reviewed, document: submitted.draft, capacity, lines: [resolved], possibleDuplicate: false, binding: 'b'.repeat(64), observedAt: '2026-10-03T02:03:00.000Z' };
const fields = ['document_reference', 'document_date', 'supplier_name', 'total_amount', 'currency', 'purchase_order_reference', 'delivery_reference', 'source_sku'] as const;
const extraction = { draftId, submissionId: document.id, evidence: image.reference, state: 'succeeded' as const, resultId: '970', extractedAt: '2026-10-03T02:03:00.000Z', provenance: { engineVersion: 'engine', modelVersion: 'model', modelSha256: 'd'.repeat(64), runtimeSha256: 'e'.repeat(64), preprocessingVersion: 'bounded-original' }, lines: [{ text: 'PO: 000QUOTE-A', confidence: '0.99', boundingBox: ['0.1', '0.1', '0.8', '0.1'] }], fields: fields.map(field => ({ field, value: field === 'purchase_order_reference' ? '000QUOTE-A' : null, confidence: field === 'purchase_order_reference' ? '0.99' : null, boundingBox: field === 'purchase_order_reference' ? ['0.1', '0.1', '0.8', '0.1'] : null, flags: field === 'purchase_order_reference' ? [] : ['unknown'] })), qualityFlags: ['human_line_matching_required'], failureCode: null };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', draftKey = `sd.draft.${order.id}.${uuid}`, submitKey = `sd.submit.${draftId}.${uuid}`, reserveKey = `sde.${draftId}.${uuid}`, matchKey = `sd.match.${document.id}.${uuid}`;
const draftResult = { operation: 'procurement.supplierdocument.draft' as const, draftId, documentId: null, matchId: null, version: '1' as const, state: 'draft' as const, recordedAt: draft.recordedAt };
const submitResult = { ...draftResult, operation: 'procurement.supplierdocument.submit' as const, documentId: document.id, state: 'submitted_unmatched' as const };
const matchResult = { ...submitResult, operation: 'procurement.supplierdocument.match' as const, matchId: '980', state: 'match_recorded' as const };
const draftCommand = () => prepareSupplierDocument(actor, target, order, input, preview, draft.reason);
const submitCommand = () => prepareSupplierDocumentSubmission(actor, read, { draft, binding: 'f'.repeat(64), observedAt: document.recordedAt }, image, document.reason);
const matchCommand = () => prepareSupplierDocumentMatch(actor, submitted, reviewed, matchedPreview, null, 'Partial allocation explicitly reviewed');

const pdfEvidence = {id: '1001', version: '1', sha256: '1'.repeat(64)};
const pdfOwner = parseSupplierDocumentRead({...submitted, document: {...document, evidence: pdfEvidence}});
const pdfObservation: SupplierDocumentPdfMatchObservation = {field: 'total_amount', originalReviewed: true,
  candidate: {resultId: '9007199254740999', resultHash: '2'.repeat(64), evidence: {...pdfEvidence, version: '1'}, pageIndex: 1, field: 'total_amount'}};
const pdfInput = {...reviewed, pdfObservations: [pdfObservation]};
test('PDF matching provenance is optional canonical and distinct while image OCR identity stays separate', () => {
  assert.equal(Object.hasOwn(supplierDocumentMatchInput(reviewed), 'pdfObservations'), false);
  assert.deepEqual(supplierDocumentMatchInput(pdfInput).pdfObservations, [pdfObservation]);
  for (const observations of [null, [], [pdfObservation, pdfObservation], [{...pdfObservation, originalReviewed: false}],
    [{...pdfObservation, field: 'currency'}], [{...pdfObservation, candidate: {...pdfObservation.candidate, pageIndex: 10}}],
    [{...pdfObservation, candidate: {...pdfObservation.candidate, resultId: Number(pdfObservation.candidate.resultId)}}],
    [{...pdfObservation, candidate: {...pdfObservation.candidate, resultId: '9223372036854775806'}}],
    [{...pdfObservation, candidate: {...pdfObservation.candidate, evidence: {...pdfEvidence, version: '3'}}}],
    [{...pdfObservation, candidate: {...pdfObservation.candidate, resultHash: '2'.repeat(64) + '\n'}}],
    [{...pdfObservation, candidate: {...pdfObservation.candidate, value: '2345678.90'}}]])
    assert.throws(() => supplierDocumentMatchInput({...reviewed, pdfObservations: observations}), RequestNotSentError);
  assert.throws(() => supplierDocumentMatchInput({...pdfInput, ocrResultId: pdfObservation.candidate.resultId}), RequestNotSentError);
  const currency = {...pdfObservation, field: 'currency' as const, candidate: {...pdfObservation.candidate, field: 'currency' as const}};
  assert.throws(() => supplierDocumentMatchInput({...reviewed, pdfObservations: [currency, pdfObservation]}), RequestNotSentError);
  assert.throws(() => supplierDocumentMatchInput({...reviewed, pdfObservations: [pdfObservation, {...currency, candidate: {...currency.candidate, resultHash: '3'.repeat(64)}}]}), RequestNotSentError);
});
test('explicit PDF observations bind actor draft submission original and page without autofilling manual facts', () => {
  const selection: PdfDocumentObservationSelection = {source: {actorId: actor, sourceId: draftId, sourceRevision: '1', submissionId: document.id,
    sourceKind: 'SupplierInvoice', evidence: {...pdfEvidence, version: '1'}}, reference: pdfObservation.candidate,
    candidate: {field: 'total_amount', value: '2345678.90', confidence: '0.97', boundingBox: ['0.1','0.2','0.3','0.1'],
      pdfCanvasCorners: [{x:1,y:2},{x:4,y:2},{x:4,y:3},{x:1,y:3}], flags: []}};
  const remembered = rememberSupplierDocumentPdfObservation(actor, pdfOwner, selection);
  assert.equal(remembered.value, '2345678.90'); assert.deepEqual(remembered.reference, pdfObservation);
  const command = prepareSupplierDocumentMatch(actor, pdfOwner, pdfInput, {...matchedPreview,input:pdfInput}, null, 'Fakta manual dan PDF asli diperiksa');
  assert.equal(command.body.input.reviewedInput.documentTotal, '25.95'); assert.equal(command.body.input.lines[0].packages, 1);
  assert.equal(command.body.input.ocrResultId, null); assert.equal('value' in command.body.input.pdfObservations![0].candidate, false);
  for (const source of [{...selection.source,actorId:'99'},{...selection.source,sourceId:'99'},{...selection.source,submissionId:'99'},
    {...selection.source,sourceKind:'Payment' as const},{...selection.source,evidence:{...selection.source.evidence,sha256:'f'.repeat(64)}}])
    assert.throws(() => rememberSupplierDocumentPdfObservation(actor,pdfOwner,{...selection,source}));
  assert.throws(() => prepareSupplierDocumentMatch(actor, submitted, pdfInput, {...matchedPreview,input:pdfInput}, null, 'Wrong original'), RequestNotSentError);
});
test('annotated supplier match history retains exact provenance and rejects another private original', () => {
  const annotated = {id:'980',draftId,documentId:document.id,version:'1',input:pdfInput,before:capacity,
    after:{...capacity,version:'1',lines:[{...capacity.lines[0],matchedPackages:'1',remainingPackages:'1',matchedNetAmount:'12.34',matchedTaxAmount:'0.60',matchedChargeAmount:'0.02'}]},
    lines:[resolved],state:'match_recorded',recordedAt:matchedPreview.observedAt,reason:'PDF asli dan alokasi diperiksa'};
  const value = {...pdfOwner,matches:[annotated]};
  assert.deepEqual(parseSupplierDocumentRead(value).matches[0].input.pdfObservations,[pdfObservation]);
  const masked = {...value,document:{...pdfOwner.document!,evidence:null,reservationId:null},matches:[{...annotated,input:reviewed}]};
  assert.equal(Object.hasOwn(parseSupplierDocumentRead(masked).matches[0].input,'pdfObservations'),false);
  assert.throws(() => parseSupplierDocumentRead({...value,document:{...pdfOwner.document!,evidence:image.reference}}), AccessFailure);
});
test('lost annotated matching reply keeps only actor operation key time and recovers without replay', async () => {
  const storage=new SourceMemoryStorage(), journal=new OperationJournal(() => storage), ledger=new MutationLedger(() => matchKey);
  let writes=0, lookups=0;
  const command=prepareSupplierDocumentMatch(actor,pdfOwner,pdfInput,{...matchedPreview,input:pdfInput},null,'PDF dan fakta manual diperiksa');
  const gateway=createSupplierDocumentGateway(createUnitTokoClient({baseUrl:'https://unit.test',fetch:async input => {
    const request=new Request(input), path=new URL(request.url).pathname;
    if(path.endsWith('/csrf')) return Response.json({requestToken:'csrf'});
    assert.equal(request.headers.get('X-Expected-Actor-Id'),actor); assert.equal(request.headers.get('X-CSRF-Token'),'csrf');
    if(path.endsWith('/operations/query')) {lookups++;assert.deepEqual(await request.json(),{purpose:'operation-recovery',operation:matchResult.operation,key:matchKey});return Response.json({status:'committed',result:matchResult});}
    if(path.endsWith('/query')) return Response.json(pdfOwner);
    writes++; assert.equal(request.headers.get('Idempotency-Key'),matchKey); assert.equal(request.headers.get('If-Match'),'"1"');
    assert.deepEqual(await request.json(),command.body); throw new TypeError('Lost committed reply');
  }}));
  await assert.rejects(() => submitTrackedRequest(ledger,journal,actor,'supplier-document-match',matchResult.operation,command.body,key => gateway.match(command,key,() => true)));
  const [entry]=journal.list(actor); assert.deepEqual(Object.keys(entry).sort(),['actorId','createdAt','key','operation']);
  assert.equal(JSON.stringify(entry).includes(pdfEvidence.sha256),false);
  const neverLegacy={lookup:async () => assert.fail('Legacy lookup forbidden'),close:async () => assert.fail('Close forbidden')};
  assert.equal((await reconcileOperationRecovery(entry,journal,neverLegacy,{'procurement.supplierdocument':{lookup:entry => gateway.recover(entry.actorId,entry.operation,entry.key)}})).state,'committed');
  assert.equal(writes,1); assert.equal(lookups,1); assert.equal(journal.list(actor).length,0);
});

const qualitySource = { allocationId: '9007199254741110', proposalId: '9007199254741111', decisionId: '9007199254741112', receiptId: '9007199254741113', receiptReference: 'HELD-MIXED-001', acceptedPackages: '2', lotReference: inspectionInput.lines[0].lotCode, expiresOn: inspectionInput.lines[0].expiryDate, acceptedCondition: 'sealed_undamaged' as const, putawayReference: 'ACCEPTED-POSITION', segregationReference: 'SEPARATED-SUBSET', recordedAt: '2026-10-03T02:02:01.000Z' };
test('quality-linked invoice allocation is explicit and legacy null keeps the original clean wire shape', () => {
  assert.deepEqual(supplierDocumentMatchInput({ ...reviewed, lines: [{ ...allocate, qualityAllocationId: null }] }), supplierDocumentMatchInput(reviewed));
  const linked = supplierDocumentMatchInput({ ...reviewed, lines: [{ ...allocate, qualityAllocationId: qualitySource.allocationId }] });
  const shown = { ...matchedPreview, input: linked, lines: [{ ...resolved, qualityAllocationId: qualitySource.allocationId, quality: qualitySource }] };
  const saved = prepareSupplierDocumentMatch(actor, submitted, linked, shown, null, 'Independent accepted subset');
  assert.equal(saved.body.input.lines[0].qualityAllocationId, qualitySource.allocationId); assert.ok(Object.isFrozen(saved.body.input.lines));
  for (const changed of [{ quality: undefined }, { quality: { ...qualitySource, allocationId: '999' } }, { quality: { ...qualitySource, acceptedPackages: '0' } }, { quality: { ...qualitySource, lotReference: 'OTHER' } }, { quality: { ...qualitySource, expiresOn: '2026-10-05' } }, { quality: { ...qualitySource, matchedPackages: '0' } }, { quality: { ...qualitySource, acceptedCondition: 'intact_as_defined' as const } }]) assert.throws(() => prepareSupplierDocumentMatch(actor, submitted, linked, { ...shown, lines: [{ ...shown.lines[0], ...changed }] }, null, 'Denied'));
  assert.throws(() => prepareSupplierDocumentMatch(actor, submitted, reviewed, { ...matchedPreview, lines: [{ ...resolved, quality: qualitySource }] }, null, 'Unselected quality'));
  assert.throws(() => supplierDocumentMatchInput({ ...reviewed, lines: [{ ...allocate, qualityAllocationId: Number(qualitySource.allocationId) }] }));
  assert.throws(() => supplierDocumentMatchInput({ ...reviewed, lines: [{ ...allocate, qualityAllocationId: qualitySource.allocationId, discrepancy: 'Unresolved', inspectionId: null, inspectionLineNumber: null }] }));
  const ordered = supplierDocumentMatchInput({ ...reviewed, lines: [{ ...allocate, qualityAllocationId: '9007199254741114' }, { ...allocate, qualityAllocationId: qualitySource.allocationId }] });
  assert.deepEqual(ordered.lines.map(item => item.qualityAllocationId), [qualitySource.allocationId, '9007199254741114']);
});

test('quality picker binds document and inspection, authentic exact counters and scanned pagination', async () => {
  const item = { ...qualitySource, matchedPackages: '1', remainingPackages: '1' }, page = { documentId: document.id, inspectionId: allocate.inspectionId, inspectionLineNumber: 1, items: [item], nextAllocationId: null };
  assert.equal(parseSupplierDocumentQualityChoices(page).items[0].remainingPackages, '1');
  for (const changed of [{ acceptedPackages: 2 }, { remainingPackages: '2' }, { matchedPackages: '-1' }, { policyPeople: [] }]) assert.throws(() => parseSupplierDocumentQualityChoices({ ...page, items: [{ ...item, ...changed }] }));
  const sent: Request[] = []; let response: unknown = page;
  const client = createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async value => { const request = new Request(value); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); sent.push(request); return Response.json(response); } });
  const gateway = createSupplierDocumentGateway(client);
  const result = await gateway.qualityAllocations(actor, document.id, allocate.inspectionId, 1); assert.equal(result.items[0].allocationId, qualitySource.allocationId);
  assert.equal(sent[0].headers.get('X-Expected-Actor-Id'), actor); assert.equal(sent[0].headers.get('X-CSRF-Token'), 'csrf'); assert.equal(sent[0].cache, 'no-store');
  assert.deepEqual(await sent[0].json(), { purpose: 'supplier-document-review', inspectionId: allocate.inspectionId, inspectionLineNumber: 1, beforeAllocationId: null });
  for (const changed of [{ documentId: '999' }, { inspectionId: '999' }, { inspectionLineNumber: 2 }]) { response = { ...page, ...changed }; await assert.rejects(() => gateway.qualityAllocations(actor, document.id, allocate.inspectionId, 1), AccessFailure); }
  response = { ...page, items: [], nextAllocationId: '9007199254741109' }; assert.equal((await gateway.qualityAllocations(actor, document.id, allocate.inspectionId, 1, qualitySource.allocationId)).nextAllocationId, '9007199254741109');
  response = { ...page, items: [], nextAllocationId: qualitySource.allocationId }; await assert.rejects(() => gateway.qualityAllocations(actor, document.id, allocate.inspectionId, 1, qualitySource.allocationId), AccessFailure);
});

test('unknown invoice facts stay null with explicit discrepancy while SPH amounts never invent packages', () => {
  assert.deepEqual(supplierDocumentInput(input), input); assert.equal(draftCommand().body.input.lines[0].sourceAccount, '0007'); assert.ok(Object.isFrozen(draftCommand().body.input.lines));
  const unknown = supplierDocumentInput({ ...input, documentDate: null, documentTotal: null, discrepancy: 'Tanggal dan nilai belum terbaca', lines: [{ ...line, packages: null, netPricePerPackage: null, taxAmount: null, chargeAmount: null }] });
  assert.equal(unknown.lines[0].packages, null); assert.equal(unknown.documentTotal, null);
  const sph = supplierDocumentInput({ ...input, kind: 'sph', documentTotal: '25.95', lines: [{ ...line, packages: null, netPricePerPackage: null, taxAmount: null, chargeAmount: null, statementAmount: '25.95', relatedInvoiceReference: '000INV-a' }] }); assert.equal(sph.lines[0].packages, null);
  assert.equal(supplierDocumentInput({ ...input, documentReference: 'A\u200bB' }).documentReference, 'A\u200bB');
  for (const changed of [{ ...unknown, discrepancy: null }, { ...input, documentTotal: '1.00' }, { ...sph, lines: [{ ...sph.lines[0], packages: 1 }] }, { ...input, lines: [{ ...line, packages: '2' }] }, { ...input, lines: [{ ...line, taxAmount: 0 }] }, { ...input, anchorOrderId: Number(draftId) }, { ...input, documentDate: '2026-02-30' }, { ...input, documentDate: '1999-12-31' }, { ...input, documentReference: 'INV\n' }, { ...input, paid: true }]) assert.throws(() => supplierDocumentInput(changed), RequestNotSentError);
});

test('matching preserves known original facts, explicit partial amounts, exact source identity and open discrepancies', () => {
  assert.deepEqual(matchCommand().body.input, reviewed);
  const discrepancy = supplierDocumentMatchInput({ ...reviewed, lines: [{ ...allocate, inspectionId: null, inspectionLineNumber: null, packages: null, netAmount: null, taxAmount: null, chargeAmount: null, discrepancy: 'Kode belum cocok' }] }); assert.equal(discrepancy.lines[0].inspectionId, null);
  for (const changed of [{ ...reviewed, lines: [{ ...allocate, taxAmount: null }] }, { ...reviewed, lines: [{ ...allocate, invoiceMatchId: '999' }] }, { ...reviewed, lines: [allocate, allocate] }, { ...reviewed, lines: [{ ...allocate, documentLineNumber: '1' }] }, { ...reviewed, ocrResultId: 970 }]) assert.throws(() => supplierDocumentMatchInput(changed), RequestNotSentError);
  const changed = { ...reviewed, reviewedInput: { ...input, lines: [{ ...line, sourceCode: '00Ab-02' }] } };
  assert.throws(() => prepareSupplierDocumentMatch(actor, submitted, changed, { ...matchedPreview, input: changed }, null, 'Do not silently rewrite original'), RequestNotSentError);
  assert.throws(() => prepareSupplierDocument(actor, { ...target, location: { ...location, warehouseId: '999' }, warehouse: { ...target.warehouse, id: '999' } }, order, input, preview, draft.reason), RequestNotSentError);
  for (const changed of [{ ...submitted, privatePath: 'secret' }, { ...submitted, document: { ...document, evidence: null } }, { ...submitted, reviewedInput: { ...input, documentReference: 'DIFFERENT' } }, { ...submitted, draft: { ...submitted.draft, supplierId: '999' } }]) assert.throws(() => parseSupplierDocumentRead(changed), AccessFailure);
});

test('OCR suggestions bind the exact private original and only highlight genuine authorized reference matches', () => {
  const parsed = parseSupplierDocumentOcr(extraction), available = { documentId: document.id, items: [{ inspectionId: '960', notice, intake, input: inspectionInput, order, capacity: { ...capacity, lines: [{ ...capacity.lines[0], packages: 3, remainingPackages: '3' }] } }], nextInspectionId: null };
  const suggestions = supplierDocumentSuggestions(submitted, parsed, available); assert.equal(suggestions.length, 1); assert.equal(suggestions[0].inspectionId, '960'); assert.deepEqual(suggestions[0].signals, ['Referensi pesanan']); assert.equal(suggestions[0].ocrResultId, '970');
  const noSignal = parseSupplierDocumentOcr({ ...extraction, fields: extraction.fields.map(field => ({ ...field, value: null, confidence: null, boundingBox: null, flags: ['unknown'] })) }); assert.equal(supplierDocumentSuggestions(submitted, noSignal, available).length, 0);
  assert.throws(() => supplierDocumentSuggestions(submitted, { ...parsed, evidence: { ...parsed.evidence, version: '4' } }, available), AccessFailure);
  const sent = { ...reviewed, ocrResultId: parsed.resultId }; assert.equal(prepareSupplierDocumentMatch(actor, submitted, sent, { ...matchedPreview, input: sent }, parsed, 'Read original and OCR').body.input.ocrResultId, '970');
  assert.throws(() => prepareSupplierDocumentMatch(actor, submitted, sent, { ...matchedPreview, input: sent }, { ...parsed, draftId: '999' }, 'Foreign original'), RequestNotSentError);
  for (const value of [{ ...extraction, fields: extraction.fields.slice(0, 7) }, { ...extraction, fields: [...extraction.fields.slice(0, 7), extraction.fields[0]] }, { ...extraction, qualityFlags: [] }, { ...extraction, lines: [{ ...extraction.lines[0], boundingBox: ['0.9', '0.1', '0.8', '0.1'] }] }, { ...extraction, privateFile: 'path' }]) assert.throws(() => parseSupplierDocumentOcr(value), AccessFailure);
});

test('recording and matching transport use actor, CSRF, version and exact frozen facts with no automatic writes after stale views', async () => {
  const requests: Request[] = []; const gateway = createSupplierDocumentGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); return Response.json(matchResult); } }));
  assert.deepEqual(await gateway.match(matchCommand(), matchKey, () => true), matchResult); assert.equal(requests.length, 1); assert.equal(requests[0].headers.get('If-Match'), '"1"'); assert.equal(requests[0].headers.get('X-Expected-Actor-Id'), actor); assert.equal(requests[0].headers.get('X-CSRF-Token'), 'csrf'); assert.equal(requests[0].headers.get('Idempotency-Key'), matchKey); assert.deepEqual(await requests[0].json(), matchCommand().body);
  await assert.rejects(() => gateway.match(matchCommand(), matchKey, () => false), RequestNotSentError); assert.equal(requests.length, 1); assert.throws(() => parseSupplierDocumentResult(matchResult, `sd.match.99.${uuid}`, matchResult.operation));
});

test('draft, original reservation, submission and match lost replies recover only through original actor and key', async () => {
  for (const mode of ['draft', 'reserve', 'submit', 'match'] as const) {
    const key = mode === 'draft' ? draftKey : mode === 'submit' ? submitKey : mode === 'match' ? matchKey : reserveKey, op = mode === 'reserve' ? 'evidence.supplier-document-image.reserve' : 'procurement.supplierdocument.' + mode;
    const saved = mode === 'draft' ? draftResult : mode === 'match' ? matchResult : submitResult, storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key), requests: Request[] = []; let confirmed = false;
    const gateway = createSupplierDocumentGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input), path = new URL(request.url).pathname; if (path.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request); if (path.endsWith('/operations/query')) return Response.json(mode === 'reserve' ? { status: confirmed ? 'committed' : 'not_confirmed', reservation: confirmed ? { reservationId: image.reservationId, revision: '1', expiresAt: image.expiresAt } : null } : { status: confirmed ? 'committed' : 'not_confirmed', result: confirmed ? saved : null }); if (path.endsWith('/query')) return Response.json(mode === 'draft' || mode === 'reserve' ? read : submitted); throw new TypeError('Lost committed response'); } }));
    const send = async () => mode === 'draft' ? gateway.createDraft(draftCommand(), key, () => true) : mode === 'submit' ? gateway.submit(submitCommand(), key, () => true) : mode === 'match' ? gateway.match(matchCommand(), key, () => true) : gateway.reserve(actor, draftId, key, () => true);
    await assert.rejects(() => submitTrackedRequest(ledger, journal, actor, 'supplier-document', op, { target: draftId }, send)); const [entry] = journal.list(actor); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.equal(journal.list('99').length, 0);
    const adapters = { 'procurement.supplierdocument': { lookup: (item: typeof entry) => gateway.recover(item.actorId, item.operation, item.key) }, 'evidence.supplier-document-image': { lookup: (item: typeof entry) => gateway.recoverReservation(item.actorId, item.key) } };
    assert.equal(operationRecoveryCapabilities(op, adapters).canClose, false); const generic = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
    assert.equal((await reconcileOperationRecovery(entry, journal, generic, adapters)).state, 'unconfirmed'); assert.equal(journal.list(actor).length, 1); confirmed = true; assert.equal((await reconcileOperationRecovery(entry, journal, generic, adapters)).state, 'committed'); assert.equal(journal.list(actor).length, 0);
    assert.equal(requests.filter(request => !new URL(request.url).pathname.endsWith('/query')).length, 1); assert.ok(requests.every(request => request.headers.get('X-Expected-Actor-Id') === actor));
  }
});

test('private original requires clean decoder status and verified MIME length and digest; completion alone is not approval', async () => {
  assert.equal(supplierDocumentEvidenceReady(image), true); assert.equal(supplierDocumentEvidenceReady({ ...image, scanStatus: 'unavailable', decoderStatus: 'unavailable' }), false); assert.throws(() => supplierDocumentFileAllowed({ size: 10 * 1024 * 1024 + 1, type: 'image/png' }), RequestNotSentError);
  assert.equal(parseSupplierDocumentEvidence(image).reference?.version, '3');
  for (const invalid of [{ ...image, reference: { ...image.reference, version: 3 } }, { ...image, reference: { ...image.reference, version: '0' } }, { ...image, scanStatus: 'unavailable' }, { ...image, safeFailure: 'invalid_media' }, { ...image, state: 'fresh' }, { ...image, privatePath: 'not-public' }]) assert.throws(() => parseSupplierDocumentEvidence(invalid), AccessFailure);
  const bytes = new TextEncoder().encode('synthetic original fixture'), digest = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join(''); let mime = 'image/png';
  const gateway = createSupplierDocumentGateway(createUnitTokoClient({ baseUrl: 'https://unit.test', fetch: async input => { const request = new Request(input); if (new URL(request.url).pathname.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); return new Response(bytes, { headers: { 'Content-Type': mime, 'Content-Length': String(bytes.length) } }); } }));
  assert.equal((await gateway.original(actor, draftId, { ...image, reference: { ...image.reference, sha256: digest } }, () => true)).type, 'image/png'); mime = 'text/html'; await assert.rejects(() => gateway.original(actor, draftId, image, () => true), AccessFailure); mime = 'image/jpeg'; await assert.rejects(() => gateway.original(actor, draftId, image, () => true), AccessFailure);
});

test('supplier document navigation uses actual read access and canonical fresh operation names', () => {
  assert.equal(identityReturnPath('/procurement/supplier-documents'), '/procurement/supplier-documents'); assert.ok(identityDestinations(['procurement.read']).some(item => item.href === '/procurement/supplier-documents')); assert.ok(!identityDestinations(['funding.read']).some(item => item.href === '/procurement/supplier-documents'));
  assert.equal(recoveryPage('evidence.supplier-document-image.reserve').href, '/procurement/supplier-documents');
  for (const permission of ['procurement.supplier.document.record', 'procurement.supplier.document.match']) assert.equal(stepUpFields(parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] }), permission, '/procurement/supplier-documents').returnPath, '/procurement/supplier-documents');
});
