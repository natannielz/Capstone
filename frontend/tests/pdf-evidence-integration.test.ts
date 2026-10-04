import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import * as React from 'react';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import type {EvidenceAttachmentBoundaryProps} from '../src/modules/documents/EvidenceAttachmentBoundary';
import {EvidenceFormatChoice} from '../src/modules/documents/EvidenceFormatChoice';
import {createPdfEvidenceGateway, pdfEvidenceReady, pdfFileAllowed, pdfReservationKey, pdfReservationKeyOwner, readCheckedPdfOriginal,
  type PdfEvidenceTarget, type PdfEvidenceStatus} from '../src/modules/documents/pdf-evidence-client';
import type {PdfDocumentOcrOwner} from '../src/modules/documents/pdf-document-ocr-client';
import {pdfDocumentOcrOwnerKey} from '../src/modules/documents/pdf-document-ocr-client';

const target: PdfEvidenceTarget = {actorId: '9007199254740993', sourceId: '9007199254740995', sourceRevision: '2', sourceKind: 'Payment'};
const reference = {id: '9007199254740997', version: '1', sha256: 'a'.repeat(64)};
const owner: PdfDocumentOcrOwner = {...target, submissionId: '9007199254740999', evidence: reference};
const requestUuid = '13ef8768-755a-40d4-8e06-204a94f6e9e2';
const reserved = {reservationId: '9007199254741001', revision: '1' as const, expiresAt: '2026-10-05T10:00:00Z'};
const ready: PdfEvidenceStatus = {...reserved, state: 'quarantined', reference, scanStatus: 'clean', decoderStatus: 'decoded', safeFailure: null};
const json = (value: unknown) => new Response(JSON.stringify(value), {headers: {'Content-Type': 'application/json'}});
function transport(reply: (request: Request) => Response | Promise<Response>) {
  const requests: Request[] = []; let tokens = 0;
  const fetcher = (async (input: RequestInfo | URL) => {const request = input instanceof Request ? input : new Request(input);
    requests.push(request); if (new URL(request.url).pathname === '/api/v1/identity/csrf') return json({requestToken: `fresh-token-${++tokens}`}); return reply(request);}) as typeof fetch;
  return {requests, gateway: createPdfEvidenceGateway({fetcher, baseUrl: 'https://unit-toko.test'})};
}

