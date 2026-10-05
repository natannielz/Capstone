import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import * as React from 'react';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {PdfDocumentOcrPanel} from '../src/modules/documents/PdfDocumentOcrPanel';
import {createPdfDocumentOcrReader, parsePdfDocumentOcr, paymentPdfFields, supplierPdfFields, selectPdfDocumentObservation,
  type PdfDocumentOcrOwner, type PdfDocumentOcrView} from '../src/modules/documents/pdf-document-ocr-client';
import {AccessFailure} from '@/modules/administration/workflow';
import {PaymentSupplementRequesterPanel, PaymentSupplementReviewerPanel} from '../src/modules/billing/PaymentSupplementPanels';
import {createPaymentSupplementGateway, parsePaymentSupplement, parsePaymentSupplementList, parsePaymentSupplementOcr, parsePaymentSupplementResult,
  paymentSupplementOwner, paymentSupplementOwnerKey, paymentSupplementError, supplementBinding, supplementPdfOwner, supplementReason, type PaymentSupplement, type PaymentSupplementOwner} from '../src/modules/billing/payment-pdf-supplement-client';
import type {PaymentSupplementReviewProps} from '../src/modules/billing/PaymentSupplementReviewPanel';
import {SupplementJournal, recoverSupplement, sendSupplementOnce, supplementKey, supplementKeyBinding, supplementOperations} from '../src/modules/billing/payment-pdf-supplement-recovery';
import type {PaymentDraft} from '@/modules/billing/payment-client';
import {paymentCorrectionLabels, correctionFields} from '../src/modules/billing/payment-correction-client';

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

const primary: PaymentSupplementOwner = {actorId: owner.actorId, declarationId: '9007199254741101', declarationRevision: '2', submissionId: '9007199254741103',
  primaryEvidence: {id: '9007199254741105', version: '1', sha256: '2'.repeat(64)}};
const draftSupplement: PaymentSupplement = {declarationId: primary.declarationId, declarationRevision: primary.declarationRevision, submissionId: primary.submissionId,
  primaryEvidence: primary.primaryEvidence, supplementId: '9007199254741107', supplementRevision: '1', state: 'draft', reservationId: null, pdfEvidence: null, attachedAt: null, reason: 'Lampiran PDF tambahan untuk pengajuan foto'};
const reserved = {reservationId: '9007199254741109', revision: '1' as const, expiresAt: '2026-10-05T10:15:00Z'};
const attachedSupplement: PaymentSupplement = {...draftSupplement, supplementRevision: '2', state: 'attached', reservationId: reserved.reservationId,
  pdfEvidence: owner.evidence as PaymentSupplement['pdfEvidence'], attachedAt: '2026-10-05T10:00:00Z', reason: 'Lampiran tambahan telah diperiksa sebagai dokumen tambahan'};
const readyStatus = {...reserved, state: 'quarantined' as const, reference: attachedSupplement.pdfEvidence, scanStatus: 'clean' as const, decoderStatus: 'decoded' as const, safeFailure: null};
const requestUuid = '13ef8768-755a-40d4-8e06-204a94f6e9e2';
const json = (value: unknown) => new Response(JSON.stringify(value), {headers: {'Content-Type': 'application/json'}});
function gatewayFor(reply: (request: Request) => Response | Promise<Response>) {
  const requests: Request[] = []; let tokens = 0;
  const fetcher = (async (input: RequestInfo | URL) => {const request = input instanceof Request ? input : new Request(input); requests.push(request);
    return new URL(request.url).pathname === '/api/v1/identity/csrf' ? json({requestToken: `fresh-${++tokens}`}) : reply(request);}) as typeof fetch;
  return {requests, gateway: createPaymentSupplementGateway({fetcher, baseUrl: 'https://unit-toko.test'})};
}
class MemoryStorage {
  values = new Map<string, string>(); get length() {return this.values.size;} key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;} setItem(key: string, value: string) {this.values.set(key, value);} removeItem(key: string) {this.values.delete(key);}
}
function supplementOcr() {
  const source = supplementPdfOwner(primary, attachedSupplement), view = fixture();
  const inner = {...view, sourceId: source.sourceId, sourceRevision: source.sourceRevision, submissionId: source.submissionId, sourceKind: 'PaymentSupplement' as const};
  return {declarationId: primary.declarationId, declarationRevision: primary.declarationRevision, submissionId: primary.submissionId, primaryEvidence: primary.primaryEvidence,
    supplementId: attachedSupplement.supplementId, supplementRevision: attachedSupplement.supplementRevision, pdfEvidence: attachedSupplement.pdfEvidence!, ocr: inner};
}

