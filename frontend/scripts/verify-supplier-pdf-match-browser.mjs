// Dedicated supplier PDF matching proof: actual original/OCR, reviewed page reference, manual allocation and original-key recovery.
// Named synthetic issued sessions; actual production Next, pinned HTTPS, .NET, Mongo and private native PDF processing.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import {createRequire} from 'node:module';
import {mkdirSync, existsSync, writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createHash} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';
import next from 'next';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE, 'full');
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/'); assert.match(config.spki, /^[A-Za-z0-9+/]{43}=$/);
assert.equal(config.mode, 'supplier'); assert.equal(config.expectedSourceKind, 'SupplierInvoice'); assert.equal(config.expectedAmountField, 'total_amount');
assert.equal(config.sourceRevision, '1'); assert.match(config.sourceId, /^[1-9][0-9]*$/);
// Parent stdin uses JsonSerializer's default PascalCase for these actual DTOs;
// HTTP uses the API's Web camelCase policy. Map only the exact declared fields,
// preserving every value (including lossless ID/money strings and nulls).
function fixtureDto(value, fields) {
  assert.ok(value !== null && typeof value === 'object' && !Array.isArray(value));
  assert.deepEqual(Object.keys(value).sort(), [...fields].sort());
  return Object.fromEntries(fields.map(field => [field[0].toLowerCase() + field.slice(1), value[field]]));
}
const documentInput = fixtureDto(config.documentInput, ['Location','AnchorOrderId','AnchorOrderVersion','AnchorOrderApprovalId','Kind',
  'DocumentReference','DocumentDate','Currency','DocumentTotal','Lines','Discrepancy']);
documentInput.location = fixtureDto(documentInput.location, ['OrganizationId','ShopId','WarehouseId','ChannelId']);
assert.ok(Array.isArray(documentInput.lines) && documentInput.lines.length > 0 && documentInput.lines.length <= 100);
documentInput.lines = documentInput.lines.map(line => fixtureDto(line, ['SourceLineReference','Description','SourceSystem','SourceAccount','SourceCode',
  'Packages','NetPricePerPackage','TaxAmount','ChargeAmount','RelatedInvoiceReference','StatementAmount']));
