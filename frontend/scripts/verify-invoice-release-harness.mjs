// Same real Next/Kestrel TLS fixture used by the maintained browser proofs, limited to invoice release approval.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import {createRequire} from 'node:module';
import {appendFileSync, mkdirSync, readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import next from 'next';
import {verifyInvoiceReleaseBrowser} from './verify-invoice-release-browser.mjs';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(config.invoiceRelease, true); assert.equal(process.versions.node.split('.')[0], '24');
assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0);
assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
assert.equal(readFileSync(resolve('.next-full/BUILD_ID'), 'utf8').trim(), config.buildId);
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage);
assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const {chromium} = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const directory = config.directory; mkdirSync(directory, {recursive: true});
const trace = resolve(directory, config.proofPrefix + '-flow.jsonl'), errors = [], pages = [], sockets = new Set();
const app = next({dev: false, dir: process.cwd(), quiet: true});
let browser, frontend, edge, tunnel;
const record = value => appendFileSync(trace, JSON.stringify({at: new Date().toISOString(), ...value}) + '\n');
function track(server) {server.on('connection', socket => {sockets.add(socket); socket.on('close', () => sockets.delete(socket));}); return server;}
async function listen(server) {await new Promise((accept, reject) => {server.once('error', reject); server.listen(0, '127.0.0.1', accept);}); return server.address().port;}
function forward(req, res, port, isApi) {
  const headers = {...req.headers, host: '127.0.0.1:' + port}, started = performance.now();
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) {headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https';}
  let completed = false;
  const upstream = http.request({hostname: '127.0.0.1', port, path: req.url, method: req.method, headers}, response => {
    if (isApi) record({kind: 'api-response', method: req.method, path: req.url, status: response.statusCode, milliseconds: Math.round(performance.now() - started)});
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res); response.once('end', () => {completed = true;});
    response.on('error', () => res.destroy());
  });
  upstream.on('error', error => {record({kind: 'proxy-error', path: req.url, message: error.message}); if (!res.headersSent) res.writeHead(502); res.end();});
  res.once('close', () => {if (!res.writableEnded && !completed) upstream.destroy();}); req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(token, storageState) {
  const context = await browser.newContext({ignoreHTTPSErrors: true, viewport: {width: 360, height: 800}, locale: 'id-ID', timezoneId: 'Asia/Jakarta', ...(storageState ? {storageState} : {})});
  await context.addCookies([{name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax'}]);
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(30_000); page.setDefaultNavigationTimeout(30_000);
  page.on('pageerror', error => errors.push(error.message)); return {context, page};
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => {void handle(req, res).catch(() => {res.statusCode = 500; res.end();});})); const frontendPort = await listen(frontend);
  edge = track(https.createServer({pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password}, (req, res) => {
    if (req.headers.host !== origin.host) {res.writeHead(403); res.end(); return;}
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => {res.writeHead(403); res.end();}));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') {socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return;}
    const upstream = net.connect(tlsPort, '127.0.0.1', () => {socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket);});
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  const launch = () => chromium.launch({...(channel === 'msedge' ? {channel: 'msedge'} : {}), headless: true, proxy: {server: 'http://127.0.0.1:' + proxyPort}});
  browser = await launch();
  await verifyInvoiceReleaseBrowser({config, maker: await user(config.makerToken), user, origin, directory,
    restartUser: async (token, storageState) => {await browser.close(); browser = await launch(); return user(token, storageState);}});
  assert.deepEqual(errors, []);
} catch (failure) {
  record({kind: 'failure', message: String(failure), stack: failure?.stack}); console.error(failure); process.exitCode = 1;
  for (const [index, page] of pages.entries()) if (!page.isClosed()) await page.screenshot({path: resolve(directory, config.proofPrefix + '-failure-' + index + '.png'), fullPage: true}).catch(() => {});
} finally {
  let timer;
  try {await Promise.race([(async () => {if (browser) await browser.close(); for (const socket of sockets) socket.destroy();
    for (const server of [tunnel, edge, frontend]) if (server) await new Promise(accept => server.close(accept)); await app.close();})(),
    new Promise((_, reject) => {timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10_000);})]);}
  catch (failure) {console.error(failure); process.exitCode = 1;} finally {clearTimeout(timer);}
}
process.exit(process.exitCode ?? 0);
