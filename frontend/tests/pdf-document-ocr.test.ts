import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {PdfDocumentOcrPanel} from '../src/modules/documents/PdfDocumentOcrPanel';
import {createPdfDocumentOcrReader, parsePdfDocumentOcr, paymentPdfFields, supplierPdfFields, selectPdfDocumentObservation,
  type PdfDocumentOcrOwner, type PdfDocumentOcrView} from '../src/modules/documents/pdf-document-ocr-client';

const owner: PdfDocumentOcrOwner = {actorId: '9007199254740993', sourceId: '9223372036854775806', sourceRevision: '9007199254740995',
  submissionId: '9007199254740997', sourceKind: 'Payment', evidence: {id: '9007199254740999', version: '1', sha256: 'a'.repeat(64)}};
const provenance = {engineVersion: 'tesseract-pinned', modelVersion: 'ind-eng-v1', modelSha256: 'b'.repeat(64), runtimeSha256: 'c'.repeat(64), preprocessingVersion: 'preprocess-v1'};
const box: [string, string, string, string] = ['0.1', '0.2', '0.3', '0.1'];
const corners = [{x: 3.6, y: 57.6}, {x: 14.4, y: 57.6}, {x: 14.4, y: 50.4}, {x: 3.6, y: 50.4}];
function fixture(kind: 'Payment' | 'SupplierInvoice' = 'Payment'): PdfDocumentOcrView {
  const names = kind === 'Payment' ? paymentPdfFields : supplierPdfFields;
  const pages = [0, 1].map(pageIndex => ({raster: {pageIndex, width: 100, height: 200, rotationDegrees: 0 as const, length: 200, sha256: 'd'.repeat(64),
    canvasSize: [36, 72] as [number, number], visibleBox: [0, 0, 36, 72] as [number, number, number, number],
    pixelCornersInPdfCanvas: [{x: 0, y: 72}, {x: 36, y: 72}, {x: 36, y: 0}, {x: 0, y: 0}] as typeof corners & [{x: number; y: number}, {x: number; y: number}, {x: number; y: number}, {x: number; y: number}],
    reviewReasons: ['human_review_required' as const]}, ocrProvenance: structuredClone(provenance),
    preprocessing: {orientationDegrees: 0 as const, deskewDegrees: 0, contrastApplied: true, workingWidth: 100, workingHeight: 200,
      orientationScore: .95, orientationMargin: .2, reviewReasons: []},
    lines: [{text: `Nominal: Rp ${pageIndex ? '200' : '100'}.00`, confidence: '0.89', boundingBox: structuredClone(box),
      pdfCanvasCorners: structuredClone(corners) as [{x: number; y: number}, {x: number; y: number}, {x: number; y: number}, {x: number; y: number}]}],
    fields: names.map(field => field === 'amount' || field === 'total_amount' || field === 'currency' ?
      {field, value: field === 'currency' ? 'IDR' : pageIndex ? '200.00' : '100.00', confidence: '0.89', boundingBox: structuredClone(box),
        pdfCanvasCorners: structuredClone(corners) as [{x: number; y: number}, {x: number; y: number}, {x: number; y: number}, {x: number; y: number}], flags: ['low_confidence' as const]} :
      {field, value: null, confidence: null, boundingBox: null, pdfCanvasCorners: null, flags: ['unknown' as const]}), reviewReasons: ['human_review_required', 'low_confidence']}));
  const fields = names.map((field, index) => ({field, observations: pages.map(page => ({pageIndex: page.raster.pageIndex, candidate: structuredClone(page.fields[index])})),
    reviewReasons: ['human_review_required' as const, field === 'currency' ? 'repeated_across_pages' as const : field === 'amount' || field === 'total_amount' ?
      'contradictory_pages' as const : 'unknown_on_page' as const]}));
  return {sourceId: owner.sourceId, sourceRevision: owner.sourceRevision, submissionId: owner.submissionId, sourceKind: kind, evidence: structuredClone(owner.evidence),
    state: 'succeeded', failureCode: null, result: {id: '9007199254741001', resultHash: 'e'.repeat(64), extractedAt: '2026-10-05T10:00:00Z', purpose: kind,
      rasterProvenance: {pipeline: 'pdf-page-raster-v1', wrapper: 'pypdfium2-pinned', pdfium: 'pdfium-pinned', wheelSha256: 'f'.repeat(64), pillow: 'pillow-pinned', runtimeSha256: '1'.repeat(64)},
      ocrProvenance: structuredClone(provenance), pages, fields, requiresHumanReview: true}};
}
const invalid = (value: unknown, selected = owner) => assert.throws(() => parsePdfDocumentOcr(value, selected), {code: 'pdf_document_ocr_response_invalid'});
const changed = (update: (value: PdfDocumentOcrView) => void) => { const value = fixture(); update(value); return value; };
const syncFields = (view: PdfDocumentOcrView) => { for (const [index, field] of view.result!.fields.entries()) for (const [pageIndex, observation] of field.observations.entries()) observation.candidate = structuredClone(view.result!.pages[pageIndex].fields[index]); };