assert.deepEqual(config.expectedAmounts, ['34000.00','34000.00']); assert.equal(documentInput.kind,'invoice');
assert.equal(documentInput.documentTotal,'34000.00'); assert.equal(documentInput.documentReference,config.documentReference);
assert.match(config.inspectionId,/^[1-9][0-9]*$/); assert.ok(typeof config.packageLabel === 'string' && config.packageLabel.length > 0);
const require = createRequire(config.playwrightPackage), metadata = require(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1'); const {chromium} = require('playwright');
const directory = resolve(config.runDirectory); mkdirSync(directory, {recursive: true});
const original = Buffer.from(config.pdfBase64, 'base64'), hash = bytes => createHash('sha256').update(bytes).digest('hex');
assert.equal(hash(original), config.pdfSha256); assert.ok(original.length > 0 && original.length <= 10 * 1024 * 1024);
const app = next({dev: false, dir: process.cwd(), quiet: true}), sockets = new Set(), pages = [], errors = [], writes = [], reads = [];
let frontend, edge, tunnel, browser, reservationId, evidence, view, matchPath=null, activeDrop=null, dropped=0;
const matchingWrites=[], matchingRecoveries=[], privateKeys=new Set(), traceEvents=[], purpose='supplier-document-review';
const keyHash=key => {if(typeof key !== 'string' || !key)return null;privateKeys.add(key);return hash(Buffer.from(key,'utf8'));};
function trace(event,details={}){if(traceEvents.length<128)traceEvents.push({sequence:traceEvents.length+1,event,...details});}
function transportProof(outcome){writeFileSync(resolve(directory,'matching-transport-trace.json'),JSON.stringify({outcome,dropped,events:traceEvents},null,2));}
async function waitDropped(){
  const deadline=performance.now()+30000;
  while(!activeDrop?.dropped){
    if(activeDrop?.failure)throw new Error('Actual matching drop failed: '+activeDrop.failure);
    if(activeDrop?.ended && activeDrop.status !== 200)throw new Error('Matching refused before drop: status '+activeDrop.status+', code '+(activeDrop.code ?? 'UNCLASSIFIED'));
    assert.ok(performance.now()<deadline,'Actual successful matching response/drop did not finish within 30 seconds');await delay(25);
  }
  assert.equal(activeDrop.status,200);assert.equal(activeDrop.ended,true);assert.equal(activeDrop.complete,true);assert.equal(dropped,1);
  trace('successful_drop_verified_before_recovery',{keyHash:keyHash(activeDrop.key)});
}
const sourcePrefix = `/api/v1/procurement/supplier-documents/drafts/${config.sourceId}`;
const submissionPath = sourcePrefix + '/submissions';
const screenPath = '/procurement/supplier-documents';
const safe = value => {let text = String(value); for (const secret of [config.actorToken, config.reviewerToken, config.outsiderToken, config.pfx, config.password, config.pdfBase64,...privateKeys])
  if (typeof secret === 'string' && secret) text = text.replaceAll(secret, '[REDACTED]'); return text.replace(/(__Host-ut_session|cookie|authorization)\s*[:=].*/gi, '$1=[REDACTED]').slice(0, 1400);};
function track(server) {server.on('connection', socket => {sockets.add(socket); socket.on('close', () => sockets.delete(socket));}); return server;}
async function listen(server) {await new Promise((yes, no) => {server.once('error', no); server.listen(0, '127.0.0.1', yes);}); return server.address().port;}
function forward(req,res,port,isApi){
  const path=new URL(req.url,origin).pathname,headers={...req.headers,host:'127.0.0.1:'+port};
  for(const name of Object.keys(headers))if(name.startsWith('x-forwarded-')||name.startsWith('x-original-')||['forwarded','connection'].includes(name))delete headers[name];
  if(isApi){headers['x-forwarded-for']=req.socket.remoteAddress;headers['x-forwarded-host']=origin.host;headers['x-forwarded-proto']='https';}
  const observed={path,actor:req.headers['x-expected-actor-id'],key:req.headers['idempotency-key'],revision:req.headers['if-match'],
    csrf:typeof req.headers['x-csrf-token']==='string',origin:req.headers.origin};
  const matching=req.method==='POST'&&matchPath!==null&&path===matchPath;
  const recovery=req.method==='POST'&&path==='/api/v1/procurement/supplier-documents/operations/query';
  if(req.method==='POST'&&(path===sourcePrefix+'/pdf-evidence'||path.includes('/pdf-evidence/')&&path.endsWith('/content')||path===submissionPath))writes.push(observed);
  if(req.method==='POST'&&(path.endsWith('/pdf-ocr/query')||path.endsWith('/evidence-format/query')||path.endsWith('/original/query')))reads.push(observed);
  const record={...observed,body:null,status:null,complete:false};
  if(matching){matchingWrites.push(record);keyHash(record.key);}if(recovery)matchingRecoveries.push(record);
  const intentional=matching&&activeDrop?.armed===true?activeDrop:null;
  if(intentional){intentional.armed=false;intentional.key=observed.key;trace('matching_dispatched',{keyHash:keyHash(observed.key),actorMatches:observed.actor===config.reviewerId,
    revision:observed.revision,csrfPresent:observed.csrf,originMatches:observed.origin===origin.origin});}
  if(matching||recovery){
    const chunks=[];let count=0;
    req.on('data',chunk=>{count+=chunk.length;if(count<=1024*1024)chunks.push(chunk);else chunks.length=0;});
    req.once('end',()=>{
      if(count>1024*1024)return;
      try{record.body=JSON.parse(Buffer.concat(chunks).toString('utf8'));if(recovery)trace('matching_recovery_dispatched',{
        keyHash:keyHash(record.body.key),sameOriginalKey:record.body.key===activeDrop?.key,actorMatches:record.actor===config.reviewerId,hasIfMatch:record.revision!==undefined});}
      catch{/* No request body is written to logs. Strict assertions below refuse an incomplete body. */}
    });
  }
  const upstream=http.request({hostname:'127.0.0.1',port,path:req.url,method:req.method,headers},response=>{
    record.status=response.statusCode??null;
    response.once('end',()=>{record.complete=response.complete;if(intentional){intentional.ended=true;intentional.complete=response.complete;}});
    if(intentional){intentional.status=record.status;trace('matching_upstream_status',{status:record.status});}
    response.once('aborted',()=>{if(intentional)intentional.failure='UPSTREAM_ABORTED';});
    response.once('error',()=>{if(intentional)intentional.failure='UPSTREAM_RESPONSE_ERROR';});
    if(intentional&&record.status!==200){
      const chunks=[];let count=0;response.on('data',chunk=>{count+=chunk.length;if(count<=4096)chunks.push(chunk);else chunks.length=0;});
      response.once('end',()=>{if(count<=4096){try{const body=JSON.parse(Buffer.concat(chunks).toString('utf8'));
        if(/^[a-z_]{1,100}$/.test(body?.code??''))intentional.code=body.code;}catch{/* Safe code only. */}}
        trace('matching_refused',{status:record.status,code:intentional.code??'UNCLASSIFIED'});});
    }
    if(intentional&&record.status===200){
      response.resume();response.once('end',()=>{
        if(!response.complete){intentional.failure='UPSTREAM_INCOMPLETE';return;}
        trace('matching_committed_reply_ended',{status:200,complete:true});
        res.writeHead(200,{'content-type':'application/json','content-length':'64','cache-control':'no-store'});res.flushHeaders();res.write('{');
        setTimeout(()=>{if(res.destroyed){intentional.failure='CLIENT_CLOSED_BEFORE_DROP';return;}
          res.destroy();intentional.dropped=true;dropped++;trace('matching_successful_reply_dropped',{keyHash:keyHash(observed.key)});},25);
      });return;
    }
    res.writeHead(response.statusCode??502,response.headers);response.pipe(res);
  });
  upstream.on('error',()=>{if(intentional)intentional.failure='UPSTREAM_REQUEST_ERROR';if(!res.headersSent)res.writeHead(502);res.end();});
  req.on('aborted',()=>{if(intentional)intentional.failure='CLIENT_REQUEST_ABORTED';upstream.destroy();});req.pipe(upstream);
}
async function user(who) {
  const context = await browser.newContext({viewport: {width: 1280, height: 960}, locale: 'id-ID', timezoneId: 'Asia/Jakarta'});
  await context.addCookies([{name: '__Host-ut_session', value: config[who + 'Token'], domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); page.setDefaultTimeout(30000); pages.push({who, page}); page.on('pageerror', error => errors.push(safe(error.message)));
  page.on('response', async response => {if (!new URL(response.url()).pathname.startsWith('/api/v1/') || response.status() < 400) return;
    const body = await response.json().catch(() => null); console.log('PDF_API_BOUNDARY ' + JSON.stringify({who, path: new URL(response.url()).pathname, status: response.status(), code: /^[a-z_]{1,100}$/.test(body?.code ?? '') ? body.code : undefined}));});
  return {context, page};
}
async function ready(path) {const deadline = performance.now() + 120000; while (!existsSync(path)) {assert.ok(performance.now() < deadline, 'Native processing readiness did not arrive within the bounded deadline'); await delay(250);}}
async function open(page, submitted = false) {
  await page.goto(new URL(screenPath, origin).href);
  const selector = page.getByLabel('Organisasi / toko / gudang / kanal', {exact: true}); await selector.waitFor();
  await page.waitForFunction(() => {const el = document.querySelector('select[aria-labelledby="supplier-document-location"]'); return el && !el.disabled && el.options.length > 1;});
  const options = await selector.locator('option').evaluateAll(items => items.map(item => item.value).filter(Boolean));
  if (!config.locationKey) assert.equal(options.length, 1, 'Supplier browser fixture must choose one actual configured location');
  await selector.selectOption(config.locationKey ?? options[0]);
  await page.getByRole('button', {name: new RegExp('^' + config.documentReference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))}).click();
  await page.getByRole('heading', {name: submitted ? 'Dokumen tersimpan untuk pencocokan' : 'Draf menunggu bukti asli', exact: true}).waitFor();
}
async function download(page, button, route) {
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === route && response.request().method() === 'POST'), downloaded = page.waitForEvent('download');
  await button.click(); const response = await reply; assert.equal(response.status(), 200);
  const headers = response.headers(); assert.equal(headers['content-type'], 'application/pdf'); assert.match(headers['cache-control'], /private/); assert.match(headers['cache-control'], /no-store/);
  assert.equal(headers['x-content-type-options'], 'nosniff'); assert.equal(headers['content-security-policy'], "default-src 'none'; sandbox"); assert.match(headers['content-disposition'], /^attachment;/);
  assert.equal(Number(headers['content-length']), original.length); assert.equal(hash(await response.body()), config.pdfSha256);
  const file = await downloaded; assert.equal(await file.failure(), null); const stream = await file.createReadStream(), chunks = []; let length = 0;
  for await (const chunk of stream) {length += chunk.length; assert.ok(length <= 10 * 1024 * 1024); chunks.push(chunk);}
  assert.equal(length, original.length); assert.equal(hash(Buffer.concat(chunks)), config.pdfSha256);
  assert.equal(await page.locator('iframe[src^="blob:"],img[src^="blob:"]').count(), 0, 'Original PDF download must not be represented as a page-image preview');
}
async function post(page, actorId, path, body, revision) {
  return page.evaluate(async ({actorId, path, body, revision}) => {const csrf = await fetch('/api/v1/identity/csrf', {credentials: 'same-origin', cache: 'no-store', redirect: 'error'});
    if (!csrf.ok) throw new Error('Actual CSRF endpoint unavailable'); const token = await csrf.json();
    const response = await fetch(path, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': token.requestToken, 'X-Expected-Actor-Id': actorId,
      ...(revision === undefined ? {} : {'If-Match': `"${revision}"`})}, body: JSON.stringify(body)});
    const value = await response.json().catch(() => null); return {status: response.status, value};
  }, {actorId, path, body, revision});
}
async function result(page, prefix, role) {
  await page.setViewportSize({width: 1280, height: 960});
  const panel = page.getByRole('region', {name: 'Pemeriksaan PDF yang diajukan', exact: true}); await panel.waitFor();
  const reply = page.waitForResponse(response => new URL(response.url()).pathname === prefix + '/pdf-ocr/query');
  await panel.getByRole('button', {name: 'Muat hasil OCR PDF', exact: true}).click(); const response = await reply;
  assert.equal(response.status(), 200); const saved = await response.json(); assert.equal(saved.sourceId, config.sourceId); assert.equal(saved.sourceKind, config.expectedSourceKind);
  assert.equal(saved.state, 'succeeded'); assert.equal(saved.failureCode, null); assert.equal(saved.evidence.sha256, config.pdfSha256); assert.equal(saved.result.requiresHumanReview, true);
  assert.equal(saved.result.pages.length, 2); assert.deepEqual(saved.result.pages.map(page => page.raster.pageIndex), [0, 1]);
  const amounts = saved.result.fields.find(field => field.field === config.expectedAmountField); assert.ok(amounts);
  assert.deepEqual(amounts.observations.map(item => item.candidate.value), config.expectedAmounts); assert.ok(amounts.reviewReasons.includes('repeated_across_pages'));assert.equal(amounts.reviewReasons.includes('contradictory_pages'),false);
  assert.equal(await panel.locator('[data-pdf-ocr-page]').count(), 2); await panel.getByRole('heading', {name: 'Halaman 1 dari 2', exact: true}).waitFor();
  await panel.getByRole('heading', {name: 'Halaman 2 dari 2', exact: true}).waitFor();
  assert.match(await panel.innerText(), /Nilai berulang pada beberapa halaman/); assert.match(await panel.innerText(), /bukan transaksi tambahan/);
  const candidates = saved.result.pages.flatMap(page => page.fields);
  if (candidates.some(candidate => candidate.value === null)) assert.match(await panel.innerText(), /Belum diketahui/);
  if (candidates.some(candidate => candidate.flags.includes('low_confidence'))) assert.match(await panel.innerText(), /Keyakinan rendah; periksa ulang/);
  assert.equal(await panel.getByRole('button', {name: /^Pilih rujukan halaman/}).count(), 0, 'Original download is required before selecting any OCR observation');
  await download(page, panel.getByRole('button', {name: 'Buka PDF asli untuk pemeriksaan', exact: true}), `${prefix}/pdf-evidence/${reservationId}/original/query`);
  const second = panel.locator('[data-pdf-ocr-page="1"]').locator('article').filter({has: page.getByRole('heading', {name: 'Total tagihan', exact: true})});
  await second.getByRole('checkbox').check(); await second.getByRole('button', {name: /^Pilih rujukan halaman 2:/}).click();
  await panel.getByRole('heading', {name: 'Rujukan observasi yang dipilih', exact: true}).waitFor();
  assert.match(await panel.getByRole('region', {name: 'Rujukan observasi PDF yang dipilih', exact: true}).innerText(), new RegExp(`Halaman 2.*${config.expectedAmountField}`));
  await second.locator('summary').click(); assert.match(await second.innerText(), /Empat sudut pada kanvas PDF/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({path: resolve(directory, `${config.mode}-${role}-desktop.png`), fullPage: true});
  await page.setViewportSize({width: 390, height: 844}); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({path: resolve(directory, `${config.mode}-${role}-mobile.png`), fullPage: true}); return saved;
}
try {
  await app.prepare(); const handle = app.getRequestHandler(); frontend = track(http.createServer((req, res) => {void handle(req, res).catch(() => {res.statusCode = 500; res.end();});})); const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {if (req.headers.host !== origin.host) {res.writeHead(403); res.end(); return;}
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);})); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => {res.writeHead(403); res.end();})); tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') {socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return;}
    const upstream = net.connect(tlsPort, '127.0.0.1', () => {socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket);});
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'msedge'; assert.ok(['chromium', 'msedge'].includes(channel));
  browser = await chromium.launch({...(channel === 'msedge' ? {channel} : {}), headless: true, args: ['--ignore-certificate-errors-spki-list=' + config.spki], proxy: {server: 'http://127.0.0.1:' + proxyPort}});
  const actor = await user('actor'), page = actor.page; await open(page);
  assert.equal(await page.getByRole('region', {name: 'Bukti PDF asli', exact: true}).count(), 0); await page.getByRole('radio', {name: /Dokumen PDF asli/}).check();
  const admission = page.getByRole('region', {name: 'Bukti PDF asli', exact: true}); await admission.waitFor();
  assert.equal(await admission.getByRole('button', {name: 'Unggah PDF asli', exact: true}).isDisabled(), true);
  await admission.getByRole('button', {name: 'Muat status PDF', exact: true}).click(); await admission.getByText('Belum ada PDF pada sumber ini.', {exact: true}).waitFor();
  await admission.getByLabel('PDF asli', {exact: true}).setInputFiles({name: config.pdfFilename, mimeType: 'application/pdf', buffer: original});
  const reserveReply = page.waitForResponse(response => new URL(response.url()).pathname === sourcePrefix + '/pdf-evidence' && response.request().method() === 'POST');
  const uploadReply = page.waitForResponse(response => new URL(response.url()).pathname.startsWith(sourcePrefix + '/pdf-evidence/') && response.url().endsWith('/content'), {timeout: 120000});
  await admission.getByRole('button', {name: 'Unggah PDF asli', exact: true}).click(); const reserved = await reserveReply; assert.equal(reserved.status(), 200); reservationId = (await reserved.json()).reservationId;
  const uploaded = await uploadReply; assert.equal(uploaded.status(), 200); const receipt = await uploaded.json(); assert.equal(receipt.state, 'quarantined'); assert.equal(receipt.reference.sha256, config.pdfSha256); evidence = receipt.reference;
  console.log('PDF_UPLOADED'); await ready(config.validationReadyPath); await admission.getByRole('button', {name: 'Muat status PDF', exact: true}).click();
  await admission.getByText('Pemeriksaan keamanan PDF selesai; periksa seluruh dokumen asli.', {exact: true}).waitFor();
  const chosen = admission.getByRole('button', {name: 'Tinjau pengajuan dengan PDF ' + reservationId, exact: true}); assert.equal(await chosen.isDisabled(), true);
  await download(page, admission.getByRole('button', {name: `Unduh PDF asli ${reservationId} untuk pemeriksaan`, exact: true}), sourcePrefix + '/pdf-evidence/' + reservationId + '/original/query');
  await admission.getByRole('checkbox', {name: /Saya telah membaca seluruh halaman PDF asli/}).check();
  await page.getByLabel('Alasan pengajuan dokumen', {exact: true}).fill('Seluruh halaman PDF asli sintetis telah diperiksa manusia');
  await chosen.click(); const review = page.getByRole('region', {name: 'Tinjauan akhir dokumen', exact: true}); await review.waitFor();
  await review.getByRole('checkbox').check();
  const submittedReply = page.waitForResponse(response => new URL(response.url()).pathname === submissionPath && response.request().method() === 'POST');
  await review.getByRole('button', {name: 'Ajukan dokumen dengan bukti asli', exact: true}).click(); assert.equal((await submittedReply).status(), 200);
  console.log('PDF_SUBMITTED'); await ready(config.extractionReadyPath);
  const mounted = page.getByRole('region', {name: 'Pemeriksaan PDF yang diajukan', exact: true}); await mounted.waitFor(); view = await result(page, sourcePrefix, 'requester');
  const journal = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length); assert.equal(journal, 0);
  await open(page, true); const freshView = await result(page, sourcePrefix, 'requester-reloaded'); assert.equal(freshView.result.id, view.result.id); assert.equal(freshView.result.resultHash, view.result.resultHash);
  const ocrPurpose = 'supplier-document-pdf-ocr-view';
  const stale = await post(page, config.actorId, sourcePrefix + '/pdf-ocr/query', {purpose:ocrPurpose, reference:evidence}, String(BigInt(view.sourceRevision) + BigInt(1))); assert.equal(stale.status, 403);
  const malformed = await post(page, config.actorId, sourcePrefix + '/pdf-ocr/query', {purpose:ocrPurpose, reference:evidence, userId:config.actorId}, view.sourceRevision); assert.equal(malformed.status, 400);
  const outsider = await user('outsider'); await outsider.page.goto(new URL('/identity/account', origin).href);
  const foreign = await post(outsider.page, config.outsiderId, sourcePrefix + '/pdf-ocr/query', {purpose:ocrPurpose, reference:evidence}, view.sourceRevision); assert.equal(foreign.status, 403);
  const foreignOriginal = await post(outsider.page, config.outsiderId, sourcePrefix + '/pdf-evidence/' + reservationId + '/original/query', {purpose: 'supplier-document-evidence-view', reference: evidence}, view.sourceRevision); assert.equal(foreignOriginal.status, 403);
  const reviewer=await user('reviewer'),matchPage=reviewer.page;await open(matchPage,true);
  const shown=await result(matchPage,sourcePrefix,'matcher');assert.equal(shown.result.resultHash,view.result.resultHash);
  const selection=matchPage.getByRole('region',{name:'Rujukan PDF untuk pencocokan',exact:true});await selection.waitFor();
  const useReference=selection.getByRole('button',{name:'Gunakan rujukan PDF untuk pencocokan',exact:true});assert.equal(await useReference.isDisabled(),true);
  await matchPage.getByRole('button',{name:'Tinjau pencocokan dokumen',exact:true}).click();
  const human=matchPage.getByRole('form',{name:'Pemeriksaan manusia dan alokasi',exact:true});await human.waitFor();
  const manualFields=['Kemasan dialokasikan 1','Nilai neto dialokasikan (IDR) 1','Pajak dialokasikan (IDR) 1','Biaya dialokasikan (IDR) 1'];
  const before=await Promise.all(manualFields.map(label=>human.getByLabel(label,{exact:true}).inputValue()));assert.deepEqual(before,['','','','']);
  assert.equal(await human.getByLabel('Baris dokumen 1',{exact:true}).inputValue(),'');assert.equal(await human.getByLabel('Hasil tinjauan 1',{exact:true}).inputValue(),'');
  await useReference.click();assert.deepEqual(await Promise.all(manualFields.map(label=>human.getByLabel(label,{exact:true}).inputValue())),before);
  assert.equal(await human.getByLabel('Baris dokumen 1',{exact:true}).inputValue(),'');assert.equal(await human.getByLabel('Hasil tinjauan 1',{exact:true}).inputValue(),'');
  assert.match(await selection.innerText(),/34000\.00/);assert.match(await selection.innerText(),/Nilai ini tidak disalin/);
  const read=await post(matchPage,config.reviewerId,sourcePrefix+'/query',{purpose});assert.equal(read.status,200);
  const documentId=read.value.document.id;assert.equal(read.value.document.evidence.sha256,config.pdfSha256);
  const reference={field:'total_amount',candidate:{resultId:shown.result.id,resultHash:shown.result.resultHash,evidence,pageIndex:1,field:'total_amount'},originalReviewed:true};
  const input={documentId,documentRevision:'1',reviewedInput:documentInput,ocrResultId:null,pdfObservations:[reference],
    lines:[{documentLineNumber:1,packages:2,inspectionId:config.inspectionId,inspectionLineNumber:1,invoiceMatchId:null,invoiceMatchLineNumber:null,
      netAmount:'20000.00',taxAmount:'2000.00',chargeAmount:'500.00',statementAmount:null,discrepancy:null}]};
  const previewPath='/api/v1/procurement/supplier-documents/documents/'+documentId+'/match-preview/query';
  for(const observations of [null,[],[reference,reference]])assert.equal((await post(matchPage,config.reviewerId,previewPath,{purpose,input:{...input,pdfObservations:observations}},'1')).status,400);
  assert.equal((await post(matchPage,config.reviewerId,previewPath,{purpose,input:{...input,pdfObservationsPresent:true}},'1')).status,400);
  const wrongPage=await post(matchPage,config.reviewerId,previewPath,{purpose,input:{...input,pdfObservations:[{...reference,candidate:{...reference.candidate,pageIndex:2}}]}},'1');
  assert.ok([400,409].includes(wrongPage.status),'An impossible page reference must be rejected');
  assert.equal((await post(outsider.page,config.outsiderId,previewPath,{purpose,input},'1')).status,403);
  await human.getByLabel('Baris dokumen 1',{exact:true}).selectOption('1');await human.getByLabel('Hasil tinjauan 1',{exact:true}).selectOption('source');
  await human.getByLabel('Hasil pemeriksaan dan kemasan 1',{exact:true}).selectOption(config.inspectionId+':1');
  for(const [label,value]of manualFields.map((label,index)=>[label,['2','20000.00','2000.00','500.00'][index]])){
    const field=human.getByLabel(label,{exact:true});assert.equal(await field.inputValue(),'');await field.fill(value);
  }
  await human.getByLabel('Alasan pencocokan',{exact:true}).fill('Dua kemasan dan alokasi manual dibandingkan dengan PDF asli halaman kedua; bukan persetujuan stok atau pembayaran');
  const previewReply=matchPage.waitForResponse(response=>new URL(response.url()).pathname===previewPath);
  await human.getByRole('button',{name:'Periksa alokasi pencocokan',exact:true}).click();const previewResponse=await previewReply;assert.equal(previewResponse.status(),200);
  const prepared=await previewResponse.json();assert.deepEqual(prepared.input,input);
  const matchReview=matchPage.getByRole('region',{name:'Tinjauan akhir dokumen',exact:true});await matchReview.getByRole('heading',{name:'Pastikan alokasi pencocokan',exact:true}).waitFor();
  assert.equal(await matchReview.getByRole('textbox').count(),0);assert.match(await matchReview.innerText(),/20000\.00/);assert.ok((await matchReview.innerText()).includes(config.packageLabel));
  assert.match(await matchReview.getByRole('region',{name:'Rujukan PDF tersimpan untuk pencocokan',exact:true}).innerText(),/Total dokumen · halaman 2/);
  assert.equal(await selection.getByRole('button',{name:'Hapus rujukan PDF: Total dokumen',exact:true}).isDisabled(),true);await matchReview.getByRole('checkbox').check();
  for(const [width,height,label]of [[1280,960,'desktop'],[390,844,'mobile']]){
    await matchPage.setViewportSize({width,height});assert.equal(await matchPage.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    await matchPage.screenshot({path:resolve(directory,'supplier-pdf-match-review-'+label+'.png'),fullPage:true});
  }
  matchPath='/api/v1/procurement/supplier-documents/documents/'+documentId+'/matches';
  activeDrop={armed:true,key:null,status:null,ended:false,complete:false,dropped:false,code:null,failure:null};
  await matchReview.getByRole('button',{name:'Simpan pencocokan dokumen',exact:true}).click();await waitDropped();
  const pendingEntries=await matchPage.evaluate(()=>Object.keys(localStorage).filter(key=>key.startsWith('unit-toko:pending:v1:')).map(key=>JSON.parse(localStorage[key])));
  assert.equal(pendingEntries.length,1);assert.deepEqual(Object.keys(pendingEntries[0]).sort(),['actorId','createdAt','key','operation']);
  const pending=pendingEntries[0];assert.equal(pending.actorId,config.reviewerId);assert.equal(pending.key,activeDrop.key);assert.equal(pending.operation,'procurement.supplierdocument.match');
  assert.equal(JSON.stringify(pending).includes(config.pdfSha256),false);assert.equal(JSON.stringify(pending).includes('34000.00'),false);
  assert.equal(matchingWrites.length,1);assert.equal(matchingWrites[0].status,200);assert.equal(matchingWrites[0].complete,true);assert.deepEqual(matchingWrites[0].body.input,input);
  const recoveryPath='/api/v1/procurement/supplier-documents/operations/query';
  // Lookup is actor-bound: another authorized actor must receive no receipt,
  // even with the committed actor's exact key. Private/source reads remain 403 above.
  const foreignRecovery=await post(outsider.page,config.outsiderId,recoveryPath,{purpose:'operation-recovery',operation:pending.operation,key:pending.key});
  assert.equal(foreignRecovery.status,200);assert.deepEqual(foreignRecovery.value,{status:'not_confirmed',result:null});
  trace('foreign_recovery_withheld',{sameOriginalKey:true,status:200,receiptReturned:false});
  await matchPage.reload();const recover=matchPage.getByRole('button',{name:'Periksa hasil',exact:true});await recover.waitFor();
  assert.equal(await matchPage.getByRole('button',{name:/Tutup.*permintaan/}).count(),0);
  const recoveredReply=matchPage.waitForResponse(response=>new URL(response.url()).pathname===recoveryPath&&response.request().postDataJSON()?.key===pending.key);
  await recover.click();const recovered=await recoveredReply;assert.equal(recovered.status(),200);assert.equal((await recovered.json()).status,'committed');await recover.waitFor({state:'hidden'});
  assert.equal(await matchPage.evaluate(()=>Object.keys(localStorage).filter(key=>key.startsWith('unit-toko:pending:v1:')).length),0);
  await open(matchPage,true);const final=await post(matchPage,config.reviewerId,sourcePrefix+'/query',{purpose});assert.equal(final.status,200);
  assert.equal(final.value.matches.length,1);assert.deepEqual(final.value.matches[0].input.pdfObservations,[reference]);assert.equal(final.value.matches[0].input.ocrResultId,null);
  assert.deepEqual(final.value.matches[0].input.reviewedInput,documentInput);assert.deepEqual(final.value.matches[0].input.lines,input.lines);
  const history=matchPage.getByRole('region',{name:'Riwayat pencocokan',exact:true});await history.waitFor();
  await history.getByRole('region',{name:'Rujukan PDF tersimpan untuk pencocokan',exact:true}).waitFor();assert.match(await history.innerText(),/Total dokumen · halaman 2/);
  await history.locator('summary').filter({hasText:'Rincian rujukan dokumen'}).click();assert.ok((await history.innerText()).includes(shown.result.resultHash));
  for(const [width,height,label]of [[1280,960,'desktop'],[390,844,'mobile']]){
    await matchPage.setViewportSize({width,height});assert.equal(await matchPage.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    await matchPage.screenshot({path:resolve(directory,'supplier-pdf-match-saved-'+label+'.png'),fullPage:true});
  }
  await open(page,true);const requester=await post(page,config.actorId,sourcePrefix+'/query',{purpose});assert.equal(requester.status,200);assert.deepEqual(requester.value.matches[0].input.pdfObservations,[reference]);
  const matcherRecovery=matchingRecoveries.filter(item=>item.actor===config.reviewerId);assert.equal(matcherRecovery.length,1);assert.equal(matcherRecovery[0].body.key,pending.key);
  assert.equal(matcherRecovery[0].revision,undefined);assert.equal(matchingWrites.length,1);assert.equal(dropped,1);
  assert.equal(writes.filter(row=>row.path===sourcePrefix+'/pdf-evidence').length,1);assert.equal(writes.filter(row=>row.path.endsWith('/content')).length,1);assert.equal(writes.filter(row=>row.path===submissionPath).length,1);
  assert.ok(writes.every(row=>row.actor===config.actorId&&row.csrf&&row.origin===origin.origin&&typeof row.key==='string'));
  assert.ok(matchingWrites.every(row=>row.actor===config.reviewerId&&row.csrf&&row.origin===origin.origin&&row.revision==='"1"'));
  assert.ok(reads.every(row=>row.csrf&&row.origin===origin.origin));assert.deepEqual(errors,[]);
  writeFileSync(resolve(directory,'browser-proof.json'),JSON.stringify({sourceId:config.sourceId,documentId,reservationId,evidence,resultId:shown.result.id,resultHash:shown.result.resultHash,
    pages:shown.result.pages.length,pdfObservations:[reference],manualPackages:2,manualNetAmount:'20000.00',manualTaxAmount:'2000.00',manualChargeAmount:'500.00',
    matchedOnce:true,noAutofill:true,originalKeyRecovery:true,lostCommittedReply:true,refreshedHistory:true,staleDenied:true,foreignDenied:true,foreignRecoveryWithheld:true,desktopMobile:true,
    matchingWrite:{path:matchPath,keyHash:keyHash(pending.key),actorMatches:true,revision:'1'},recovery:{keyHash:keyHash(pending.key),sameOriginalKey:true}},null,2));
  transportProof('passed');console.log('REAL_SUPPLIER_PDF_MATCH_BROWSER_PASS');
} catch (error) {
  transportProof('failed');console.error('SUPPLIER_PDF_MATCH_BROWSER_FAILURE ' + safe(error instanceof Error ? error.stack ?? error.message : error));
  for (const {who, page} of pages) {try {await page.screenshot({path: resolve(directory, `failed-${config.mode}-${who}.png`), fullPage: true}); writeFileSync(resolve(directory, `failed-${config.mode}-${who}.txt`), safe(await page.locator('body').innerText()));} catch {}}
  process.exitCode = 1;
} finally {
  await browser?.close(); for (const socket of sockets) socket.destroy();
  await Promise.allSettled([frontend, edge, tunnel].filter(Boolean).map(server => new Promise(yes => server.close(yes)))); process.exit(process.exitCode ?? 0);
}