function boundaryHarness() {
  const hooks: unknown[] = [], effects = new Map<number, {dependencies?: readonly unknown[]; cleanup?: () => void}>();
  let position = 0, pending: {index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  const queries: {owner: PdfDocumentOcrOwner; valid: () => boolean; signal: AbortSignal; finish: (value: {kind: 'image' | 'pdf'}) => void}[] = [];
  const mockReact = {...React,
    useState(initial: unknown) {const index = position++; if (!(index in hooks)) hooks[index] = typeof initial === 'function' ? initial() : initial;
      return [hooks[index], (value: unknown) => {hooks[index] = typeof value === 'function' ? value(hooks[index]) : value;}];},
    useRef(initial: unknown) {return hooks[position++] ??= {current: initial};},
    useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {pending.push({index: position++, effect, dependencies});}};
  const runtimeRequire = createRequire(resolve('package.json'));
  const ts = runtimeRequire('typescript') as typeof import('typescript');
  const source = readFileSync(resolve('src/modules/documents/EvidenceAttachmentBoundary.tsx'), 'utf8');
  const code = ts.transpileModule(source, {compilerOptions: {module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
  const evaluated: {exports: {EvidenceAttachmentBoundary?: (props: EvidenceAttachmentBoundaryProps) => React.ReactElement}} = {exports: {}};
  const imports = (name: string): unknown => {
    if (name === 'react') return mockReact; if (name === 'react/jsx-runtime') return runtimeRequire(name);
    if (name.endsWith('pdf-document-ocr-client')) return {pdfDocumentOcrOwnerKey};
    if (name === './pdf-evidence-client') return {createPdfEvidenceGateway: () => ({format: (_context: unknown, selected: PdfDocumentOcrOwner, valid: () => boolean, signal: AbortSignal) =>
      new Promise(resolve => queries.push({owner: selected, valid, signal, finish: resolve}))})};
    if (name === '@/modules/administration/workflow') return {accessMessage: () => 'Kesalahan pemeriksaan'};
    return new Proxy({}, {get: (_object, key) => key === '__esModule' ? true : String(key)});
  };
  new Function('require', 'module', 'exports', code)(imports, evaluated, evaluated.exports);
  return {queries, render(props: EvidenceAttachmentBoundaryProps) {
    position = 0; pending = []; const tree = evaluated.exports.EvidenceAttachmentBoundary!(props);
    for (const {index, effect, dependencies} of pending) {const before = effects.get(index);
      if (!before || !dependencies || !before.dependencies || dependencies.length !== before.dependencies.length || dependencies.some((value, offset) => !Object.is(value, before.dependencies?.[offset]))) {
        before?.cleanup?.(); effects.set(index, {dependencies, cleanup: effect() || undefined});}}
    return tree;
  }, unmount() {for (const item of effects.values()) item.cleanup?.(); effects.clear();}};
}

test('actual attachment boundary does not refetch unchanged evidence on parent callback rerenders and aborts changed ownership', async () => {
  const harness = boundaryHarness(), kinds: ('image' | 'pdf' | null)[] = [];
  let authorized = true;
  const props = (selected = owner): EvidenceAttachmentBoundaryProps => ({context: 'payment-requester', target,
    submitted: {owner: selected, reservationId: reserved.reservationId}, locked: false, fresh: true, now: 0,
    current: () => authorized, journal: {} as EvidenceAttachmentBoundaryProps['journal'], onBusyChange: () => undefined,
    onDenied: () => assert.fail('No failure expected'), onKind: kind => kinds.push(kind), renderImage: () => null});
  harness.render(props()); await Promise.resolve(); assert.equal(harness.queries.length, 1);
  harness.queries[0].finish({kind: 'pdf'}); await Promise.resolve();
  for (let index = 0; index < 4; index++) {harness.render(props()); await Promise.resolve();}
  assert.equal(harness.queries.length, 1); assert.deepEqual(kinds, [null, 'pdf']);
  const updated = {...owner, sourceRevision: '3'}; harness.render(props(updated)); await Promise.resolve();
  assert.equal(harness.queries[0].signal.aborted, true); assert.equal(harness.queries.length, 2);
  authorized = false; harness.render(props(updated)); await Promise.resolve();
  assert.equal(harness.queries[1].signal.aborted, true); assert.equal(harness.queries[1].valid(), false);
  harness.queries[1].finish({kind: 'image'}); await Promise.resolve();
  assert.deepEqual(kinds, [null, 'pdf', null]); assert.equal(harness.queries.length, 2); harness.unmount();
});

test('explicit admission never chooses a PDF route from a MIME guess or filename', () => {
  assert.doesNotThrow(() => pdfFileAllowed(new File(['pdf'], 'original.pdf', {type: 'application/pdf'}), 'pdf'));
  for (const kind of [null, 'image'] as const) assert.throws(() => pdfFileAllowed(new File(['pdf'], 'original.pdf', {type: 'application/pdf'}), kind), {code: 'explicit_pdf_required'});
  for (const file of [{size: 0, type: 'application/pdf'}, {size: 10 * 1024 * 1024 + 1, type: 'application/pdf'}, {size: 3, type: 'image/png'}, {size: 3, type: ''}])
    assert.throws(() => pdfFileAllowed(file, 'pdf'), {code: 'explicit_pdf_required'});
  const html = renderToStaticMarkup(createElement(EvidenceFormatChoice, {value: null, disabled: false, onChange: () => undefined}));
  assert.match(html, /Foto PNG atau JPEG/); assert.match(html, /Dokumen PDF asli/); assert.match(html, /Belum ada jenis bukti/); assert.doesNotMatch(html, /checked=""/);
});

test('PDF reservation keys bind actor/source/type and cannot recover an image or another actor', () => {
  const key = pdfReservationKey(target, requestUuid); assert.equal(pdfReservationKeyOwner(key, target.actorId, 'Payment'), target.sourceId);
  assert.throws(() => pdfReservationKeyOwner(key, '41', 'Payment')); assert.throws(() => pdfReservationKeyOwner(key, target.actorId, 'SupplierInvoice'));
  assert.throws(() => pdfReservationKeyOwner(`p.${target.sourceId}.${requestUuid}`, target.actorId, 'Payment'));
  assert.throws(() => pdfReservationKey(target, '00000000-0000-0000-0000-000000000000'));
});

test('M85 reserve, upload and original-key recovery use exact routes and fresh actor/CSRF/revision headers', async () => {
  const {gateway, requests} = transport(request => request.url.endsWith('/operations/query') ? json({status: 'committed', reservation: reserved}) :
    request.url.endsWith('/content') ? json({state: 'quarantined', reference}) : json(reserved));
  const key = pdfReservationKey(target, requestUuid);
  await gateway.reserve(target, key, 'Unggah PDF asli', () => true);
  await gateway.upload(target, reserved, new File(['pdf'], 'original.pdf', {type: 'application/pdf'}), 'pdf', () => true);
  assert.equal((await gateway.recoverReservation({actorId: target.actorId, sourceId: target.sourceId, sourceKind: target.sourceKind}, key, () => true))!.result.reservationId, reserved.reservationId);
  const posts = requests.filter(request => request.method === 'POST'); assert.equal(posts.length, 3);
  assert.equal(new URL(posts[0].url).pathname, `/api/v1/billing/payment-declarations/${target.sourceId}/pdf-evidence`);
  assert.equal(posts[0].headers.get('Idempotency-Key'), key); assert.equal(posts[0].headers.get('If-Match'), '"2"');
  assert.equal(posts[1].headers.get('Content-Type'), 'application/octet-stream'); assert.equal(posts[1].headers.get('Idempotency-Key'), `upload.${reserved.reservationId}`);
  assert.equal(posts[1].headers.get('If-Match'), '"1"');
  assert.deepEqual(posts.map(request => request.headers.get('X-CSRF-Token')), ['fresh-token-1', 'fresh-token-2', 'fresh-token-3']);
  for (const request of posts) {assert.equal(request.headers.get('X-Expected-Actor-Id'), target.actorId); assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');}
  assert.deepEqual(await posts[2].json(), {purpose: 'operation-recovery', key});
});

test('requester/reviewer/supplier reads use distinct agreed purpose strings and persisted format binding', async () => {
  for (const [context, selected, prefix, formatPurpose, ocrPurpose] of [
    ['payment-requester', owner, `/api/v1/billing/payment-declarations/${owner.sourceId}`, 'payment-evidence-format-view', 'payment-pdf-ocr-view'],
    ['payment-reviewer', owner, `/api/v1/billing/payment-reviews/${owner.sourceId}`, 'payment-review-evidence-format-view', 'payment-review-pdf-ocr-view'],
    ['supplier-document', {...owner, sourceKind: 'SupplierInvoice', sourceRevision: '1'}, `/api/v1/procurement/supplier-documents/drafts/${owner.sourceId}`, 'supplier-document-evidence-format-view', 'supplier-document-pdf-ocr-view'],
  ] as const) {
    const {gateway, requests} = transport(request => request.url.endsWith('/evidence-format/query') ? json({sourceId: selected.sourceId, sourceRevision: selected.sourceRevision, evidence: selected.evidence, kind: 'pdf'}) :
      json({sourceId: selected.sourceId, sourceRevision: selected.sourceRevision, submissionId: selected.submissionId, sourceKind: selected.sourceKind, evidence: selected.evidence, state: 'queued', result: null, failureCode: null}));
    assert.equal((await gateway.format(context, selected, () => true)).kind, 'pdf'); assert.equal((await gateway.ocr(context, selected, () => true)).state, 'queued');
    const posts = requests.filter(request => request.method === 'POST');
    assert.equal(new URL(posts[0].url).pathname, prefix + '/evidence-format/query'); assert.equal(new URL(posts[1].url).pathname, prefix + '/pdf-ocr/query');
    assert.deepEqual(await posts[0].json(), {purpose: formatPurpose, reference: selected.evidence}); assert.deepEqual(await posts[1].json(), {purpose: ocrPurpose, reference: selected.evidence});
    assert.equal(posts[0].headers.get('If-Match'), `"${selected.sourceRevision}"`);
  }
  const mismatched = transport(() => json({sourceId: '41', sourceRevision: owner.sourceRevision, evidence: reference, kind: 'pdf'}));
  await assert.rejects(mismatched.gateway.format('payment-requester', owner, () => true), {code: 'pdf_evidence_response_invalid'});
});

test('stale actor/source cancellation blocks dispatch or replies and never retries writes', async () => {
  let current = false; const {gateway, requests} = transport(() => json(reserved));
  await assert.rejects(gateway.reserve(target, pdfReservationKey(target, requestUuid), 'Unggah PDF asli', () => current), {code: 'pdf_view_stale'}); assert.equal(requests.length, 0);
  current = true; let finish: ((value: Response) => void) | undefined;
  const late = transport(() => new Promise(resolve => {finish = resolve;}));
  const pending = late.gateway.reserve(target, pdfReservationKey(target, requestUuid), 'Unggah PDF asli', () => current);
  while (!finish) await Promise.resolve(); current = false; finish(json(reserved));
  await assert.rejects(pending, {code: 'commit_outcome_unknown'}); assert.equal(late.requests.filter(request => request.method === 'POST').length, 1);
});

test('only security-ready PDF status can reach submission review', () => {
  assert.equal(pdfEvidenceReady(ready), true);
  for (const value of [{...ready, decoderStatus: 'unavailable'}, {...ready, decoderStatus: 'invalid', safeFailure: 'invalid_media'},
    {...ready, scanStatus: 'infected', decoderStatus: 'unavailable', safeFailure: 'infected_media'}] as PdfEvidenceStatus[]) assert.equal(pdfEvidenceReady(value), false);
});

const bytes = new TextEncoder().encode('%PDF-1.4\nsynthetic-private-test\n%%EOF\n');
async function originalReference() {return {...reference, sha256: Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), byte => byte.toString(16).padStart(2, '0')).join('')};}
const pdfHeaders = () => new Headers({'Content-Type': 'application/pdf', 'Content-Length': String(bytes.length), 'Cache-Control': 'private, no-store, max-age=0',
  'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': "default-src 'none'; sandbox", 'Content-Disposition': 'attachment; filename="bukti.pdf"'});

test('private original PDF is bounded and hash checked before Blob creation, including reviewer path', async () => {
  const expected = await originalReference(); const blob = await readCheckedPdfOriginal(new Response(bytes, {headers: pdfHeaders()}), expected, () => true);
  assert.equal(blob.type, 'application/pdf'); assert.deepEqual(new Uint8Array(await blob.arrayBuffer()), bytes);
  const {gateway, requests} = transport(() => new Response(bytes, {headers: pdfHeaders()}));
  await gateway.original('payment-reviewer', target, reserved.reservationId, expected, () => true);
  const post = requests.find(request => request.method === 'POST')!;
  assert.equal(new URL(post.url).pathname, `/api/v1/billing/payment-reviews/${target.sourceId}/pdf-evidence/${reserved.reservationId}/original/query`);
  assert.deepEqual(await post.json(), {purpose: 'payment-evidence-view', reference: expected});
});

test('private PDF rejects changed hash, unsafe headers, misleading length and stale streams with cleanup', async () => {
  const expected = await originalReference();
  await assert.rejects(readCheckedPdfOriginal(new Response(bytes, {headers: pdfHeaders()}), reference, () => true), {code: 'pdf_evidence_response_invalid'});
  for (const [name, value] of [['Content-Type', 'image/png'], ['Cache-Control', 'public'], ['X-Content-Type-Options', ''], ['Content-Security-Policy', "default-src 'none'"],
    ['Content-Length', '10485761'], ['Content-Length', String(bytes.length - 1)], ['Content-Disposition', 'inline']] as const) {
    const headers = pdfHeaders(); headers.set(name, value); await assert.rejects(readCheckedPdfOriginal(new Response(bytes, {headers}), expected, () => true), {code: 'pdf_evidence_response_invalid'});
  }
  let cancelled = false; const body = new ReadableStream<Uint8Array>({cancel() {cancelled = true;}});
  await assert.rejects(readCheckedPdfOriginal(new Response(body, {headers: pdfHeaders()}), expected, () => false), {code: 'pdf_view_stale'}); assert.equal(cancelled, true);
});
