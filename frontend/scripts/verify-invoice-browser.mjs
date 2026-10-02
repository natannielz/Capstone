// Actual production Next + Kestrel + isolated Mongo. All accounts and approvals are synthetic.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createRequire } from 'node:module';
import { appendFileSync, existsSync, mkdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import next from 'next';
import { verifyReturnFinancialApprovalBrowser } from './verify-return-financial-approval-browser.mjs';
import { verifyRefundPayoutBrowser } from './verify-refund-payout-browser.mjs';
import { verifyRefundSettlementBrowser } from './verify-refund-settlement-browser.mjs';
import { verifyOrganizationAddressBrowser } from './verify-organization-address-browser.mjs';
import { verifyOrderSpendingPolicyBrowser } from './verify-order-spending-policy-browser.mjs';
import { verifyAccountingBooksBrowser } from './verify-accounting-books-browser.mjs';
import { verifyProcurementRequestsBrowser } from './verify-procurement-requests-browser.mjs';
import { verifySuppliersBrowser } from './verify-suppliers-browser.mjs';
import { verifyPurchaseOrdersBrowser } from './verify-purchase-orders-browser.mjs';
import { verifyProcurementFundingBrowser } from './verify-procurement-funding-browser.mjs';

const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk);
const config = JSON.parse(Buffer.concat(chunks).toString('utf8')), api = new URL(config.apiAddress), origin = new URL(config.origin);
assert.equal(process.versions.node.split('.')[0], '24'); assert.equal(api.protocol, 'http:'); assert.equal(api.hostname, '127.0.0.1'); assert.ok(Number(api.port) > 0); assert.equal(origin.href, 'https://portal.unit-toko.co.id/');
const load = createRequire(config.playwrightPackage), metadata = load(config.playwrightPackage); assert.equal(metadata.name, 'playwright'); assert.equal(metadata.version, '1.62.1');
const { chromium } = load('playwright'), channel = process.env.UNITTOKO_BROWSER_CHANNEL ?? 'chromium'; assert.ok(['chromium', 'msedge'].includes(channel));
const app = next({ dev: false, dir: process.cwd(), quiet: true }), sockets = new Set(), writes = [], caseWrites = [], decisionWrites = [], returnWrites = [], inspectionWrites = [], resolutionWrites = [], entitlementWrites = [], financialWrites = [], financialRecoveries = [], draftWrites = [], payoutWrites = [], payoutRecoveries = [], settlementWrites = [], settlementRecoveries = [], settlementOriginalReads = [], addressWrites = [], addressRecoveries = [], errors = [], pages = [];
const directory = resolve('../tmp/production/invoice-browser-results'); mkdirSync(directory, { recursive: true });
const financialTracePath = config.financialApproval ? resolve(directory, 'return-financial-approval-flow-' + Date.now() + '.jsonl') : null;
const payoutTracePath = config.refundPayout ? resolve(directory, 'refund-payout-flow-' + Date.now() + '.jsonl') : null;
const settlementTracePath = config.refundSettlement ? resolve(directory, 'refund-settlement-flow-' + Date.now() + '.jsonl') : null;
const addressTracePath = config.organizationAddress ? resolve(directory, 'organization-address-flow-' + Date.now() + '.jsonl') : null;
const spendingWrites = [], spendingRecoveries = [], spendingTracePath = config.orderSpendingPolicy ? resolve(directory, 'order-spending-policy-flow-' + Date.now() + '.jsonl') : null;
const bookWrites = [], bookRecoveries = [], bookTracePath = config.accountingBooks ? resolve(directory, 'accounting-books-flow-' + Date.now() + '.jsonl') : null;
const procurementWrites = [], procurementRecoveries = [], procurementTracePath = config.procurementRequests ? resolve(directory, 'procurement-requests-flow-' + Date.now() + '.jsonl') : null;
const supplierWrites = [], supplierRecoveries = [], supplierTracePath = config.suppliers ? resolve(directory, 'supplier-flow-' + Date.now() + '.jsonl') : null;
const purchaseOrderWrites = [], purchaseOrderRecoveries = [], purchaseOrderTracePath = config.purchaseOrders ? resolve(directory, 'purchase-order-flow-' + Date.now() + '.jsonl') : null;
const fundingWrites = [], fundingRecoveries = [], fundingTracePath = config.procurementFunding ? resolve(directory, 'procurement-funding-flow-' + Date.now() + '.jsonl') : null;
function traceFinancial(type, value) { for (const path of [financialTracePath, payoutTracePath, settlementTracePath, addressTracePath, spendingTracePath, bookTracePath, procurementTracePath, supplierTracePath, purchaseOrderTracePath, fundingTracePath]) if (path) appendFileSync(path, JSON.stringify({ at: new Date().toISOString(), type, ...value }) + '\n'); }
traceFinancial('host-start', {});
let browser, frontend, edge, tunnel, dropNext = false, dropped = 0;
function track(server) { server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); }); return server; }
async function listen(server) { await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); return server.address().port; }
function forward(req, res, port, isApi) {
  const headers = { ...req.headers, host: '127.0.0.1:' + port };
  for (const name of Object.keys(headers)) if (name.startsWith('x-forwarded-') || name.startsWith('x-original-') || ['forwarded', 'connection'].includes(name)) delete headers[name];
  if (isApi) { headers['x-forwarded-for'] = req.socket.remoteAddress; headers['x-forwarded-host'] = origin.host; headers['x-forwarded-proto'] = 'https'; }
  const write = req.method === 'POST' && req.url === '/api/v1/billing/invoices'; if (write) writes.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const caseWrite = req.method === 'POST' && req.url === '/api/v1/fulfillment/cases'; if (caseWrite) caseWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const decisionWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/cases\/[1-9][0-9]*\/decision$/.test(req.url); if (decisionWrite) decisionWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const returnWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/cases\/[1-9][0-9]*\/returns$/.test(req.url); if (returnWrite) returnWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const inspectionWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/inspection$/.test(req.url); if (inspectionWrite) inspectionWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const resolutionWrite = req.method === 'POST' && /^\/api\/v1\/fulfillment\/cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/resolution$/.test(req.url); if (resolutionWrite) resolutionWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const entitlementWrite = req.method === 'POST' && /^\/api\/v1\/billing\/cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/entitlement$/.test(req.url); if (entitlementWrite) entitlementWrites.push({ key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'] });
  const financialWrite = req.method === 'POST' && /^\/api\/v1\/billing\/cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/financial-approval$/.test(req.url);
  const financialRecovery = req.method === 'POST' && /^\/api\/v1\/billing\/cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/financial-approval\/operations\/query$/.test(req.url);
  const financialPath = config.financialApproval && /^\/api\/v1\/billing\/cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/financial-approval(?:\/.*)?$/.test(req.url);
  const draftWrite = req.method === 'POST' && /^\/api\/v1\/billing\/refund-approvals\/[1-9][0-9]*\/payout-drafts$/.test(req.url);
  const payoutWrite = req.method === 'POST' && /^\/api\/v1\/billing\/refund-payout-drafts\/[1-9][0-9]*\/record$/.test(req.url);
  const payoutRecovery = req.method === 'POST' && /^\/api\/v1\/billing\/refund-payout-drafts\/[1-9][0-9]*\/operations\/query$/.test(req.url);
  const payoutPath = config.refundPayout && /^\/api\/v1\/billing\/(?:refund-approvals\/|refund-payout-drafts\/|cases\/[1-9][0-9]*\/returns\/[1-9][0-9]*\/refund-payout\/)/.test(req.url);
  const settlementWrite = req.method === 'POST' && /^\/api\/v1\/billing\/refund-payouts\/[1-9][0-9]*\/settlement\/verify$/.test(req.url);
  const settlementRecovery = req.method === 'POST' && /^\/api\/v1\/billing\/refund-payouts\/[1-9][0-9]*\/settlement\/operations\/query$/.test(req.url);
  const settlementOriginal = req.method === 'POST' && /^\/api\/v1\/billing\/refund-payouts\/[1-9][0-9]*\/settlement\/evidence\/[1-9][0-9]*\/original\/query$/.test(req.url);
  const settlementPath = config.refundSettlement && /^\/api\/v1\/billing\/refund-payouts\/[1-9][0-9]*\/settlement\//.test(req.url);
  const addressWrite = req.method === 'POST' && /^\/api\/v1\/organizations\/addresses\/(?:proposals|proposals\/[1-9][0-9]*\/decide)$/.test(req.url);
  const addressRecovery = req.method === 'POST' && req.url === '/api/v1/organizations/addresses/operations/query';
  const addressPath = config.organizationAddress && req.url.startsWith('/api/v1/organizations/addresses/');
  const spendingWrite = req.method === 'POST' && /^\/api\/v1\/ordering\/spending-policies\/(?:proposals|proposals\/[1-9][0-9]*\/decisions)$/.test(req.url);
  const spendingRecovery = req.method === 'POST' && req.url === '/api/v1/ordering/spending-policies/operations/query';
  const spendingPath = config.orderSpendingPolicy && req.url.startsWith('/api/v1/ordering/spending-policies/');
  const bookWrite = req.method === 'POST' && /^\/api\/v1\/accounting\/books\/(?:proposals|proposals\/[1-9][0-9]*\/decisions)$/.test(req.url);
  const bookRecovery = req.method === 'POST' && req.url === '/api/v1/accounting/books/operations/query';
  const bookPath = config.accountingBooks && req.url.startsWith('/api/v1/accounting/books/');
  const procurementWrite = req.method === 'POST' && /^\/api\/v1\/procurement\/requests\/(?:submissions|[1-9][0-9]*\/decisions)$/.test(req.url);
  const procurementRecovery = req.method === 'POST' && req.url === '/api/v1/procurement/requests/operations/query';
  const procurementPath = config.procurementRequests && req.url.startsWith('/api/v1/procurement/requests/');
  const supplierWrite = req.method === 'POST' && /^\/api\/v1\/procurement\/suppliers\/(?:proposals|proposals\/[1-9][0-9]*\/(?:observations|decisions))$/.test(req.url);
  const supplierRecovery = req.method === 'POST' && req.url === '/api/v1/procurement/suppliers/operations/query';
  const supplierPath = config.suppliers && req.url.startsWith('/api/v1/procurement/suppliers/');
  const purchaseOrderWrite = req.method === 'POST' && /^\/api\/v1\/procurement\/purchase-orders\/(?:proposals|proposals\/[1-9][0-9]*\/decisions)$/.test(req.url);
  const purchaseOrderRecovery = req.method === 'POST' && req.url === '/api/v1/procurement/purchase-orders/operations/query';
  const purchaseOrderPath = config.purchaseOrders && req.url.startsWith('/api/v1/procurement/purchase-orders/');
  const fundingWrite = req.method === 'POST' && /^\/api\/v1\/procurement\/funding\/(?:requests|requests\/[1-9][0-9]*\/decisions)$/.test(req.url);
  const fundingRecovery = req.method === 'POST' && req.url === '/api/v1/procurement/funding/operations/query';
  const fundingPath = config.procurementFunding && req.url.startsWith('/api/v1/procurement/funding/');
  if (financialPath || payoutPath || settlementPath || addressPath || spendingPath || bookPath || procurementPath || supplierPath || purchaseOrderPath || fundingPath) traceFinancial('request-begin', { path: req.url, method: req.method, actor: req.headers['x-expected-actor-id'] });
  let financialReference;
  if (financialWrite || financialRecovery || draftWrite || payoutWrite || payoutRecovery || settlementWrite || settlementRecovery || settlementOriginal || addressWrite || addressRecovery || spendingWrite || spendingRecovery || bookWrite || bookRecovery || procurementWrite || procurementRecovery || supplierWrite || supplierRecovery || purchaseOrderWrite || purchaseOrderRecovery || fundingWrite || fundingRecovery) {
    const reference = { path: req.url, key: req.headers['idempotency-key'], actor: req.headers['x-expected-actor-id'], ...(addressWrite || spendingWrite || bookWrite || procurementWrite || supplierWrite || purchaseOrderWrite || fundingWrite ? { version: req.headers['if-match'] } : {}) }, body = [];
    financialReference = reference;
    (fundingWrite ? fundingWrites : fundingRecovery ? fundingRecoveries : purchaseOrderWrite ? purchaseOrderWrites : purchaseOrderRecovery ? purchaseOrderRecoveries : supplierWrite ? supplierWrites : supplierRecovery ? supplierRecoveries : procurementWrite ? procurementWrites : procurementRecovery ? procurementRecoveries : bookWrite ? bookWrites : bookRecovery ? bookRecoveries : spendingWrite ? spendingWrites : spendingRecovery ? spendingRecoveries : addressWrite ? addressWrites : addressRecovery ? addressRecoveries : financialWrite ? financialWrites : financialRecovery ? financialRecoveries : draftWrite ? draftWrites : payoutWrite ? payoutWrites : payoutRecovery ? payoutRecoveries : settlementWrite ? settlementWrites : settlementRecovery ? settlementRecoveries : settlementOriginalReads).push(reference);
    req.on('data', chunk => body.push(chunk)); req.on('end', () => { reference.body = JSON.parse(Buffer.concat(body).toString('utf8')); });
  }
  const drop = (write || caseWrite || decisionWrite || returnWrite || inspectionWrite || resolutionWrite || entitlementWrite || financialWrite || payoutWrite || settlementWrite || addressWrite || spendingWrite || bookWrite || procurementWrite || supplierWrite || purchaseOrderWrite || fundingWrite) && dropNext; if (drop) dropNext = false;
  const startedAt = performance.now(); let upstreamCompleted = false;
  const upstream = http.request({ hostname: '127.0.0.1', port, path: req.url, method: req.method, headers }, response => {
    response.once('end', () => { upstreamCompleted = true; });
    if (financialPath || payoutPath || settlementPath || addressPath || spendingPath || bookPath || procurementPath || supplierPath || purchaseOrderPath || fundingPath) traceFinancial('response-headers', { path: req.url, status: response.statusCode, elapsedMs: Math.round(performance.now() - startedAt) });
    if (financialReference) { financialReference.status = response.statusCode; financialReference.elapsedMs = Math.round(performance.now() - startedAt); }
    if (drop && response.statusCode === 200) { response.resume(); response.once('end', () => { dropped++; if (financialWrite || payoutWrite || settlementWrite || addressWrite || spendingWrite || bookWrite || procurementWrite || supplierWrite || purchaseOrderWrite || fundingWrite) traceFinancial('actual-200-response-ended-before-intentional-loss', { path: req.url, elapsedMs: Math.round(performance.now() - startedAt) }); res.writeHead(200, { 'content-type': 'application/json', 'content-length': '64', 'cache-control': 'no-store' }); res.flushHeaders(); res.write('{'); setTimeout(() => res.destroy(), 25); }); return; }
    res.writeHead(response.statusCode ?? 502, response.headers); response.pipe(res);
  }); upstream.on('error', () => { if (res.destroyed || res.writableEnded) return; if (!res.headersSent) res.writeHead(502); res.end(); });
  res.once('close', () => { if (!res.writableEnded && !upstreamCompleted) { if (isApi) traceFinancial('downstream-cancelled', { path: req.url, method: req.method, elapsedMs: Math.round(performance.now() - startedAt) }); upstream.destroy(); } });
  req.on('aborted', () => upstream.destroy()); req.pipe(upstream);
}
async function user(token) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 360, height: 800 }, locale: 'id-ID', timezoneId: 'Asia/Jakarta' });
  await context.addCookies([{ name: '__Host-ut_session', value: token, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  const page = await context.newPage(); pages.push(page); page.setDefaultTimeout(15000); page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => { if (response.url().includes('/api/v1/') && response.status() >= 400) { const value = await response.json().catch(() => ({})); console.log('FIXTURE API ' + JSON.stringify({ path: new URL(response.url()).pathname, status: response.status(), code: value?.code })); } }); return { context, page };
}
try {
  await app.prepare(); const handle = app.getRequestHandler();
  frontend = track(http.createServer((req, res) => { void handle(req, res).catch(() => { res.statusCode = 500; res.end(); }); })); const frontendPort = await listen(frontend);
  edge = track(https.createServer({ pfx: Buffer.from(config.pfx, 'base64'), passphrase: config.password }, (req, res) => {
    if (req.headers.host !== origin.host) { res.writeHead(403); res.end(); return; }
    const isApi = req.url.startsWith('/api/v1/') || req.url.startsWith('/auth/'); forward(req, res, isApi ? Number(api.port) : frontendPort, isApi);
  })); const tlsPort = await listen(edge);
  tunnel = track(http.createServer((_req, res) => { res.writeHead(403); res.end(); }));
  tunnel.on('connect', (req, socket, head) => {
    if (req.url !== origin.host + ':443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    const upstream = net.connect(tlsPort, '127.0.0.1', () => { socket.write('HTTP/1.1 200 Connection Established\r\n\r\n'); if (head.length) upstream.write(head); socket.pipe(upstream); upstream.pipe(socket); });
    sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream)); upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy()); socket.on('close', () => upstream.destroy());
  }); const proxyPort = await listen(tunnel);
  browser = await chromium.launch({ ...(channel === 'msedge' ? { channel: 'msedge' } : {}), headless: true, proxy: { server: 'http://127.0.0.1:' + proxyPort } });
  const finance = await user(config.financeToken), page = finance.page;
  if (config.procurementFunding) {
    assert.equal(config.financeToken, config.makerToken);
    await verifyProcurementFundingBrowser({ config, maker: finance, user, origin, directory, fundingWrites, fundingRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length + addressWrites.length + spendingWrites.length + bookWrites.length + procurementWrites.length + supplierWrites.length + purchaseOrderWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.purchaseOrders) {
    assert.equal(config.financeToken, config.makerToken);
    await verifyPurchaseOrdersBrowser({ config, maker: finance, user, origin, directory, purchaseOrderWrites, purchaseOrderRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length + addressWrites.length + spendingWrites.length + bookWrites.length + procurementWrites.length + supplierWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.suppliers) {
    assert.equal(config.financeToken, config.makerToken);
    await verifySuppliersBrowser({ config, maker: finance, user, origin, directory, supplierWrites, supplierRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length + addressWrites.length + spendingWrites.length + bookWrites.length + procurementWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.procurementRequests) {
    assert.equal(config.financeToken, config.makerToken);
    await verifyProcurementRequestsBrowser({ config, maker: finance, user, origin, directory, procurementWrites, procurementRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length + addressWrites.length + spendingWrites.length + bookWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.accountingBooks) {
    assert.equal(config.financeToken, config.makerToken);
    await verifyAccountingBooksBrowser({ config, maker: finance, user, origin, directory, bookWrites, bookRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length + addressWrites.length + spendingWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.orderSpendingPolicy) {
    assert.equal(config.financeToken, config.makerToken);
    await verifyOrderSpendingPolicyBrowser({ config, maker: finance, user, origin, directory, spendingWrites, spendingRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length + addressWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.organizationAddress) {
    assert.equal(config.financeToken, config.makerToken);
    await verifyOrganizationAddressBrowser({ config, maker: finance, user, origin, directory, addressWrites, addressRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length + settlementWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.refundSettlement) {
    assert.equal(config.financeToken, config.verifierToken);
    await verifyRefundSettlementBrowser({ config, verifier: finance, user, origin, directory, settlementWrites, settlementRecoveries, originalReads: settlementOriginalReads,
      dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length + payoutWrites.length + draftWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.refundPayout) {
    assert.equal(config.financeToken, config.recorderToken); assert.equal(typeof config.validationReadyPath, 'string');
    await verifyRefundPayoutBrowser({ config, recorder: finance, user, origin, directory, draftWrites, payoutWrites, payoutRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped,
      validateEvidence: async () => {
        assert.equal(existsSync(config.validationReadyPath), false); traceFinancial('actual-evidence-validation-requested', {});
        console.log('VALIDATE_REFUND_PAYOUT_EVIDENCE_READY');
        const startedAt = performance.now();
        while (!existsSync(config.validationReadyPath)) { assert.ok(performance.now() - startedAt < 60000, 'Actual private evidence validation did not finish within 60 seconds'); await new Promise(resolve => setTimeout(resolve, 100)); }
        traceFinancial('actual-evidence-validation-completed', { elapsedMs: Math.round(performance.now() - startedAt) });
      } });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length + financialWrites.length, 0);
    assert.deepEqual(errors, []);
  } else if (config.financialApproval) {
    await verifyReturnFinancialApprovalBrowser({ config, finance, user, origin, directory, financialWrites, financialRecoveries, dropNext: () => { dropNext = true; }, dropped: () => dropped });
    assert.equal(writes.length + caseWrites.length + decisionWrites.length + returnWrites.length + inspectionWrites.length + resolutionWrites.length + entitlementWrites.length, 0);
    assert.deepEqual(errors, []);
  } else {
  await page.goto(origin.origin + '/identity/account'); await page.getByRole('link', { name: /^Tagihan\b/ }).click();
  const candidates = page.getByRole('region', { name: 'Penerimaan untuk invoice', exact: true });
  await candidates.getByRole('checkbox').check(); await candidates.getByLabel('Alasan penerbitan', { exact: true }).fill('Bukti penerimaan cabang diperiksa');
  await candidates.getByRole('button', { name: 'Tinjau invoice', exact: true }).click();
  const review = page.getByRole('region', { name: 'Tinjauan invoice', exact: true }); await review.waitFor();
  const heading = review.getByRole('heading', { name: 'Tinjau penerbitan invoice', exact: true }); assert.equal(await heading.evaluate(element => element === document.activeElement), true);
  await review.getByText(/Diterima: 6/).waitFor(); await review.getByRole('button', { name: 'Kembali ke pilihan penerimaan', exact: true }).click(); assert.equal(writes.length, 0);
  await candidates.getByRole('button', { name: 'Tinjau invoice', exact: true }).click();
  await review.waitFor();
  await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await review.getByRole('button', { name: 'Terbitkan invoice', exact: true }).isDisabled(), true); assert.equal(writes.length, 0);
  await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'invoice-review-mobile.png'), fullPage: true });
  dropNext = true; await review.getByRole('button', { name: 'Terbitkan invoice', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  const references = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(references.length, 1); assert.equal(references[0].operation, 'billing.invoice.issue'); assert.deepEqual(Object.keys(references[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByText(/^Invoice tersimpan/).waitFor();
  assert.equal(writes.length, 1); assert.equal(writes[0].actor, config.financeId); assert.equal(dropped, 1); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await candidates.getByRole('checkbox').isDisabled(), true);
  const buyer = await user(config.buyerToken); await buyer.page.goto(origin.origin + '/billing/invoices');
  assert.equal(await buyer.page.getByRole('region', { name: 'Penerimaan untuk invoice', exact: true }).count(), 0);
  await buyer.page.getByRole('button', { name: 'Buka invoice 100/103/2026/1', exact: true }).click();
  const download = buyer.page.waitForEvent('download'); await buyer.page.getByRole('button', { name: 'Unduh invoice', exact: true }).click();
  const file = await download, bytes = readFileSync(await file.path()); assert.match(file.suggestedFilename(), /^invoice-[1-9][0-9]*\.html$/);
  assert.ok(bytes.includes(Buffer.from('Cabang fixture'))); assert.ok(bytes.includes(Buffer.from('Alamat badan fixture'))); assert.ok(!bytes.includes(Buffer.from('recordHash')));
  assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'invoice-buyer-mobile.png'), fullPage: true });
  if (config.afterSales) {
    await buyer.page.getByRole('link', { name: 'Laporkan masalah barang', exact: true }).click();
    await buyer.page.getByRole('button', { name: 'Periksa barang pada invoice', exact: true }).click();
    const form = buyer.page.getByRole('region', { name: 'Pengajuan keluhan', exact: true }); await form.waitFor();
    await form.getByText(/Diterima: 6/).waitFor(); await form.getByLabel(/^Jumlah bermasalah/).fill('1');
    await form.getByLabel('Jelaskan masalah', { exact: true }).fill('Satu barang rusak saat dibuka');
    await form.getByRole('button', { name: 'Tinjau keluhan', exact: true }).click();
    const caseReview = buyer.page.getByRole('region', { name: 'Tinjauan keluhan', exact: true }); await caseReview.waitFor();
    assert.equal(await caseReview.getByRole('heading', { name: 'Tinjau keluhan barang', exact: true }).evaluate(element => element === document.activeElement), true);
    await caseReview.getByRole('button', { name: 'Kembali ke isian', exact: true }).click(); assert.equal(caseWrites.length, 0);
    await form.getByRole('button', { name: 'Tinjau keluhan', exact: true }).click(); await caseReview.getByRole('checkbox').check();
    await buyer.context.setOffline(true); await buyer.page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await caseReview.getByRole('button', { name: 'Kirim keluhan', exact: true }).isDisabled(), true);
    assert.equal(caseWrites.length, 0); await buyer.context.setOffline(false); await buyer.page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
    assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'case-review-mobile.png'), fullPage: true });
    dropNext = true; await caseReview.getByRole('button', { name: 'Kirim keluhan', exact: true }).click(); await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
    const caseReferences = await buyer.page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
    assert.equal(caseReferences.length, 1); assert.equal(caseReferences[0].operation, 'fulfillment.case.request'); assert.deepEqual(Object.keys(caseReferences[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.match(caseReferences[0].key, /^ca\.[1-9][0-9]*\./); assert.ok(!JSON.stringify(caseReferences).includes('rusak'));
    await buyer.page.reload(); await buyer.page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await buyer.page.getByText(/^Pengajuan keluhan \d+ tercatat/).waitFor();
    assert.equal(caseWrites.length, 1); assert.equal(caseWrites[0].actor, config.buyerId); assert.equal(dropped, 2); assert.equal(await buyer.page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
    await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); await buyer.page.getByRole('region', { name: 'Rincian keluhan', exact: true }).getByText('Satu barang rusak saat dibuka', { exact: true }).waitFor();
    assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'case-buyer-mobile.png'), fullPage: true });
    if (config.caseDecision) {
      assert.equal(await buyer.page.getByRole('button', { name: 'Periksa kewenangan keputusan', exact: true }).count(), 0);
      await page.goto(origin.origin + '/fulfillment/cases'); await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click();
      const decisionPanel = page.getByRole('region', { name: 'Pemeriksaan keluhan', exact: true });
      await decisionPanel.getByRole('button', { name: 'Periksa kewenangan keputusan', exact: true }).click();
      await decisionPanel.getByLabel(/^Jumlah retur/).fill('1'); await decisionPanel.getByLabel('Penjelasan keputusan untuk pelanggan', { exact: true }).fill('Kembalikan satu barang untuk pemeriksaan kondisi');
      await decisionPanel.getByLabel('Catatan pemeriksa internal', { exact: true }).fill('Pertimbangan pemeriksa privat');
      await decisionPanel.getByRole('button', { name: 'Tinjau keputusan keluhan', exact: true }).click();
      const decisionReview = decisionPanel.getByRole('region', { name: 'Konfirmasi keputusan keluhan', exact: true }); await decisionReview.waitFor();
      assert.equal(await decisionReview.getByRole('heading', { name: 'Izinkan pengembalian barang', exact: true }).evaluate(element => element === document.activeElement), true);
      await decisionReview.getByRole('button', { name: 'Kembali ke pemeriksaan', exact: true }).click(); assert.equal(decisionWrites.length, 0);
      await decisionPanel.getByRole('button', { name: 'Tinjau keputusan keluhan', exact: true }).click(); await decisionReview.getByRole('checkbox').check();
      await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await decisionReview.getByRole('button', { name: 'Simpan keputusan keluhan', exact: true }).isDisabled(), true); assert.equal(decisionWrites.length, 0);
      await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'case-decision-review-mobile.png'), fullPage: true });
      dropNext = true; await decisionReview.getByRole('button', { name: 'Simpan keputusan keluhan', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
      const pending = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
      assert.equal(pending.length, 1); assert.equal(pending[0].operation, 'fulfillment.case.decide'); assert.match(pending[0].key, /^cd\.[1-9][0-9]*\./);
      assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pending).includes('privat'));
      await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
      assert.equal(decisionWrites.length, 1); assert.equal(decisionWrites[0].actor, config.financeId); assert.equal(dropped, 3); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
      await buyer.page.reload(); await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click();
      const explanation = buyer.page.getByRole('region', { name: 'Keputusan keluhan', exact: true }); await explanation.getByText('Kembalikan satu barang untuk pemeriksaan kondisi', { exact: true }).waitFor();
      assert.ok(!(await buyer.page.locator('main').innerText()).includes('Pertimbangan pemeriksa privat')); assert.equal(await buyer.page.getByRole('button', { name: 'Periksa kewenangan keputusan', exact: true }).count(), 0);
      assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'case-decision-buyer-mobile.png'), fullPage: true });
      console.log('REAL_CASE_DECISION_BROWSER_PASS ' + JSON.stringify({ decisions: decisionWrites.length, independentReviewer: true, lostDecisionRecovered: true, privateNoteHidden: true, originalReportPreserved: true, offlineUnsent: true, mobile: true }));
      if (config.caseReturn) {
        assert.equal(await buyer.page.getByRole('button', { name: 'Periksa penerimaan retur', exact: true }).count(), 0);
        await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const intake = page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await intake.getByRole('button', { name: 'Periksa penerimaan retur', exact: true }).click(); await intake.getByLabel(/^Jumlah tiba/).fill('1'); await intake.getByLabel(/^Kondisi terlihat/).selectOption('opened');
        await intake.getByLabel('Catatan penerimaan untuk pelanggan', { exact: true }).fill('Satu barang diterima dalam karantina'); await intake.getByLabel('Catatan petugas retur internal', { exact: true }).fill('Pertimbangan retur privat');
        await intake.getByRole('button', { name: 'Tinjau penerimaan retur', exact: true }).click(); const returnReview = intake.getByRole('region', { name: 'Konfirmasi penerimaan retur', exact: true }); await returnReview.waitFor();
        assert.equal(await returnReview.getByRole('heading', { name: 'Terima barang dalam karantina', exact: true }).evaluate(element => element === document.activeElement), true);
        await returnReview.getByRole('button', { name: 'Kembali ke penerimaan', exact: true }).click(); assert.equal(returnWrites.length, 0);
        await intake.getByRole('button', { name: 'Tinjau penerimaan retur', exact: true }).click(); await returnReview.getByRole('checkbox').check();
        await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await returnReview.getByRole('button', { name: 'Catat penerimaan retur', exact: true }).isDisabled(), true); assert.equal(returnWrites.length, 0);
        await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        await page.screenshot({ path: resolve(directory, 'return-intake-review-mobile.png'), fullPage: true }); dropNext = true; await returnReview.getByRole('button', { name: 'Catat penerimaan retur', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
        const pendingReturn = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
        assert.equal(pendingReturn.length, 1); assert.equal(pendingReturn[0].operation, 'fulfillment.case.return_receive'); assert.match(pendingReturn[0].key, /^cr\.[1-9][0-9]*\./); assert.deepEqual(Object.keys(pendingReturn[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pendingReturn).includes('privat'));
        await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
        assert.equal(returnWrites.length, 1); assert.equal(returnWrites[0].actor, config.financeId); assert.equal(dropped, 4); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
        await buyer.page.reload(); await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const buyerReturns = buyer.page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await buyerReturns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click(); await buyerReturns.getByText('Satu barang diterima dalam karantina', { exact: true }).waitFor();
        await buyerReturns.getByText(/1 dari 1 .*telah diterima dalam karantina/).waitFor(); assert.ok(!(await buyer.page.locator('main').innerText()).includes('Pertimbangan retur privat')); assert.equal(await buyerReturns.getByRole('button', { name: 'Periksa penerimaan retur', exact: true }).count(), 0);
        assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'return-intake-buyer-mobile.png'), fullPage: true });
        console.log('REAL_RETURN_QUARANTINE_BROWSER_PASS ' + JSON.stringify({ receipts: returnWrites.length, lostIntakeRecovered: true, privateNoteHidden: true, baseUnits: 1, condition: 'opened', offlineUnsent: true, mobile: true }));
        assert.equal(await buyerReturns.getByRole('button', { name: /^Periksa barang retur/ }).count(), 0);
        await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const staffReturns = page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await staffReturns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click(); await staffReturns.getByRole('button', { name: /^Periksa barang retur/ }).click();
        const inspectionPanel = staffReturns.getByRole('region', { name: /^Pemeriksaan retur \d+$/ });
        await inspectionPanel.getByLabel(/^Identitas barang/).selectOption('matched'); await inspectionPanel.getByLabel(/^Kondisi hasil pemeriksaan/).selectOption('opened');
        await inspectionPanel.getByLabel(/^Identitas atau label yang diperiksa/).fill('Label produk terperiksa privat'); await inspectionPanel.getByLabel(/^Label lot yang terlihat/).fill('LOT-PRIVAT');
        await inspectionPanel.getByLabel('Hasil pemeriksaan untuk pelanggan', { exact: true }).fill('Produk telah diperiksa, kemasan terbuka'); await inspectionPanel.getByLabel('Catatan pemeriksaan internal', { exact: true }).fill('Penilaian pemeriksaan privat');
        await inspectionPanel.getByRole('button', { name: 'Tinjau hasil pemeriksaan', exact: true }).click(); const inspectionReview = inspectionPanel.getByRole('region', { name: 'Konfirmasi pemeriksaan retur', exact: true });
        await inspectionReview.waitFor(); assert.equal(await inspectionReview.getByRole('heading', { name: 'Catat hasil pemeriksaan barang', exact: true }).evaluate(element => element === document.activeElement), true);
        await inspectionReview.getByRole('button', { name: 'Kembali ke pemeriksaan', exact: true }).click(); assert.equal(inspectionWrites.length, 0);
        await inspectionPanel.getByRole('button', { name: 'Tinjau hasil pemeriksaan', exact: true }).click(); assert.equal(await inspectionReview.getByRole('button', { name: 'Catat pemeriksaan retur', exact: true }).isDisabled(), true); await inspectionReview.getByRole('checkbox').check();
        await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await inspectionReview.getByRole('button', { name: 'Catat pemeriksaan retur', exact: true }).isDisabled(), true); assert.equal(inspectionWrites.length, 0);
        await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        await page.screenshot({ path: resolve(directory, 'return-inspection-review-mobile.png'), fullPage: true }); dropNext = true; await inspectionReview.getByRole('button', { name: 'Catat pemeriksaan retur', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
        const pendingInspection = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
        assert.equal(pendingInspection.length, 1); assert.equal(pendingInspection[0].operation, 'fulfillment.case.return_inspect'); assert.match(pendingInspection[0].key, /^ci\.[1-9][0-9]*\.[1-9][0-9]*\./); assert.deepEqual(Object.keys(pendingInspection[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pendingInspection).includes('privat'));
        await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
        assert.equal(inspectionWrites.length, 1); assert.equal(inspectionWrites[0].actor, config.financeId); assert.equal(dropped, 5); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
        await buyer.page.reload(); await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const inspectedReturns = buyer.page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await inspectedReturns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click(); await inspectedReturns.getByRole('button', { name: /^Lihat hasil pemeriksaan/ }).click(); await inspectedReturns.getByText('Produk telah diperiksa, kemasan terbuka', { exact: true }).waitFor();
        const publicInspection = await buyer.page.locator('main').innerText(); for (const secret of ['Penilaian pemeriksaan privat', 'Label produk terperiksa privat', 'LOT-PRIVAT']) assert.ok(!publicInspection.includes(secret)); assert.equal(await inspectedReturns.getByRole('button', { name: /^Periksa barang retur/ }).count(), 0);
        assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'return-inspection-buyer-mobile.png'), fullPage: true });
        console.log('REAL_RETURN_INSPECTION_BROWSER_PASS ' + JSON.stringify({ inspections: inspectionWrites.length, lostInspectionRecovered: true, privateObservationsHidden: true, baseUnits: 1, identity: 'matched', condition: 'opened', staysQuarantined: true, offlineUnsent: true, mobile: true }));
        await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const resolutionReturns = page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await resolutionReturns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click(); await resolutionReturns.getByRole('button', { name: /^Lihat hasil pemeriksaan/ }).click();
        const resolutionPanel = resolutionReturns.getByRole('region', { name: /^Penyelesaian retur \d+$/ });
        await resolutionPanel.getByRole('combobox', { name: /^Pilihan penyelesaian/ }).waitFor();
        assert.deepEqual(await resolutionPanel.getByRole('combobox', { name: /^Pilihan penyelesaian/ }).locator('option').evaluateAll(options => options.map(option => option.value)), config.financialRequest ? ['replacement', 'credit'] : ['replacement']);
        if (config.financialRequest) await resolutionPanel.getByRole('combobox', { name: /^Pilihan penyelesaian/ }).selectOption('credit');
        await resolutionPanel.getByRole('button', { name: 'Periksa pilihan penyelesaian', exact: true }).click();
        const resolutionNote = config.financialRequest ? 'Pengajuan kredit satu barang yang telah diperiksa' : 'Pengajuan penggantian satu barang yang telah diperiksa';
        await resolutionPanel.getByLabel('Pengajuan penyelesaian untuk pelanggan', { exact: true }).fill(resolutionNote);
        await resolutionPanel.getByLabel('Catatan pengajuan penyelesaian internal', { exact: true }).fill('Pertimbangan penyelesaian privat');
        await resolutionPanel.getByRole('button', { name: 'Tinjau pengajuan penyelesaian', exact: true }).click(); const resolutionReview = resolutionPanel.getByRole('region', { name: 'Konfirmasi pengajuan penyelesaian', exact: true });
        await resolutionReview.waitFor(); assert.equal(await resolutionReview.getByRole('heading', { name: 'Ajukan penyelesaian untuk persetujuan', exact: true }).evaluate(element => element === document.activeElement), true);
        await resolutionReview.getByRole('button', { name: 'Kembali ke pengajuan penyelesaian', exact: true }).click(); assert.equal(resolutionWrites.length, 0);
        await resolutionPanel.getByRole('button', { name: 'Tinjau pengajuan penyelesaian', exact: true }).click(); assert.equal(await resolutionReview.getByRole('button', { name: 'Kirim pengajuan penyelesaian', exact: true }).isDisabled(), true); await resolutionReview.getByRole('checkbox').check();
        await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor(); assert.equal(await resolutionReview.getByRole('button', { name: 'Kirim pengajuan penyelesaian', exact: true }).isDisabled(), true); assert.equal(resolutionWrites.length, 0);
        await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        await page.screenshot({ path: resolve(directory, 'return-resolution-review-mobile.png'), fullPage: true }); dropNext = true; await resolutionReview.getByRole('button', { name: 'Kirim pengajuan penyelesaian', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
        const pendingResolution = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
        assert.equal(pendingResolution.length, 1); assert.equal(pendingResolution[0].operation, 'fulfillment.case.resolution_request'); assert.match(pendingResolution[0].key, /^cq\.[1-9][0-9]*\.[1-9][0-9]*\./); assert.deepEqual(Object.keys(pendingResolution[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pendingResolution).includes('privat'));
        await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
        assert.equal(resolutionWrites.length, 1); assert.equal(resolutionWrites[0].actor, config.financeId); assert.equal(dropped, 6); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
        await buyer.page.reload(); await buyer.page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click(); const resolvedReturns = buyer.page.getByRole('region', { name: 'Penerimaan retur', exact: true });
        await resolvedReturns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click(); await resolvedReturns.getByRole('button', { name: /^Lihat hasil pemeriksaan/ }).click(); await resolvedReturns.getByRole('button', { name: /^Lihat pengajuan penyelesaian/ }).click();
        await resolvedReturns.getByText(resolutionNote, { exact: true }).waitFor(); await resolvedReturns.getByText(/Menunggu persetujuan penyelesaian/).waitFor();
        const publicResolution = await buyer.page.locator('main').innerText(); for (const secret of ['Pertimbangan penyelesaian privat', 'Label produk terperiksa privat', 'LOT-PRIVAT']) assert.ok(!publicResolution.includes(secret)); assert.equal(await resolvedReturns.getByRole('button', { name: 'Periksa pilihan penyelesaian', exact: true }).count(), 0);
        assert.equal(await buyer.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await buyer.page.screenshot({ path: resolve(directory, 'return-resolution-buyer-mobile.png'), fullPage: true });
        console.log('REAL_RETURN_RESOLUTION_BROWSER_PASS ' + JSON.stringify({ requests: resolutionWrites.length, lostResolutionRecovered: true, privateNoteHidden: true, baseUnits: 1, action: config.financialRequest ? 'credit' : 'replacement', state: 'resolution_requested', noApprovalOrFinancialPosting: true, offlineUnsent: true, mobile: true }));
        if (config.financialRequest) {
          async function openFinancial() {
            await page.getByRole('button', { name: /^Buka keluhan \d+$/ }).click();
            const returns = page.getByRole('region', { name: 'Penerimaan retur', exact: true });
            await returns.getByRole('button', { name: 'Perbarui riwayat retur', exact: true }).click();
            await returns.getByRole('button', { name: /^Lihat hasil pemeriksaan/ }).click();
            await returns.getByRole('button', { name: /^Lihat pengajuan penyelesaian/ }).click();
            const panel = returns.getByRole('region', { name: /^Nilai finansial retur \d+$/ }); await panel.waitFor();
            await panel.getByRole('button', { name: /^Periksa nilai finansial retur \d+$/ }).click(); return panel;
          }
          const financial = await openFinancial();
          assert.equal(await financial.getByLabel(/^Pajak yang diajukan/).inputValue(), '0.00'); assert.equal(await financial.getByLabel(/^Biaya yang diajukan/).inputValue(), '0.00');
          await financial.getByLabel('Alasan pengajuan nilai finansial', { exact: true }).fill('Nilai satu barang retur terperiksa privat');
          await financial.getByRole('button', { name: 'Tinjau nilai finansial retur', exact: true }).click();
          const financialReview = financial.getByRole('region', { name: 'Konfirmasi pengajuan nilai retur', exact: true }); await financialReview.waitFor();
          assert.equal(await financialReview.getByRole('heading', { name: 'Ajukan nilai retur untuk pemeriksaan Finance', exact: true }).evaluate(element => element === document.activeElement), true);
          await financialReview.getByRole('button', { name: 'Kembali ke nilai retur', exact: true }).click(); assert.equal(entitlementWrites.length, 0);
          await financial.getByRole('button', { name: 'Tinjau nilai finansial retur', exact: true }).click();
          assert.equal(await financialReview.getByRole('button', { name: 'Kirim pengajuan nilai retur', exact: true }).isDisabled(), true); await financialReview.getByRole('checkbox').check();
          await finance.context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
          assert.equal(await financialReview.getByRole('button', { name: 'Kirim pengajuan nilai retur', exact: true }).isDisabled(), true); assert.equal(entitlementWrites.length, 0);
          await finance.context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'return-entitlement-review-mobile.png'), fullPage: true });
          dropNext = true; await financialReview.getByRole('button', { name: 'Kirim pengajuan nilai retur', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
          const pendingFinancial = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
          assert.equal(pendingFinancial.length, 1); assert.equal(pendingFinancial[0].operation, 'billing.return.entitlement.request'); assert.match(pendingFinancial[0].key, /^er\.[1-9][0-9]*\.[1-9][0-9]*\.credit\./);
          assert.deepEqual(Object.keys(pendingFinancial[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pendingFinancial).includes('privat'));
          await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden' });
          assert.equal(entitlementWrites.length, 1); assert.equal(entitlementWrites[0].actor, config.financeId); assert.equal(dropped, 7);
          assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
          const savedFinancial = await openFinancial(); await savedFinancial.getByRole('region', { name: 'Pengajuan nilai retur tercatat', exact: true }).waitFor();
          await savedFinancial.getByText(/Menunggu pemeriksaan finansial/).waitFor();
          assert.equal(await savedFinancial.getByRole('button', { name: 'Tinjau nilai finansial retur', exact: true }).count(), 0);
          assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'return-entitlement-saved-mobile.png'), fullPage: true });
          await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
          await page.screenshot({ path: resolve(directory, 'return-entitlement-saved-desktop.png'), fullPage: true });
          assert.equal(await resolvedReturns.getByRole('button', { name: /^Periksa nilai finansial retur/ }).count(), 0); assert.ok(!(await buyer.page.locator('main').innerText()).includes('Nilai satu barang retur terperiksa privat'));
          console.log('REAL_RETURN_ENTITLEMENT_BROWSER_PASS ' + JSON.stringify({ requests: entitlementWrites.length, lostOriginalRecovered: true, inspectedBaseUnits: 1, originalInvoiceMoney: true, noCreditOrRefundApproval: true, offlineUnsent: true, privateMetadataOnly: true, mobile: true, desktop: true }));
        }
      }
    }
    const caseOutsider = await user(config.courierToken); await caseOutsider.page.goto(origin.origin + '/fulfillment/cases'); await caseOutsider.page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
    assert.equal(await caseOutsider.page.getByRole('button', { name: /^Buka keluhan/ }).count(), 0);
    console.log('REAL_CASE_BROWSER_PASS ' + JSON.stringify({ requests: caseWrites.length, lostResponseRecovered: true, originalSaleLinked: true, requester: true, outsiderDenied: true, privateMetadataOnly: true, offlineUnsent: true, mobile: true }));
  }
  const outsider = await user(config.courierToken); await outsider.page.goto(origin.origin + '/billing/invoices'); await outsider.page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor();
  assert.equal(await outsider.page.getByRole('button', { name: /^Buka invoice/ }).count(), 0);
  assert.deepEqual(errors, []); console.log('REAL_INVOICE_BROWSER_PASS ' + JSON.stringify({ issued: writes.length, lostResponseRecovered: dropped, acceptedBaseUnits: 6, buyerDownload: true, outsiderDenied: true, offlineUnsent: true, mobile: true }));
  }
} catch (error) {
  for (const [index, page] of pages.entries()) if (!page.isClosed()) { console.error('FIXTURE PAGE ' + index + ': ' + (await page.locator('main').innerText().catch(() => 'unavailable')).slice(0, 5000)); await page.screenshot({ path: resolve(directory, 'failure-' + index + '.png'), fullPage: true }).catch(() => {}); }
  console.error(error.stack ?? String(error)); process.exitCode = 1;
} finally {
  if (config.procurementFunding) console.log('FIXTURE PROCUREMENT FUNDING FLOW ' + JSON.stringify({
    writes: fundingWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: fundingRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: fundingTracePath }));
  if (config.purchaseOrders) console.log('FIXTURE PURCHASE ORDER FLOW ' + JSON.stringify({
    writes: purchaseOrderWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: purchaseOrderRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: purchaseOrderTracePath }));
  if (config.suppliers) console.log('FIXTURE SUPPLIER FLOW ' + JSON.stringify({
    writes: supplierWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: supplierRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: supplierTracePath }));
  if (config.procurementRequests) console.log('FIXTURE PROCUREMENT REQUEST FLOW ' + JSON.stringify({
    writes: procurementWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: procurementRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: procurementTracePath }));
  if (config.accountingBooks) console.log('FIXTURE ACCOUNTING BOOK FLOW ' + JSON.stringify({
    writes: bookWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: bookRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: bookTracePath }));
  if (config.orderSpendingPolicy) console.log('FIXTURE ORDER SPENDING POLICY FLOW ' + JSON.stringify({
    writes: spendingWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: spendingRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: spendingTracePath }));
  if (config.organizationAddress) console.log('FIXTURE ORGANIZATION ADDRESS FLOW ' + JSON.stringify({
    writes: addressWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: addressRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: addressTracePath }));
  if (config.refundSettlement) console.log('FIXTURE REFUND SETTLEMENT FLOW ' + JSON.stringify({
    writes: settlementWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: settlementRecoveries.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })),
    originals: settlementOriginalReads.map(({ path, actor, status, elapsedMs }) => ({ path, actor, status, elapsedMs })), dropped, tracePath: settlementTracePath }));
  if (config.refundPayout) console.log('FIXTURE REFUND PAYOUT FLOW ' + JSON.stringify({
    drafts: draftWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    writes: payoutWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: payoutRecoveries.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })), dropped, tracePath: payoutTracePath }));
  if (config.financialApproval) console.log('FIXTURE FINANCIAL FLOW ' + JSON.stringify({
    writes: financialWrites.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })),
    recoveries: financialRecoveries.map(({ path, key, actor, status, elapsedMs }) => ({ path, key, actor, status, elapsedMs })), dropped }));
  traceFinancial('host-finally', { writes: financialWrites.length, recoveries: financialRecoveries.length, dropped });
  let timer;
  try { await Promise.race([(async () => { traceFinancial('cleanup-browser-begin', {}); if (browser) await browser.close(); traceFinancial('cleanup-browser-ended', {}); for (const socket of sockets) socket.destroy();
    for (const [name, server] of [['tunnel', tunnel], ['edge', edge], ['frontend', frontend]]) if (server) { traceFinancial('cleanup-server-begin', { name }); await new Promise(resolve => server.close(resolve)); traceFinancial('cleanup-server-ended', { name }); }
    traceFinancial('cleanup-next-begin', {}); await app.close(); traceFinancial('cleanup-next-ended', {}); })(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Fixture cleanup exceeded 10 seconds')), 10000); })]); }
  catch (failure) { console.error(failure); process.exitCode = 1; } finally { clearTimeout(timer); }
}
process.exit(process.exitCode ?? 0);
