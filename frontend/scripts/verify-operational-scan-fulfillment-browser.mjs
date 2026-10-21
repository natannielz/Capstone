// Local synthetic identities only; actual production Next/HTTPS/API and approved finite-stock owners.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import {createRequire} from 'node:module';
import {mkdir, writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {createHash} from 'node:crypto';
import {EventEmitter, once} from 'node:events';
import next from 'next';
// Order numbers as the screens show them (src/lib/document-numbers.ts).
const orderNumber = id => 'PSN-' + String(id).padStart(6, '0');

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1');
assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
assert.match(config.proofPrefix, /^operational-scan-fulfillment-[a-f0-9]{32}$/);
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const {chromium} = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium';
assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({dev: false, dir: process.cwd(), quiet: true}), sockets = new Set(), writes = [], recoveries = [], scans = [], errors = [], captures = [];
const committed = new EventEmitter(), directory = resolve('../tmp/production/operational-scan-browser-results');
let browser, frontend, edge, tunnel, fault = null, dropped = 0;
function track(server) { server.on('connection', socket => {sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => socket.destroy());}); return server; }
async function listen(server) { await new Promise((done, reject) => {server.once('error', reject); server.listen(0, '127.0.0.1', done);}); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = {...req.headers, host: '127.0.0.1:' + port};
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded','connection','x-fixture-control'].includes(name)) delete headers[name];
  if (isApi) {headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https';}
  const kind = req.method === 'POST' && /^\/api\/v1\/fulfillment\/preparations\/[1-9][0-9]*$/.test(req.url) ? 'preparation'
    : req.method === 'POST' && /^\/api\/v1\/fulfillment\/dispatches\/[1-9][0-9]*$/.test(req.url) ? 'dispatch' : null;
  const recovery = req.method === 'POST' && req.url.endsWith('/operations/query');
  const scan = req.method === 'POST' && req.url === '/api/v1/catalog/scans/query';
  const record = kind || recovery || scan ? {kind, path: req.url, actor: req.headers['x-expected-actor-id'], key: req.headers['idempotency-key'], version: req.headers['if-match'], status: null, body: null, response: null, elapsedMs: null} : null;
  const started = performance.now();
  if (record) {
    (kind ? writes : scan ? scans : recoveries).push(record); const body = []; let length = 0;
    req.on('data', bytes => {length += bytes.length; if (length <= 131072) body.push(bytes);});
    req.once('end', () => {assert.ok(length <= 131072); record.body = JSON.parse(Buffer.concat(body).toString('utf8'));});
  }
  const lose = kind && fault === kind; if (lose) fault = null;
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    if (!record) {res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res); return;}
    const body = []; let length = 0;
    response.on('data', bytes => {length += bytes.length; if (length <= 131072) body.push(bytes);});
    response.once('end', () => {
      assert.ok(length <= 131072); const bytes = Buffer.concat(body);
      record.status = response.statusCode; record.response = JSON.parse(bytes.toString('utf8')); record.elapsedMs = performance.now() - started;
      if (kind) committed.emit(kind, record);
      if (res.destroyed) return;
      if (lose && response.statusCode === 200) {dropped++; res.writeHead(200, {'content-type':'application/json','cache-control':'private, no-store'}); res.end('{');}
      else {res.writeHead(response.statusCode ?? 502, response.headers); res.end(bytes);}
    });
  });
  upstream.on('error', () => {if (!res.headersSent) res.writeHead(502); res.end();}); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function renew(context) {
  const response = await new Promise((done, reject) => {
    const req = http.request(new URL('/__fixture/operational-scan/session', api), {method:'POST', headers:{'X-Fixture-Control':config.controlKey}}, res => {
      const chunks = []; res.on('data', chunk => chunks.push(chunk)); res.once('end', () => done({status:res.statusCode, body:JSON.parse(Buffer.concat(chunks).toString('utf8'))}));
    }); req.on('error', reject); req.end();
  });
  assert.equal(response.status, 200); await context.addCookies([{name:'__Host-ut_session',value:response.body.token,domain:origin.hostname,path:'/',secure:true,httpOnly:true,sameSite:'Lax'}]);
}
async function capture(page, state) {
  await mkdir(directory, {recursive:true}); const old = page.viewportSize();
  for (const width of [1280,360]) {
    await page.setViewportSize({width,height:900}); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    const file = config.proofPrefix + '-' + state + '-' + width + '.png', bytes = await page.screenshot({path:resolve(directory,file),fullPage:true});
    captures.push({state,width,file,sha256:createHash('sha256').update(bytes).digest('hex')});
  } await page.setViewportSize(old);
}
async function pending(page, operation, record) {
  const refs = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(refs.length,1); assert.deepEqual(Object.keys(refs[0]).sort(),['actorId','createdAt','key','operation']);
  assert.equal(refs[0].actorId,config.dispatcherId); assert.equal(refs[0].operation,operation); assert.equal(refs[0].key,record.key);
}
async function scan(page, purpose, code, result) {
  const panel = page.getByRole('region',{name:'Periksa kode barang',exact:true}), count = scans.length;
  await panel.getByLabel('Jenis kode',{exact:true}).selectOption('sku');
  await panel.getByLabel('Kode hasil pindai',{exact:true}).fill(code);
  await panel.getByRole('button',{name:'Periksa kode',exact:true}).click();
  await page.waitForFunction(() => [...document.querySelectorAll('[aria-label="Periksa kode barang"]')].some(item => item.getAttribute('aria-busy') === 'false'));
  assert.equal(scans.length,count+1); const record = scans.at(-1); assert.equal(record.status,200); assert.equal(record.body.purpose,purpose); assert.equal(record.response.kind,result);
  if (result === 'unknown') {assert.equal(await panel.getByRole('button',{name:'Konfirmasi kemasan hasil pindai',exact:true}).count(),0); return;}
  const choice = record.response.candidates[0]; assert.equal(choice.skuId,config.skuId); assert.equal(choice.definitionId,config.definitionId);
  assert.equal(choice.definitionVersion,config.definitionVersion); assert.equal(choice.selection.identifier.identifierId,config.identifierId);
  assert.equal(choice.selection.identifier.identifierRevision,config.identifierRevision);
  await panel.getByRole('button',{name:'Konfirmasi kemasan hasil pindai',exact:true}).click(); return choice.selection;
}
async function saveLost(page, panel, kind, label, selection) {
  fault = kind; const deadline = AbortSignal.timeout(30000), waiting = once(committed,kind,{signal:deadline}); void waiting.catch(() => {});
  await panel.getByRole('button',{name:label,exact:true}).click(); const [record] = await waiting;
  assert.equal(record.status,200); assert.ok(record.elapsedMs < 30000, 'Actual owner response must stay within the existing30-second command window');
  assert.equal(record.actor,config.dispatcherId); assert.deepEqual(record.body.scanSelections,[selection]);
  await page.getByRole('button',{name:'Periksa hasil',exact:true}).waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent?.trim() === 'Periksa hasil' && !button.disabled));
  await pending(page,kind === 'preparation' ? 'fulfillment.preparation.save' : 'fulfillment.shipment.dispatch',record);
  return record;
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req,res) => {void handle(req,res).catch(() => {res.statusCode=500;res.end();});})); const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx:Buffer.from(config.pfx,'base64'),passphrase:config.password},(req,res) => {
    if (req.headers.host !== origin.host) {res.writeHead(403);res.end();return;}
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req,res,isApi ? Number(api.port) : frontendPort,isApi);
  })); const edgePort = await listen(edge);
  tunnel = track(http.createServer((_req,res) => {res.writeHead(403);res.end();}));
  tunnel.on('connect',(req,socket,head) => {
    if (req.url !== origin.host+':443') {socket.end('HTTP/1.1 403 Forbidden\r\n\r\n');return;}
    const upstream = net.connect(edgePort,'127.0.0.1',() => {socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');if(head.length)upstream.write(head);socket.pipe(upstream);upstream.pipe(socket);});
    sockets.add(upstream); upstream.on('close',() => sockets.delete(upstream)); upstream.on('error',() => socket.destroy()); socket.on('error',() => upstream.destroy()); socket.on('close',() => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({...(channel === 'msedge' ? {channel:'msedge'} : {}),headless:true,proxy:{server:'http://127.0.0.1:'+proxyPort}});
  const context = await browser.newContext({ignoreHTTPSErrors:true,viewport:{width:1280,height:900},locale:'id-ID',timezoneId:'Asia/Jakarta'});
  await renew(context); const page = await context.newPage(); page.setDefaultTimeout(30000); page.on('pageerror',error => errors.push(error.message));
  await page.goto(origin.origin+'/fulfillment/preparations');
  await page.getByRole('button',{name:'Siapkan pesanan '+orderNumber(config.orderId)+' / '+config.suborderId,exact:true}).click();
  let panel = page.getByRole('region',{name:'Rencana persiapan',exact:true});
  assert.equal(await panel.getByRole('checkbox').isChecked(),false);
  await scan(page,'picking','UNKNOWN-SCAN-CODE','unknown'); assert.equal(writes.length,0); assert.equal(await panel.getByRole('checkbox').isChecked(),false);
  const picked = await scan(page,'picking',config.skuCode,'match');
  assert.equal(await panel.getByRole('checkbox').isChecked(),true); assert.equal(await panel.getByLabel('Jumlah '+config.skuCode,{exact:true}).inputValue(),'');
  await panel.getByLabel('Jumlah '+config.skuCode,{exact:true}).fill(String(config.packages));
  await panel.getByLabel('Catatan persiapan',{exact:true}).fill('Synthetic confirmed exact scan picking');
  await panel.getByRole('button',{name:'Tinjau persiapan',exact:true}).click(); await panel.getByRole('heading',{name:'Periksa kemasan yang dipilih',exact:true}).waitFor();
  await capture(page,'picking-review'); const preparation = await saveLost(page,panel,'preparation','Konfirmasi persiapan',picked);
  await page.reload(); await page.getByRole('button',{name:'Periksa hasil',exact:true}).click();
  await page.getByText('Persiapan ditemukan dan tercatat. Kiriman belum diberangkatkan.',{exact:true}).waitFor();
  assert.equal(writes.length,1); assert.ok(recoveries.some(record => record.status === 200 && record.body.key === preparation.key));
  await page.reload(); await page.getByRole('button',{name:'Siapkan pesanan '+orderNumber(config.orderId)+' / '+config.suborderId,exact:true}).click();
  await page.getByText('Persiapan versi 1 tersimpan.',{exact:true}).waitFor(); await capture(page,'picking-reloaded');
  await renew(context); await page.goto(origin.origin+'/fulfillment/dispatches');
  await page.getByRole('button',{name:'Periksa keberangkatan pesanan '+orderNumber(config.orderId),exact:true}).click();
  panel = page.getByRole('region',{name:'Pemeriksaan keberangkatan',exact:true});
  const quantity = panel.getByLabel('Jumlah lot '+config.lotId,{exact:true}), courier = panel.getByLabel('Kurir',{exact:true});
  assert.equal(await quantity.inputValue(),''); assert.equal(await courier.inputValue(),'');
  await scan(page,'dispatch','UNKNOWN-SCAN-CODE','unknown'); assert.equal(writes.length,1); assert.equal(await quantity.inputValue(),'');
  const dispatched = await scan(page,'dispatch',config.skuCode,'match'); assert.equal(await quantity.inputValue(),''); assert.equal(await courier.inputValue(),'');
  await quantity.fill(String(config.packages)); await courier.selectOption(config.courierId);
  await panel.getByLabel('Catatan keberangkatan',{exact:true}).fill('Synthetic explicit confirmed scan lot handover');
  await panel.getByRole('button',{name:'Tinjau keberangkatan',exact:true}).click(); await panel.getByRole('heading',{name:'Konfirmasi barang dan kurir',exact:true}).waitFor();
  await capture(page,'dispatch-review'); const dispatch = await saveLost(page,panel,'dispatch','Konfirmasi keberangkatan',dispatched);
  await page.reload(); await page.getByRole('button',{name:'Periksa hasil',exact:true}).click();
  await page.getByRole('heading',{name:'Keberangkatan tercatat',exact:true}).waitFor();
  assert.equal(writes.length,2); assert.ok(recoveries.some(record => record.status === 200 && record.body.key === dispatch.key));
  assert.equal(dropped,2); assert.deepEqual(errors,[]);
  assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length),0);
  await capture(page,'dispatch-recovered');
  await writeFile(resolve(directory,config.proofPrefix+'-proof.json'),JSON.stringify({recordedAt:new Date().toISOString(),scans,writes,recoveries,captures,droppedCommittedResponses:dropped,
    actualApprovedOwners:true,syntheticProviderAndAccounts:true,productionAccepted:false},null,2));
  console.log('REAL_OPERATIONAL_SCAN_FULFILLMENT_BROWSER_PASS '+JSON.stringify({picking:1,dispatch:1,unknownWithoutWrites:2,originalKeysRecovered:2,captures:captures.length}));
} catch (error) {console.error(error.stack ?? String(error)); process.exitCode=1;}
finally {if(browser)await browser.close();for(const socket of sockets)socket.destroy();for(const server of[tunnel,edge,frontend])if(server)await new Promise(done=>server.close(done));await app.close();}