test('PDF observations preserve lossless IDs, every page, unknowns, contradictions and repetitions', () => {
  const value = parsePdfDocumentOcr(fixture(), owner);
  assert.equal(value.sourceId, '9223372036854775806'); assert.equal(value.sourceRevision, '9007199254740995');
  assert.equal(value.result!.pages.length, 2); assert.deepEqual(value.result!.fields[0].reviewReasons, ['human_review_required', 'contradictory_pages']);
  assert.deepEqual(value.result!.fields[1].reviewReasons, ['human_review_required', 'repeated_across_pages']);
  assert.equal(value.result!.fields[2].observations[1].candidate.value, null);
  const supplierOwner = {...owner, sourceKind: 'SupplierInvoice' as const};
  assert.equal(parsePdfDocumentOcr(fixture('SupplierInvoice'), supplierOwner).result!.pages[0].fields[0].field, 'document_reference');
});

test('IDs, revisions and decimal geometry require exact string types and Int64 bounds', () => {
  for (const value of ['0', '01', '9223372036854775807', '9223372036854775808', '1e3', '12\n', 9007199254740993]) invalid({...fixture(), sourceId: value});
  for (const value of [.89, '0.89\n', 'NaN', '1.0000000000000000000000000001', '0.12345678901234567890123456789', '-0.1', '0e0']) {
    invalid(changed(view => { (view.result!.pages[0].lines[0] as unknown as {confidence: unknown}).confidence = value; }));
  }
  invalid(changed(view => { view.result!.pages[0].lines[0].boundingBox = ['0.9000000000000000000000000001', '0', '0.1', '0.1']; }));
  invalid(changed(view => { view.result!.pages[0].lines[0].boundingBox[2] = '0'; }));
});

test('source, original evidence, revision, submission and extraction purpose must all match', () => {
  for (const key of ['sourceId', 'sourceRevision', 'submissionId'] as const) invalid({...fixture(), [key]: '41'});
  invalid({...fixture(), sourceKind: 'SupplierInvoice'}); invalid({...fixture(), evidence: {...owner.evidence, sha256: '0'.repeat(64)}});
  invalid({...fixture(), evidence: {...owner.evidence, version: '2'}});
  invalid(changed(view => { view.result!.purpose = 'SupplierInvoice'; }));
  invalid({...fixture(), privatePath: 'untrusted'});
});

test('page and field identity must remain contiguous and complete without silently choosing an aggregate', () => {
  invalid(changed(view => { view.result!.pages.reverse(); })); invalid(changed(view => { view.result!.pages[1].raster.pageIndex = 0; }));
  invalid(changed(view => { view.result!.pages[1].fields.reverse(); })); invalid(changed(view => { view.result!.fields[0].observations[1].pageIndex = 0; }));
  invalid(changed(view => { view.result!.fields[0].observations[1].candidate.value = '100.00'; }));
  invalid(changed(view => { view.result!.fields[0].reviewReasons = ['human_review_required', 'repeated_across_pages']; }));
  invalid(changed(view => { view.result!.pages.pop(); }));
  invalid(changed(view => { view.result!.requiresHumanReview = false as true; }));
});

test('raster bounds, mapped corners, provenance and preprocessing cannot borrow another coordinate space', () => {
  invalid(changed(view => { view.result!.pages[0].raster.width = 101; })); invalid(changed(view => { view.result!.pages[0].raster.rotationDegrees = 90; }));
  invalid(changed(view => { view.result!.pages[0].lines[0].pdfCanvasCorners[0].x += .002; }));
  invalid(changed(view => { view.result!.pages[0].raster.pixelCornersInPdfCanvas[0].y = 0; }));
  invalid(changed(view => { view.result!.pages[0].fields[0].pdfCanvasCorners![0].x += 1; syncFields(view); }));
  invalid(changed(view => { view.result!.pages[0].fields[0].confidence = '0.88'; syncFields(view); }));
  invalid(changed(view => { view.result!.pages[0].ocrProvenance.modelSha256 = '0'.repeat(64); }));
  invalid(changed(view => { view.result!.pages[0].preprocessing.workingWidth = 0; }));
  invalid(changed(view => { view.result!.pages[0].preprocessing.orientationMargin = .01; }));
  invalid(changed(view => { view.result!.pages[0].preprocessing.deskewDegrees = .1; }));
});

test('a failed job has only a safe status and failure code; partial pages and suggestions are rejected', () => {
  const failed = {...fixture(), state: 'dead' as const, result: null, failureCode: 'pdf_raster_encrypted_pdf' as const};
  assert.equal(parsePdfDocumentOcr(failed, owner).result, null);
  invalid({...failed, result: fixture().result}); invalid({...failed, pages: fixture().result!.pages});
  invalid({...failed, failureCode: 'private filesystem path'}); invalid({...failed, state: 'running'});
  invalid(changed(view => { view.failureCode = 'ocr_timeout'; }));
});