test('supplement owner only comes from an existing submitted primary tuple and keeps lossless identity', () => {
  const payment = {id: primary.declarationId, revision: primary.declarationRevision, state: 'submitted', createdAt: '2026-10-05T09:00:00Z', updatedAt: '2026-10-05T10:00:00Z',
    input: {invoiceIds: ['41'], declaredAmount: '100.00', transferDate: '2026-10-05', payerName: 'Cabang', bankReference: null, note: null},
    invoices: [{invoiceId: '41', number: 'INV-41', total: '100.00', buyerName: 'Cabang', sellerName: 'Unit Toko'}],
    submission: {id: primary.submissionId, reviewRevision: '1', reservationId: '43', evidence: primary.primaryEvidence, submittedAt: '2026-10-05T10:00:00Z'}} as PaymentDraft;
  assert.deepEqual(paymentSupplementOwner(primary.actorId, payment), primary);
  assert.throws(() => paymentSupplementOwner(primary.actorId, {...payment, state: 'draft', submission: null}), {code: 'payment_primary_image_required'});
});

test('draft/attached supplement state, exact primary image and distinct PDF association cannot be repaired', () => {
  assert.deepEqual(parsePaymentSupplement(draftSupplement, primary), draftSupplement); assert.deepEqual(parsePaymentSupplement(attachedSupplement, primary), attachedSupplement);
  for (const key of ['declarationId', 'declarationRevision', 'submissionId', 'supplementId'] as const)
    assert.throws(() => parsePaymentSupplement({...attachedSupplement, [key]: '41'}, primary, attachedSupplement.supplementId));
  for (const wrong of [{...draftSupplement, pdfEvidence: attachedSupplement.pdfEvidence}, {...attachedSupplement, supplementRevision: '1'},
    {...attachedSupplement, reservationId: null}, {...attachedSupplement, pdfEvidence: primary.primaryEvidence}, {...attachedSupplement, attachedAt: null},
    {...attachedSupplement, primaryEvidence: {...primary.primaryEvidence, sha256: '3'.repeat(64)}}, {...attachedSupplement, supplementId: 9007199254741107},
    {...attachedSupplement, supplementId: '9223372036854775807'}, {...attachedSupplement, approvalId: '41'}]) assert.throws(() => parsePaymentSupplement(wrong, primary));
  assert.equal(parsePaymentSupplementResult({operation: supplementOperations.create, result: draftSupplement}, supplementOperations.create, primary).state, 'draft');
  assert.throws(() => parsePaymentSupplementResult({operation: supplementOperations.attach, result: draftSupplement}, supplementOperations.attach, primary));
});

test('page-aware supplement OCR binds parent image plus its own kind/id/revision/hash and never relabels primary', () => {
  const wrapped = supplementOcr(), parsed = parsePaymentSupplementOcr(wrapped, primary, attachedSupplement);
  assert.equal(parsed.owner.sourceKind, 'PaymentSupplement'); assert.equal(parsed.view.result!.purpose, 'Payment');
  assert.equal(parsed.owner.sourceId, attachedSupplement.supplementId); assert.equal(parsed.owner.submissionId, attachedSupplement.supplementId);
  const chosen = selectPdfDocumentObservation(parsed.view, parsed.owner, 1, 'amount'); assert.equal(chosen.candidate.value, '200.00');
  assert.deepEqual(chosen.reference.evidence, attachedSupplement.pdfEvidence);
  for (const wrong of [{...wrapped, primaryEvidence: {...primary.primaryEvidence, sha256: '3'.repeat(64)}}, {...wrapped, submissionId: '41'},
    {...wrapped, supplementRevision: '1'}, {...wrapped, pdfEvidence: primary.primaryEvidence}, {...wrapped, ocr: {...wrapped.ocr, sourceKind: 'Payment'}},
    {...wrapped, ocr: {...wrapped.ocr, submissionId: primary.submissionId}}, {...wrapped, ocr: {...wrapped.ocr, evidence: primary.primaryEvidence}}])
    assert.throws(() => parsePaymentSupplementOcr(wrong, primary, attachedSupplement));
  const html = renderToStaticMarkup(createElement(PdfDocumentOcrPanel, {owner: parsed.owner, currentActorId: primary.actorId, view: parsed.view, locked: false, loading: false,
    isCurrent: () => true, onRefresh: () => undefined})); assert.match(html, /Halaman 1 dari 2/); assert.match(html, /Halaman 2 dari 2/); assert.match(html, /Nilai antarhalaman berbeda/);
});