test('text is bounded canonical Unicode, while confidence, unknown and mapped-field flags stay honest', () => {
  invalid(changed(view => { view.result!.pages[0].lines[0].text = 'e\u0301'; })); invalid(changed(view => { view.result!.pages[0].lines[0].text = '\uD800'; }));
  invalid(changed(view => { view.result!.pages[0].lines[0].text = 'x'.repeat(513); }));
  invalid(changed(view => { view.result!.pages[0].fields[0].flags = []; syncFields(view); }));
  invalid(changed(view => { view.result!.pages[0].fields[0].flags = ['unknown', 'low_confidence']; syncFields(view); }));
  invalid(changed(view => { view.result!.pages[0].fields[0].pdfCanvasCorners = null; syncFields(view); }));
  invalid(changed(view => { view.result!.pages[0].fields[0].value = '1e3'; syncFields(view); }));
  invalid(changed(view => { view.result!.pages[0].fields[1].value = 'USD'; syncFields(view); }));
});

test('explicit selection returns the exact page/field/result/original reference and is isolated from mutation', () => {
  const view = fixture(), selected = selectPdfDocumentObservation(view, owner, 1, 'amount');
  assert.deepEqual(selected.reference, {resultId: '9007199254741001', resultHash: 'e'.repeat(64), evidence: owner.evidence, pageIndex: 1, field: 'amount'});
  assert.equal(selected.source.actorId, owner.actorId); assert.equal(selected.candidate.value, '200.00');
  selected.reference.evidence.sha256 = '0'.repeat(64); selected.candidate.value = 'changed';
  assert.equal(owner.evidence.sha256, 'a'.repeat(64)); assert.equal(view.result!.pages[1].fields[0].value, '200.00');
  assert.throws(() => selectPdfDocumentObservation(view, owner, 2, 'amount'));
  assert.throws(() => selectPdfDocumentObservation(view, owner, .5, 'amount'));
});

test('reader fences actor/source changes before dispatch and after replies without retries', async () => {
  let actorId: string | null = owner.actorId, currentOwner: PdfDocumentOcrOwner | null = owner, calls = 0;
  let finish: ((value: unknown) => void) | undefined, signal: AbortSignal | undefined;
  const reader = createPdfDocumentOcrReader({currentActorId: () => actorId, currentOwner: () => currentOwner,
    read: (_selected, requestSignal) => { calls++; signal = requestSignal; return new Promise(resolve => { finish = resolve; }); }});
  actorId = '41'; await assert.rejects(reader.query(owner), {code: 'pdf_document_ocr_stale'}); assert.equal(calls, 0);
  actorId = owner.actorId; const pending = reader.query(owner); currentOwner = {...owner, sourceRevision: '41'}; finish!(fixture());
  await assert.rejects(pending, {code: 'pdf_document_ocr_stale'}); assert.equal(signal!.aborted, true); assert.equal(calls, 1);
  currentOwner = owner; const second = reader.query(owner); reader.cancel(); finish!(fixture());
  await assert.rejects(second, {code: 'pdf_document_ocr_stale'}); assert.equal(calls, 2);
});

test('a newer query aborts and fences the older reply while the current complete response succeeds', async () => {
  const replies: ((value: unknown) => void)[] = [], signals: AbortSignal[] = [];
  const reader = createPdfDocumentOcrReader({currentActorId: () => owner.actorId, currentOwner: () => owner,
    read: (_selected, signal) => {signals.push(signal); return new Promise(resolve => replies.push(resolve));}});
  const old = reader.query(owner), current = reader.query(owner); assert.equal(signals[0].aborted, true);
  replies[1](fixture()); assert.equal((await current).result!.pages.length, 2); replies[0](fixture());
  await assert.rejects(old, {code: 'pdf_document_ocr_stale'}); assert.equal(replies.length, 2);
});

test('the viewer renders all pages and observations, no partial failure output, and hides stale actors', () => {
  const props = {owner, currentActorId: owner.actorId, view: fixture(), locked: false, loading: false, isCurrent: () => true, onRefresh: () => undefined,
    onSelectObservation: () => undefined};
  const html = renderToStaticMarkup(createElement(PdfDocumentOcrPanel, props));
  assert.equal((html.match(/data-pdf-ocr-page=/g) ?? []).length, 2);
  assert.match(html, /Nilai antarhalaman berbeda/); assert.match(html, /Nilai berulang/); assert.match(html, /Belum diketahui pada halaman ini/);
  assert.match(html, /Lokasi pada halaman asli/); assert.match(html, /Pilih rujukan halaman 2: Jumlah transfer/); assert.match(html, /disabled=""/);
  const stale = renderToStaticMarkup(createElement(PdfDocumentOcrPanel, {...props, currentActorId: '41'}));
  assert.doesNotMatch(stale, /200\.00|data-pdf-ocr-page=/); assert.match(stale, /Sesi atau dokumen berubah/);
  const failed = renderToStaticMarkup(createElement(PdfDocumentOcrPanel, {...props, view: {...props.view, state: 'dead', result: null, failureCode: 'ocr_timeout'}}));
  assert.doesNotMatch(failed, /data-pdf-ocr-page=/); assert.match(failed, /hasil sebagian tidak ditampilkan/);
});