test('pagination remains exact to one immutable primary submission and policy unavailable is not approval', () => {
  const data = {items: [attachedSupplement], nextSupplementId: null, observedAt: '2026-10-05T10:00:00Z', readiness: 'policy_unavailable'};
  assert.equal(parsePaymentSupplementList(data, primary, null).readiness, 'policy_unavailable');
  for (const wrong of [{...data, readiness: 'approved'}, {...data, items: [attachedSupplement, attachedSupplement]},
    {...data, nextSupplementId: attachedSupplement.supplementId}, {...data, items: [{...attachedSupplement, declarationRevision: '41'}]}])
    assert.throws(() => parsePaymentSupplementList(wrong, primary, null));
  assert.throws(() => parsePaymentSupplementList(data, primary, attachedSupplement.supplementId));
  assert.match(paymentSupplementError(new AccessFailure(409, 'payment_supplement_limit')), /batas kebijakan unit.*Hubungi penanggung jawab/);
  assert.match(paymentSupplementError(new AccessFailure(400, 'payment_supplement_policy_unavailable')), /belum diizinkan.*foto utama tetap tersedia/);
});

test('create/attach transport has distinct operations, real CSRF/actor/version and no primary submission writes', async () => {
  const {gateway, requests} = gatewayFor(request => request.url.endsWith('/attach') ? json({operation: supplementOperations.attach, result: attachedSupplement}) : json({operation: supplementOperations.create, result: draftSupplement}));
  const createKey = supplementKey(supplementOperations.create, supplementBinding(primary, null), requestUuid), attachKey = supplementKey(supplementOperations.attach, supplementBinding(primary, draftSupplement), requestUuid);
  await gateway.create(primary, createKey, ' Tambahan e\u0301 ', () => true); await gateway.attach(primary, draftSupplement, readyStatus, 'Lampiran tambahan', attachKey, () => true);
  const posts = requests.filter(item => item.method === 'POST'); assert.equal(posts.length, 2);
  assert.equal(new URL(posts[0].url).pathname, `/api/v1/billing/payment-declarations/${primary.declarationId}/supplements`);
  assert.equal(new URL(posts[1].url).pathname, `/api/v1/billing/payment-declarations/${primary.declarationId}/supplements/${draftSupplement.supplementId}/attach`);
  assert.deepEqual(await posts[0].json(), {submissionId: primary.submissionId, primaryEvidence: primary.primaryEvidence, reason: 'Tambahan é'});
  assert.deepEqual(await posts[1].json(), {reservationId: reserved.reservationId, evidence: attachedSupplement.pdfEvidence, reason: 'Lampiran tambahan'});
  assert.deepEqual(posts.map(item => item.headers.get('If-Match')), ['"2"', '"1"']); assert.deepEqual(posts.map(item => item.headers.get('X-CSRF-Token')), ['fresh-1', 'fresh-2']);
  for (const request of posts) {assert.equal(request.headers.get('X-Expected-Actor-Id'), primary.actorId); assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');}
  for (const wrong of [{...readyStatus, scanStatus: 'infected', decoderStatus: 'unavailable', safeFailure: 'infected_media'}, {...readyStatus, decoderStatus: 'unavailable'}])
    await assert.rejects(gateway.attach(primary, draftSupplement, wrong as typeof readyStatus, 'Tambahan', attachKey, () => true));
  assert.equal(requests.filter(item => item.method === 'POST').length, 2);
});

test('requester and independent reviewer reads use separate routes/purposes and exact supplement revision', async () => {
  for (const context of ['requester', 'reviewer'] as const) {
    const {gateway, requests} = gatewayFor(request => request.url.endsWith('/pdf-ocr/query') ? json(supplementOcr()) :
      request.url.endsWith('/supplements/query') ? json({items: [attachedSupplement], nextSupplementId: null, observedAt: '2026-10-05T10:00:00Z', readiness: 'allowed'}) : json(attachedSupplement));
    await gateway.list(context, primary, null, () => true); await gateway.detail(context, primary, attachedSupplement.supplementId, () => true); await gateway.ocr(context, primary, attachedSupplement, () => true);
    const posts = requests.filter(request => request.method === 'POST'), name = context === 'requester' ? 'payment' : 'payment-review';
    assert.equal(new URL(posts[0].url).pathname, `/api/v1/billing/${context === 'requester' ? 'payment-declarations' : 'payment-reviews'}/${primary.declarationId}/supplements/query`);
    assert.deepEqual(await posts[0].json(), {purpose: `${name}-supplements-list`, beforeId: null}); assert.deepEqual(await posts[1].json(), {purpose: `${name}-supplement-detail`});
    assert.deepEqual(await posts[2].json(), {purpose: `${name}-supplement-pdf-ocr-view`, reference: attachedSupplement.pdfEvidence});
    assert.deepEqual(posts.map(request => request.headers.get('If-Match')), ['"2"', '"2"', '"2"']);
  }
});

test('reservation/upload/recovery remain under the supplement association and never attach on their own', async () => {
  const {gateway, requests} = gatewayFor(request => request.url.endsWith('/operations/query') ? json({status: 'committed', reservation: reserved}) :
    request.url.endsWith('/content') || request.url.endsWith('/recover') ? json({state: 'quarantined', reference: attachedSupplement.pdfEvidence}) :
    request.url.endsWith('/pdf-evidence/query') ? json({items: [readyStatus]}) : json(reserved));
  const key = supplementKey(supplementOperations.reserve, supplementBinding(primary, draftSupplement), requestUuid);
  await gateway.reserve(primary, draftSupplement, key, () => true); await gateway.upload(primary, draftSupplement, reserved, new File(['pdf'], 'additional.pdf', {type: 'application/pdf'}), () => true);
  await gateway.reservations(primary, draftSupplement, null, () => true); await gateway.recoverReservation(primary, draftSupplement, key, () => true); await gateway.recoverUpload(primary, draftSupplement, reserved.reservationId, () => true);
  const posts = requests.filter(item => item.method === 'POST'); assert.equal(posts.length, 5);
  assert.ok(posts.every(request => new URL(request.url).pathname.startsWith(`/api/v1/billing/payment-declarations/${primary.declarationId}/supplements/${draftSupplement.supplementId}/pdf-evidence`)));
  assert.equal(posts[0].headers.get('Idempotency-Key'), key); assert.equal(posts[1].headers.get('Idempotency-Key'), `upload.${reserved.reservationId}`); assert.equal(posts[1].headers.get('Content-Type'), 'application/octet-stream');
  assert.ok(posts.every(request => request.headers.get('If-Match') === '"1"')); assert.deepEqual(await posts[2].json(), {purpose: 'payment-supplement-pdf-status', beforeId: null});
  assert.deepEqual(await posts[3].json(), {purpose: 'operation-recovery', key});
});

test('supplement private original retains hash/bounds checks and separate independent reviewer route', async () => {
  const bytes = new TextEncoder().encode('%PDF-1.4\nprivate-additional-doc\n%%EOF\n'), digest = await crypto.subtle.digest('SHA-256', bytes);
  const expected = {...attachedSupplement, pdfEvidence: {...attachedSupplement.pdfEvidence!, sha256: Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')}};
  const headers = {'Content-Type': 'application/pdf', 'Content-Length': String(bytes.length), 'Cache-Control': 'private, no-store', 'X-Content-Type-Options': 'nosniff',
    'Content-Security-Policy': "default-src 'none'; sandbox", 'Content-Disposition': 'attachment; filename="additional.pdf"'};
  const {gateway, requests} = gatewayFor(() => new Response(bytes, {headers})); assert.deepEqual(new Uint8Array(await (await gateway.original('reviewer', primary, expected, () => true)).arrayBuffer()), bytes);
  const post = requests.find(item => item.method === 'POST')!; assert.equal(new URL(post.url).pathname, `/api/v1/billing/payment-reviews/${primary.declarationId}/supplements/${expected.supplementId}/pdf-evidence/${expected.reservationId}/original/query`);
  assert.deepEqual(await post.json(), {purpose: 'payment-review-supplement-pdf-view', reference: expected.pdfEvidence}); assert.equal(post.headers.get('If-Match'), '"2"');
  await assert.rejects(gateway.original('reviewer', primary, attachedSupplement, () => true), {code: 'pdf_evidence_response_invalid'});
  for (const [header, value] of [['Cache-Control', 'public'], ['Content-Length', '10485761'], ['Content-Disposition', 'inline']] as const) {
    const bad = gatewayFor(() => new Response(bytes, {headers: {...headers, [header]: value}})); await assert.rejects(bad.gateway.original('requester', primary, expected, () => true));
  }
});

test('supplement keys and journal bind exact actors/parent/submission/attachment without storing form or PDF', () => {
  const storage = new MemoryStorage(), journal = new SupplementJournal(() => storage, () => new Date('2026-10-05T10:00:00Z'));
  for (const operation of Object.values(supplementOperations)) {
    const binding = supplementBinding(primary, operation === supplementOperations.create ? null : draftSupplement), key = supplementKey(operation, binding, requestUuid);
    assert.ok(key.length <= 128); assert.deepEqual(supplementKeyBinding(operation, key, primary.actorId), binding); journal.remember(primary.actorId, operation, key);
    assert.throws(() => supplementKeyBinding(operation, key, '41')); assert.throws(() => supplementKey(operation, binding, '00000000-0000-0000-0000-000000000000'));
  }
  assert.equal(new SupplementJournal(() => storage).list(primary.actorId).length, 3); assert.deepEqual(journal.list('41'), []);
  for (const serialized of storage.values.values()) assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.throws(() => journal.remember(primary.actorId, supplementOperations.reserve, `pdf.p.${primary.actorId}.${primary.declarationId}.${requestUuid}`));
  assert.doesNotThrow(() => supplementKey(supplementOperations.attach, {actorId: '9223372036854775805', declarationId: '9223372036854775805', submissionId: '9223372036854775805', supplementId: '9223372036854775805'}, requestUuid));
  assert.throws(() => supplementKey(supplementOperations.attach, {actorId: '9223372036854775806', declarationId: '41', submissionId: '43', supplementId: '45'}, requestUuid));
});

test('lost attach reply survives reload and lookup only; null or stale replies never permit resend', async () => {
  const storage = new MemoryStorage(), journal = new SupplementJournal(() => storage), key = supplementKey(supplementOperations.attach, supplementBinding(primary, draftSupplement), requestUuid);
  let writes = 0, reads = 0, current = true;
  await assert.rejects(sendSupplementOnce(journal, {actorId: primary.actorId, operation: supplementOperations.attach, key}, async () => {writes++; throw new AccessFailure(503, 'commit_outcome_unknown');}));
  const reloaded = new SupplementJournal(() => storage), [entry] = reloaded.list(primary.actorId);
  await assert.rejects(sendSupplementOnce(reloaded, {actorId: primary.actorId, operation: supplementOperations.attach, key}, async () => {writes++; return attachedSupplement;}), {code: 'resolve_previous_attempt'});
  assert.equal(await recoverSupplement(reloaded, entry, () => current, async () => {reads++; return null;}), null); assert.equal(reloaded.list(primary.actorId).length, 1);
  await assert.rejects(recoverSupplement(reloaded, entry, () => current, async () => {reads++; current = false; return attachedSupplement;}), {code: 'payment_supplement_view_stale'});
  assert.equal(reloaded.list(primary.actorId).length, 1); current = true;
  assert.equal(await recoverSupplement(reloaded, entry, () => current, async () => {reads++; return attachedSupplement;}), attachedSupplement);
  assert.equal(writes, 1); assert.equal(reads, 3); assert.deepEqual(reloaded.list(primary.actorId), []);
});

test('storage failure blocks dispatch and an actor/source change retains ambiguous writes without retries', async () => {
  let writes = 0; const key = supplementKey(supplementOperations.create, supplementBinding(primary, null), requestUuid);
  const unavailable = new SupplementJournal(() => {throw new Error('storage unavailable');});
  await assert.rejects(sendSupplementOnce(unavailable, {actorId: primary.actorId, operation: supplementOperations.create, key}, async () => {writes++; return draftSupplement;})); assert.equal(writes, 0);
  let current = false; const early = gatewayFor(() => {writes++; return json({operation: supplementOperations.create, result: draftSupplement});});
  await assert.rejects(early.gateway.create(primary, key, 'Tambahan', () => current), {code: 'payment_supplement_view_stale'}); assert.equal(early.requests.length, 0);
  current = true; let finish: ((response: Response) => void) | undefined;
  const late = gatewayFor(() => new Promise(resolve => {writes++; finish = resolve;})), pending = late.gateway.create(primary, key, 'Tambahan', () => current);
  while (!finish) await Promise.resolve(); current = false; finish(json({operation: supplementOperations.create, result: draftSupplement}));
  await assert.rejects(pending, {code: 'commit_outcome_unknown'}); assert.equal(writes, 1);
  assert.throws(() => supplementReason('unsafe\u0000')); assert.throws(() => supplementReason('\uD800')); assert.throws(() => supplementReason('x'.repeat(1001))); assert.throws(() => supplementReason('😀'.repeat(1001))); assert.equal(Array.from(supplementReason('😀'.repeat(1000))).length, 1000);
});

test('confirmed writes with journal cleanup failure stay usable and prohibit a second command', async () => {
  const storage = new MemoryStorage(), journal = new SupplementJournal(() => ({get length() {return storage.length;}, key: storage.key.bind(storage), getItem: storage.getItem.bind(storage),
    setItem: storage.setItem.bind(storage), removeItem() {throw new Error('cleanup unavailable');}}));
  const key = supplementKey(supplementOperations.create, supplementBinding(primary, null), requestUuid); let writes = 0;
  const result = await sendSupplementOnce(journal, {actorId: primary.actorId, operation: supplementOperations.create, key}, async () => {writes++; return draftSupplement;});
  assert.equal(result, draftSupplement); assert.equal(journal.list(primary.actorId).length, 1);
  await assert.rejects(sendSupplementOnce(journal, {actorId: primary.actorId, operation: supplementOperations.create, key}, async () => {writes++; return draftSupplement;}), {code: 'resolve_previous_attempt'});
  assert.equal(writes, 1);
});

test('attachment recovery parses exact committed binding and rejects missing, conflicting or extra receipts', async () => {
  const key = supplementKey(supplementOperations.attach, supplementBinding(primary, draftSupplement), requestUuid);
  const good = gatewayFor(() => json({status: 'committed', result: attachedSupplement}));
  assert.equal((await good.gateway.recoverCommand(supplementOperations.attach, primary, key, draftSupplement, () => true))!.supplementId, draftSupplement.supplementId);
  const post = good.requests.find(request => request.method === 'POST')!;
  assert.equal(new URL(post.url).pathname, `/api/v1/billing/payment-declarations/${primary.declarationId}/supplements/${draftSupplement.supplementId}/attachment/operations/query`);
  assert.deepEqual(await post.json(), {purpose: 'operation-recovery', key});
  for (const receipt of [{status: 'not_confirmed', result: attachedSupplement}, {status: 'committed', result: null}, {status: 'committed', result: draftSupplement},
    {status: 'committed', result: {...attachedSupplement, supplementId: '41'}}, {status: 'committed', result: attachedSupplement, extra: true}])
    await assert.rejects(gatewayFor(() => json(receipt)).gateway.recoverCommand(supplementOperations.attach, primary, key, draftSupplement, () => true));
  assert.equal(await gatewayFor(() => json({status: 'not_confirmed', result: null})).gateway.recoverCommand(supplementOperations.attach, primary, key, draftSupplement, () => true), null);
});

test('initial requester/reviewer UI keeps Indonesian supplementary copy and no primary PDF choice or reviewer writes', () => {
  const props = {owner: primary, locked: false, current: () => true, onDenied: () => undefined};
  const requester = renderToStaticMarkup(createElement(PaymentSupplementRequesterPanel, {...props, fresh: true, now: 0, journal: new SupplementJournal(() => new MemoryStorage())}));
  const reviewer = renderToStaticMarkup(createElement(PaymentSupplementReviewerPanel, props));
  for (const html of [requester, reviewer]) {assert.match(html, /Lampiran PDF tambahan/); assert.match(html, /Foto bukti pembayaran yang memenuhi syarat tetap wajib/);
    assert.doesNotMatch(html, /type="radio"/); assert.doesNotMatch(html, /Ajukan pembayaran dengan PDF/); assert.match(html, /Muat lampiran/);}
  assert.match(requester, /Buat draf lampiran tambahan/); assert.doesNotMatch(reviewer, /Buat draf|type="file"|Tautkan sebagai/);
});

test('actual supplementary review component aborts a changed session without refetching on callback rerenders', async () => {
  const hooks: unknown[] = [], effects = new Map<number, {dependencies?: readonly unknown[]; cleanup?: () => void}>();
  let position = 0, pending: {index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  const queries: {valid: () => boolean; signal: AbortSignal; finish: (value: {view: PdfDocumentOcrView}) => void}[] = [];
  const mockReact = {...React,
    useState(initial: unknown) {const index = position++; if (!(index in hooks)) hooks[index] = typeof initial === 'function' ? initial() : initial;
      return [hooks[index], (value: unknown) => {hooks[index] = typeof value === 'function' ? value(hooks[index]) : value;}];},
    useRef(initial: unknown) {return hooks[position++] ??= {current: initial};},
    useLayoutEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {pending.push({index: position++, effect, dependencies});}};
  const runtimeRequire = createRequire(resolve('package.json')), ts = runtimeRequire('typescript') as typeof import('typescript');
  const source = readFileSync(resolve('src/modules/billing/PaymentSupplementReviewPanel.tsx'), 'utf8');
  const code = ts.transpileModule(source, {compilerOptions: {module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
  const marker = () => null, evaluated: {exports: {PaymentSupplementReviewPanel?: (props: PaymentSupplementReviewProps) => React.ReactElement}} = {exports: {}};
  const imports = (name: string) => {
    if (name === 'react') return mockReact; if (name === 'react/jsx-runtime') return runtimeRequire(name);
    if (name.endsWith('.module.css')) return {default: new Proxy({}, {get: (_target, key) => String(key)})};
    if (name === '../documents/PdfDocumentOcrPanel') return {PdfDocumentOcrPanel: marker};
    if (name === './payment-correction-client') return {paymentCorrectionLabels, correctionFields};
    if (name === '../documents/pdf-document-ocr-client') return {pdfDocumentOcrOwnerKey: (item: PdfDocumentOcrOwner) => JSON.stringify(item)};
    if (name === './payment-pdf-supplement-client') return {parsePaymentSupplement, paymentSupplementOwnerKey, paymentSupplementError, supplementPdfOwner,
      createPaymentSupplementGateway: () => ({ocr: (_context: unknown, _owner: unknown, _supplement: unknown, valid: () => boolean, signal: AbortSignal) =>
        new Promise(resolve => queries.push({valid, signal, finish: resolve}))})};
    throw new Error('Unexpected review import: ' + name);
  };
  new Function('require', 'module', 'exports', code)(imports, evaluated, evaluated.exports);
  const render = (props: PaymentSupplementReviewProps) => {
    position = 0; pending = []; const desk = evaluated.exports.PaymentSupplementReviewPanel!(props) as React.ReactElement<PaymentSupplementReviewProps>;
    if (typeof desk.type !== 'function') {for (const item of effects.values()) item.cleanup?.(); effects.clear(); return desk;}
    const tree = (desk.type as (input: PaymentSupplementReviewProps) => React.ReactElement)(desk.props);
    for (const {index, effect, dependencies} of pending) {const previous = effects.get(index);
      if (!previous || !dependencies || !previous.dependencies || dependencies.length !== previous.dependencies.length || dependencies.some((value, offset) => !Object.is(value, previous.dependencies?.[offset]))) {
        previous?.cleanup?.(); effects.set(index, {dependencies, cleanup: effect() || undefined});}}
    return tree;
  };
  const find = (tree: unknown): React.ReactElement<{onRefresh: () => void}> | null => {
    if (!tree || typeof tree !== 'object') return null; const item = tree as React.ReactElement<{children?: unknown; onRefresh: () => void}>;
    if (item.type === marker) return item; const children = item.props?.children;
    for (const child of Array.isArray(children) ? children : [children]) {const found = find(child); if (found) return found;} return null;
  };
  let authorized = true;
  const props = (): PaymentSupplementReviewProps => ({owner: primary, supplement: attachedSupplement, context: 'reviewer', locked: false,
    current: () => authorized, onDenied: () => assert.fail('Stale replies stay hidden')});
  find(render(props()))!.props.onRefresh(); await Promise.resolve(); assert.equal(queries.length, 1);
  for (let index = 0; index < 4; index++) render(props()); assert.equal(queries.length, 1); assert.equal(queries[0].signal.aborted, false);
  authorized = false; assert.equal(find(render(props())), null); assert.equal(queries[0].signal.aborted, true); assert.equal(queries[0].valid(), false);
  queries[0].finish({view: supplementOcr().ocr}); await Promise.resolve(); assert.equal(find(render(props())), null);
  for (const effect of effects.values()) effect.cleanup?.(); assert.equal(queries.length, 1);
});

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
