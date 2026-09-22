// Run from website: node --env-file=../.tools/v7-qa.env scripts/smoke-customer.mjs
// This script mutates only the explicitly isolated local QA database. No secrets are logged.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { assertLocalQaEnvironment, prepareCustomerQaFixture, qaPassword, setQaCustomerRole, QA_CUSTOMER_ID, QA_CUSTOMER_EMAIL } from "./customer-qa-fixture.mjs";

const base = assertLocalQaEnvironment();
const sessions = new Map();
const checks = [], failures = [];
let fixture;
const A = "customer-demo", B = QA_CUSTOMER_ID;
const actors = [A, B, "pic-a", "pic-b", "kepala", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin"];
const email = id => id === A ? "customer@unit-toko.demo" : id === B ? QA_CUSTOMER_EMAIL : `${id}@unit-toko.demo`;
const total = values => values.reduce((sum, value) => sum + value, 0);
const invoiceTotal = (s, id) => total(s.invoiceLines.filter(l => l.invoiceId === id).map(l => l.subtotal + l.tax));
const invoiceBalance = (s, id) => invoiceTotal(s, id) - total(s.allocations.filter(l => l.invoiceId === id).map(l => l.amount)) - total(s.credits.filter(c => c.invoiceId === id && c.status === "approved").map(c => c.amount));
const onHand = (s, id) => total(s.batches.filter(b => b.productId === id).map(b => b.qty));
const available = (s, id) => onHand(s, id) - total(s.reservations.filter(r => s.batches.some(b => b.id === r.batchId && b.productId === id)).map(r => r.qty));
const safeMessage = error => String(error?.message || "Unexpected failure").replace(/toko_session=[^\s;]+/g, "[redacted session]").slice(0, 260);
async function check(name, fn) {
  try { await fn(); checks.push(name); console.log(`PASS ${name}`); }
  catch (error) { failures.push({ name, error: safeMessage(error) }); console.error(`FAIL ${name}: ${safeMessage(error)}`); }
}
async function request(who, path, options = {}) {
  const headers = { origin: base, ...options.headers };
  if (sessions.has(who)) headers.cookie = sessions.get(who);
  return fetch(base + path, { ...options, headers, signal: AbortSignal.timeout(60000) });
}
const jsonOptions = (data, method = "POST") => ({ method, headers: { "content-type": "application/json" }, body: JSON.stringify(data) });
async function expectStatus(response, status, label) {
  assert.ok((Array.isArray(status) ? status : [status]).includes(response.status), `${label}: HTTP ${response.status}, expected ${status}`);
  return response;
}
async function login(id, { key = id, password = qaPassword(id), suppliedEmail = email(id), extra = {} } = {}) {
  const response = await request(null, "/api/auth/login", jsonOptions({ email: suppliedEmail, password, ...extra }));
  await expectStatus(response, 200, `login ${id}`);
  const cookie = response.headers.get("set-cookie");
  assert.ok(cookie?.includes("HttpOnly") && cookie.includes("SameSite=Strict"), "session cookie protection");
  sessions.set(key, cookie.split(";")[0]);
  return (await response.json()).user;
}
async function state(id) {
  const response = await request(id, "/api/state"); await expectStatus(response, 200, `state ${id}`);
  return (await response.json()).state;
}
async function actionResponse(who, type, data, id = crypto.randomUUID()) {
  return request(who, "/api/commands", jsonOptions({ id, type, date: fixture.date, data }));
}
async function action(who, type, data, id = crypto.randomUUID()) {
  const response = await actionResponse(who, type, data, id); await expectStatus(response, 200, `${who} ${type}`); return response.json();
}
async function retry(who, type, data) {
  const id = crypto.randomUUID();
  const [first, second] = await Promise.all([action(who, type, data, id), action(who, type, data, id)]);
  assert.deepEqual(first, second, `same result for retried ${type}`); return { ...first, commandId: id };
}
function orderData(productId, qty = 1, extra = {}) {
  return { neededAt: fixture.date, recipientName: "Penerima QA", recipientPhone: "08000000001", address: "Alamat penerimaan QA", note: `QA ${fixture.run}`, lines: [{ productId, qty, unitPrice: 10000 }], ...extra };
}
const buyer = who => who === "pic-a" ? { divisionId: "div-ops" } : who === "pic-b" ? { divisionId: "div-ti" } : { divisionId: null, customerId: who };
async function fulfill(who, productId, qty, accepted = qty) {
  const order = await retry(who, "order.create", orderData(productId, qty));
  await action("kepala", "order.review", { id: order.id, approve: true });
  const line = (await state("staf")).orderLines.find(l => l.orderId === order.id);
  await action("staf", "stock.reserve", { orderLineId: line.id, qty });
  const shipment = await action("staf", "shipment.create", { orderId: order.id, courierId: "kurir", vehicle: "QA-V7", lines: [{ orderLineId: line.id, qty }] });
  await retry("kurir", "shipment.dispatch", { id: shipment.id });
  const shipmentLine = (await state("staf")).shipmentLines.find(l => l.shipmentId === shipment.id);
  await retry(who, "shipment.receive", { id: shipment.id, lines: [{ id: shipmentLine.id, accepted, reason: "Selisih penerimaan QA" }] });
  if (accepted < qty) {
    const complaint = (await state("staf")).complaints.find(c => c.shipmentId === shipment.id);
    await action("staf", "shipment.return", { shipmentLineId: shipmentLine.id, qty: qty - accepted, good: true, reason: "Barang layak kembali ke stok QA" });
    await action("staf", "complaint.resolve", { id: complaint.id, outcome: "returned", resolution: "Barang kembali, sisa dibatalkan" });
    await action(who, "order.cancel", { id: order.id, reason: "Tidak perlu pengiriman pengganti QA" });
  }
  await retry("staf", "sale.finalize", { id: shipment.id });
  const invoice = await retry("penagihan", "invoice.issue", { ...buyer(who), dueDate: fixture.date, lines: [{ shipmentLineId: shipmentLine.id }] });
  return { order, line, shipment, shipmentLine, invoice };
}
const png = () => readFileSync("public/images/products/water.png");
function uploadForm(scope, targetId, name = "bukti-QA.png") {
  const form = new FormData(); form.set("scope", scope); form.set("targetId", targetId); form.set("customerId", B); form.set("divisionId", "div-ti"); form.set("ownerId", B);
  form.set("file", new Blob([png()], { type: "image/png" }), name); return form;
}
async function attachment(who, scope, targetId) {
  const response = await request(who, "/api/attachments", { method: "POST", body: uploadForm(scope, targetId) });
  await expectStatus(response, 201, "upload own attachment"); return response.json();
}

async function main() {
  fixture = await prepareCustomerQaFixture();
  // A unique product created through the guarded local repository proves server DB identity.
  const response = await request(null, "/api/catalog"); await expectStatus(response, 200, "QA server catalog");
  const catalog = await response.json();
  assert.ok(catalog.products.some(p => p.id === fixture.products.full), "Server must read the same isolated QA database before HTTP mutations");
  await check("public catalog has an exact merchandise allowlist and no HPP/private graph", async () => {
    assert.deepEqual(Object.keys(catalog).sort(), ["products", "revision"]);
    const allowed = ["id", "familyId", "sku", "name", "unit", "price", "active", "category", "image", "description", "packaging", "available"].sort();
    for (const p of catalog.products) { assert.deepEqual(Object.keys(p).sort(), allowed); assert.equal(p.active, true); assert.ok(Number.isFinite(p.available) && p.available >= 0); }
    assert.ok(!/"(?:cost|supplierId|customerId|divisionId|email|journalLines|password|hash|salt)"/.test(JSON.stringify(catalog)));
  });
  await check("unauthenticated protected endpoints reject access", async () => {
    for (const [path, options] of [["/api/state", {}], ["/api/commands", jsonOptions({})], ["/api/profile", jsonOptions({}, "PATCH")], ["/api/profile", jsonOptions({})], ["/api/profile/avatar", {}], ["/api/profile/avatar", { method: "POST" }], ["/api/attachments", { method: "POST" }], ["/api/attachments/known-qa-id", {}], ["/api/documents/invoice/known-qa-id", {}], ["/api/reports", {}]]) await expectStatus(await request(null, path, options), 401, path);
  });
  for (const id of actors) await login(id);
  await check("case-insensitive explicit email works and submitted role is ignored", async () => {
    const actor = await login(A, { suppliedEmail: "  CUSTOMER@UNIT-TOKO.DEMO  ", extra: { role: "admin", divisionId: "div-ops", id: "admin" } });
    assert.equal(actor.id, A); assert.equal(actor.role, "customer"); assert.equal(actor.divisionId, null);
  });
  await check("wrong and unknown email share generic failure; repeated unknown login is throttled", async () => {
    const wrong = await request(null, "/api/auth/login", jsonOptions({ email: email(A), password: "not-a-valid-qa-password" }));
    const missingEmail = `missing-${fixture.run}@unit-toko.demo`;
    const missing = await request(null, "/api/auth/login", jsonOptions({ email: missingEmail, password: "not-a-valid-qa-password" }));
    await expectStatus(wrong, 401, "wrong login"); await expectStatus(missing, 401, "unknown login"); assert.deepEqual(await wrong.json(), await missing.json());
    for (let i = 0; i < 7; i++) await expectStatus(await request(null, "/api/auth/login", jsonOptions({ email: missingEmail, password: "not-a-valid-qa-password" })), 401, "rate limit pre-threshold");
    await expectStatus(await request(null, "/api/auth/login", jsonOptions({ email: missingEmail, password: "not-a-valid-qa-password" })), 429, "rate limit threshold");
  });
  await check("profile ownership injection denied; address persists", async () => {
    await expectStatus(await request(A, "/api/profile", jsonOptions({ name: "QA", role: "admin" }, "PATCH")), 403, "profile role injection");
    await expectStatus(await request(A, "/api/profile", jsonOptions({ name: "Pelanggan QA Pertama", phone: "08000000001", position: "", address: "  Alamat awal QA  " }, "PATCH")), 200, "profile update");
    assert.equal((await state(A)).users[0].address, "Alamat awal QA");
  });
  await check("customer cannot record payment before an invoice exists", async () => {
    await expectStatus(await actionResponse(A, "payment.record", { amount: 30000 }), 400, "pre-invoice payment");
  });

  let full, partial, second, pic, payment, evidence;
  await check("full purchase preserves owner, snapshot, stock, invoice and idempotent finance", async () => {
    const createData = orderData(fixture.products.full, 3, { customerId: B, divisionId: "div-ti", address: "Alamat awal QA", recipientName: "Penerima awal QA" });
    const order = await retry(A, "order.create", createData);
    let s = await state(A); const stored = s.orders.find(o => o.id === order.id);
    assert.equal(stored.customerId, A); assert.equal(stored.divisionId, null); assert.equal(stored.channel, "customer"); assert.ok(stored.createdAt); assert.equal(stored.createdDate, fixture.date);
    await expectStatus(await actionResponse(A, "order.create", { ...createData, note: "different payload" }, order.commandId), 409, "idempotency mismatched payload");
    await expectStatus(await actionResponse(B, "order.create", createData, order.commandId), 409, "idempotency cross actor");
    await request(A, "/api/profile", jsonOptions({ name: "Nama profil baru QA", phone: "08000000009", position: "", address: "Alamat baru QA" }, "PATCH"));
    s = await state(A); const snapshot = s.orders.find(o => o.id === order.id);
    assert.equal(snapshot.address, "Alamat awal QA"); assert.equal(snapshot.recipientName, "Penerima awal QA"); assert.equal(snapshot.recipientPhone, "08000000001"); assert.equal(snapshot.buyerName, "Pelanggan QA Pertama");
    assert.equal(s.orders.filter(o => o.id === order.id).length, 1);
    await action("kepala", "order.review", { id: order.id, approve: true });
    const line = (await state("staf")).orderLines.find(l => l.orderId === order.id);
    await retry("staf", "stock.reserve", { orderLineId: line.id, qty: 3 });
    s = await state("staf"); assert.equal(onHand(s, fixture.products.full), 10); assert.equal(available(s, fixture.products.full), 7);
    const shipment = await action("staf", "shipment.create", { orderId: order.id, courierId: "kurir", vehicle: "QA-V7", lines: [{ orderLineId: line.id, qty: 3 }] });
    await retry("kurir", "shipment.dispatch", { id: shipment.id });
    s = await state("staf"); assert.equal(onHand(s, fixture.products.full), 7); assert.equal(total(s.movements.filter(m => m.sourceId === shipment.id && m.type === "dispatch").map(m => m.qty)), -3);
    const shipmentLine = s.shipmentLines.find(l => l.shipmentId === shipment.id);
    await retry(A, "shipment.receive", { id: shipment.id, lines: [{ id: shipmentLine.id, accepted: 3 }] });
    await retry("staf", "sale.finalize", { id: shipment.id });
    const invoice = await retry("penagihan", "invoice.issue", { ...buyer(A), dueDate: fixture.date, lines: [{ shipmentLineId: shipmentLine.id }] });
    s = await state(A); assert.equal(invoiceBalance(s, invoice.id), 30000);
    payment = await retry(A, "payment.record", { invoiceId: invoice.id, customerId: B, divisionId: "div-ti", amount: 30000, status: "verified", verifiedBy: "penagihan", reference: `QA-${fixture.run}` });
    s = await state(A); const recorded = s.payments.find(p => p.id === payment.id); assert.equal(recorded.status, "recorded"); assert.equal(recorded.customerId, A); assert.equal(recorded.divisionId, null); assert.equal(invoiceBalance(s, invoice.id), 30000);
    evidence = await attachment(A, "payment", payment.id);
    await retry("penagihan", "payment.verify", { id: payment.id });
    await retry("penagihan", "payment.allocate", { paymentId: payment.id, lines: [{ invoiceId: invoice.id, amount: 30000 }] });
    s = await state(A); assert.equal(invoiceBalance(s, invoice.id), 0); assert.equal(s.allocations.filter(a => a.invoiceId === invoice.id).length, 1);
    const meta = s.attachments.find(a => a.id === evidence.id); assert.equal(meta.customerId, A); assert.equal(meta.divisionId, null); assert.equal(meta.ownerId, A);
    await expectStatus(await actionResponse(A, "payment.record", { invoiceId: invoice.id, amount: 1 }), 409, "paid invoice duplicate payment");
    full = { order, line, shipment, shipmentLine, invoice };
  });
  await check("attachment retries and simultaneous renamed uploads preserve one proof per owner and target", async () => {
    assert.ok(full && evidence && payment, "earlier complete flow required");
    const repeated = await attachment(A, "payment", payment.id);
    assert.equal(repeated.id, evidence.id, "retry after saved response returns original proof");
    const responses = await Promise.all(["bukti-awal.png", "bukti-diganti-nama.png", "bukti-tab-kedua.png"].map(name =>
      request(A, "/api/attachments", { method: "POST", body: uploadForm("receipt", full.shipment.id, name) })));
    const uploaded = [];
    for (const response of responses) { await expectStatus(response, 201, "concurrent proof upload"); uploaded.push(await response.json()); }
    assert.equal(new Set(uploaded.map(result => result.id)).size, 1, "simultaneous uploads share one attachment");
    assert.notEqual(uploaded[0].id, evidence.id, "same bytes for a different target remain independent");
    const s = await state(A);
    assert.equal(s.attachments.filter(a => a.scope === "payment" && a.targetId === payment.id && a.ownerId === A).length, 1);
    assert.equal(s.attachments.filter(a => a.scope === "receipt" && a.targetId === full.shipment.id && a.ownerId === A).length, 1);
    const file = await request(A, `/api/attachments/${uploaded[0].id}`);
    await expectStatus(file, 200, "concurrent upload leaves readable blob");
    assert.deepEqual(Buffer.from(await file.arrayBuffer()), png(), "saved proof bytes stay intact");
    await expectStatus(await request(B, "/api/attachments", { method: "POST", body: uploadForm("receipt", full.shipment.id) }), 403, "dedup cannot bypass ownership");
  });
  await check("partial return bills accepted units only and refunds excess once", async () => {
    partial = await fulfill(A, fixture.products.partial, 3, 2);
    let s = await state("penagihan"); assert.equal(onHand(s, fixture.products.partial), 8); assert.equal(invoiceTotal(s, partial.invoice.id), 20000);
    const returned = s.shipmentLines.find(l => l.id === partial.shipmentLine.id); assert.equal(returned.returned, 1); assert.equal(returned.accepted, 2);
    assert.equal(s.orderLines.find(l => l.id === partial.line.id).cancelled, 1);
    const overpay = await action(A, "payment.record", { invoiceId: partial.invoice.id, amount: 25000 });
    await action("penagihan", "payment.verify", { id: overpay.id });
    await action("penagihan", "payment.allocate", { paymentId: overpay.id, lines: [{ invoiceId: partial.invoice.id, amount: 20000 }] });
    const refund = await action("penagihan", "refund.request", { paymentId: overpay.id, amount: 5000, reason: "Pengembalian kelebihan simulasi QA" });
    await action("pimpinan", "refund.approve", { id: refund.id }); await retry("penagihan", "refund.pay", { id: refund.id });
    s = await state(A); assert.equal(invoiceBalance(s, partial.invoice.id), 0); assert.equal(s.refunds.find(r => r.id === refund.id).status, "paid");
    assert.ok(!(await state(B)).refunds.some(r => r.id === refund.id));
  });
  await check("known customer B and PIC transactions are created through operational API", async () => {
    second = await fulfill(B, fixture.products.isolation, 1);
    pic = await fulfill("pic-a", fixture.products.isolation, 1);
    assert.equal(invoiceTotal(await state(B), second.invoice.id), 10000); assert.equal(invoiceTotal(await state("pic-a"), pic.invoice.id), 10000);
  });
  await check("known cross-buyer documents, attachments and direct commands are denied", async () => {
    assert.ok(full && second && pic && evidence, "earlier complete flow required");
    for (const [who, foreign] of [[A, second], [A, pic], [B, full], ["pic-a", full]]) {
      await expectStatus(await request(who, `/api/documents/invoice/${foreign.invoice.id}`), 404, "foreign invoice");
      await expectStatus(await request(who, `/api/documents/shipment/${foreign.shipment.id}`), 404, "foreign shipment");
      await expectStatus(await actionResponse(who, "order.cancel", { id: foreign.order.id, reason: "QA prohibited" }), 403, "foreign cancel");
      await expectStatus(await actionResponse(who, "shipment.receive", { id: foreign.shipment.id, lines: [{ id: foreign.shipmentLine.id, accepted: 1 }] }), 403, "foreign receipt");
      await expectStatus(await actionResponse(who, "complaint.create", { shipmentLineId: foreign.shipmentLine.id, qty: 1, reason: "QA prohibited" }), 403, "foreign complaint");
      await expectStatus(await actionResponse(who, "payment.record", { invoiceId: foreign.invoice.id, amount: 1 }), 403, "foreign payment");
      await expectStatus(await request(who, "/api/attachments", { method: "POST", body: uploadForm("receipt", foreign.shipment.id) }), 403, "foreign receipt attachment");
    }
    for (const id of [B, "pic-a", "pic-b", "staf", "kepala", "laporan", "kurir", "admin"]) await expectStatus(await request(id, `/api/attachments/${evidence.id}`), 404, `payment attachment privacy ${id}`);
    for (const id of [A, "penagihan", "pimpinan", "akuntansi"]) await expectStatus(await request(id, `/api/attachments/${evidence.id}`), 200, `payment attachment allowed ${id}`);
    await expectStatus(await request(B, "/api/attachments", { method: "POST", body: uploadForm("payment", payment.id) }), 403, "foreign payment attachment upload");
    const foreignPayment = await action(B, "payment.record", { invoiceId: second.invoice.id, amount: 10000 });
    await action("penagihan", "payment.verify", { id: foreignPayment.id });
    await expectStatus(await actionResponse("penagihan", "payment.allocate", { paymentId: foreignPayment.id, lines: [{ invoiceId: pic.invoice.id, amount: 10000 }] }), 400, "customer payment to PIC invoice");
    await expectStatus(await actionResponse("penagihan", "payment.allocate", { paymentId: foreignPayment.id, lines: [{ invoiceId: full.invoice.id, amount: 1 }] }), 400, "customer payment to another customer");
  });
  await check("customer scoped graph excludes other buyers, HPP and internal account directory", async () => {
    const s = await state(A);
    for (const key of ["suppliers", "purchases", "purchaseLines", "supplierPayments", "journals", "journalLines", "periods", "stocktakes", "stockReturns", "movements", "audits", "expenses", "divisions"]) assert.equal(s[key].length, 0, key);
    assert.equal(s.users.length, 1); assert.equal(s.users[0].id, A);
    for (const key of ["orders", "invoices", "payments", "complaints", "attachments"]) assert.ok(s[key].every(v => v.customerId === A && v.divisionId === null), key);
    assert.ok(s.shipmentLines.every(l => l.cost === 0)); assert.ok(s.batches.every(b => b.cost === 0 && !b.supplierId && !b.purchaseLineId && !b.expiry && !b.location));
    const orderIds = new Set(s.orders.map(o => o.id)), shipmentIds = new Set(s.shipments.map(sh => sh.id));
    assert.ok(s.orderLines.every(l => orderIds.has(l.orderId))); assert.ok(s.shipments.every(sh => orderIds.has(sh.orderId))); assert.ok(s.shipmentLines.every(l => shipmentIds.has(l.shipmentId)));
    for (const who of ["staf", "penagihan", "kepala"]) assert.ok((await state(who)).users.every(u => u.id === who || u.role === "kurir"), "internal user directory minimized");
    const staff = await state("staf"), admin = await state("admin");
    for (const key of ["invoices", "invoiceLines", "payments", "allocations", "journals", "journalLines"]) assert.equal(staff[key].length, 0, `staff ${key}`);
    for (const key of ["orders", "orderLines", "invoices", "payments", "journals", "attachments"]) assert.equal(admin[key].length, 0, `admin ${key}`);
  });
  await check("every role obeys invoice/report endpoints and admin command boundaries", async () => {
    assert.ok(full, "own invoice required");
    const finance = ["kepala", "laporan", "penagihan", "pimpinan", "akuntansi"];
    for (const id of actors) {
      await expectStatus(await request(id, "/api/state"), 200, `state ${id}`);
      await expectStatus(await request(id, "/api/reports"), finance.includes(id) ? 200 : 403, `reports ${id}`);
      const invoiceStatus = ["staf", "kurir", "admin"].includes(id) ? 403 : finance.includes(id) || id === A ? 200 : 404;
      await expectStatus(await request(id, `/api/documents/invoice/${full.invoice.id}`), invoiceStatus, `invoice ${id}`);
      if (id !== "admin") await expectStatus(await actionResponse(id, "admin.user", { id: B, role: "customer", active: true }), 403, `admin boundary ${id}`);
    }
    for (const [type, data] of [["order.review", { id: full.order.id, approve: true }], ["stock.reserve", { orderLineId: full.line.id, qty: 1 }], ["shipment.dispatch", { id: full.shipment.id }], ["sale.finalize", { id: full.shipment.id }], ["invoice.issue", {}], ["payment.verify", { id: payment.id }], ["payment.allocate", {}], ["purchase.create", {}], ["period.close", {}]]) await expectStatus(await actionResponse(A, type, data), 403, `customer ${type}`);
    await expectStatus(await actionResponse("admin", "admin.user", { id: A, role: "staf", active: true }), 403, "customer role conversion");
    await expectStatus(await actionResponse("admin", "admin.user", { id: "staf", role: "customer", active: true }), 403, "staff role conversion");
  });
  await check("changed price and stock produce atomic 409; corrected checkout succeeds", async () => {
    let before = await state(A); const count = before.orders.length;
    await expectStatus(await actionResponse(A, "order.create", orderData(fixture.products.isolation, 1, { lines: [{ productId: fixture.products.isolation, qty: 1, unitPrice: 1 }] })), 409, "forged unit price");
    await action("kepala", "product.update", { id: fixture.products.isolation, price: 12000, minimum: 0, returnMonths: 1 });
    const retryId = crypto.randomUUID();
    await expectStatus(await actionResponse(A, "order.create", orderData(fixture.products.isolation), retryId), 409, "changed price");
    assert.equal((await state(A)).orders.length, count);
    const corrected = await action(A, "order.create", orderData(fixture.products.isolation, 1, { lines: [{ productId: fixture.products.isolation, qty: 1, unitPrice: 12000 }] }), retryId);
    assert.equal((await state(A)).orderLines.find(l => l.orderId === corrected.id).price, 12000);
    await action("kepala", "product.update", { id: fixture.products.isolation, price: 10000, minimum: 0, returnMonths: 1 });
    const remaining = (await (await request(null, "/api/catalog")).json()).products.find(p => p.id === fixture.products.isolation).available;
    const blocker = await action(B, "order.create", orderData(fixture.products.isolation, remaining));
    await action("kepala", "order.review", { id: blocker.id, approve: true });
    const line = (await state("staf")).orderLines.find(l => l.orderId === blocker.id);
    await action("staf", "stock.reserve", { orderLineId: line.id, qty: remaining });
    before = await state(A);
    await expectStatus(await actionResponse(A, "order.create", orderData(fixture.products.isolation)), 409, "changed availability");
    assert.equal((await state(A)).orders.length, before.orders.length);
    await action(B, "order.cancel", { id: blocker.id, reason: "Selesai uji stok QA" });
  });
  await check("customer cannot backdate checkout; server keeps actual timestamp and open date", async () => {
    const response = await request(A, "/api/commands", jsonOptions({ id: crypto.randomUUID(), type: "order.create", date: "1900-01-01", data: orderData(fixture.products.full, 1, { neededAt: "1900-01-01" }) }));
    await expectStatus(response, 200, "customer date override");
    const result = await response.json(), order = (await state(A)).orders.find(o => o.id === result.id);
    assert.equal(order.createdDate, fixture.date); assert.equal(order.neededAt, fixture.date);
    assert.ok(Math.abs(Date.now() - Date.parse(order.createdAt)) < 120000, "actual checkout timestamp");
  });
  await check("credit on paid customer invoice becomes only that customer's credit balance", async () => {
    assert.ok(full, "paid invoice required");
    const credit = await action("penagihan", "credit.request", { invoiceId: full.invoice.id, amount: 1000, reason: "Koreksi simulasi QA pada invoice lunas" });
    await retry("pimpinan", "credit.approve", { id: credit.id });
    const own = await state(A), balance = own.payments.find(p => p.sourceCreditId === credit.id);
    assert.ok(balance); assert.equal(balance.customerId, A); assert.equal(balance.divisionId, null); assert.equal(balance.amount, 1000); assert.equal(balance.kind, "credit");
    assert.ok(!(await state(B)).payments.some(p => p.id === balance.id)); assert.ok(!(await state("pic-a")).credits.some(c => c.id === credit.id));
    // invoiceBalance helper deliberately exposes an over-credit if present; the
    // approved credit consumes paid value and records a separate buyer balance.
    assert.equal(total(own.allocations.filter(a => a.invoiceId === full.invoice.id).map(a => a.amount)), 30000);
  });
  await check("cross-origin writes are denied", async () => {
    await expectStatus(await request(A, "/api/commands", { ...jsonOptions({ id: crypto.randomUUID(), type: "order.create", data: {} }), headers: { "content-type": "application/json", origin: "https://example.invalid" } }), 403, "CSRF command");
    await expectStatus(await request(A, "/api/auth/logout", { method: "POST", headers: { origin: "https://example.invalid" } }), 403, "CSRF logout");
  });
  await check("unknown role loses all protected endpoint access immediately", async () => {
    await setQaCustomerRole("unknown-qa-role");
    try {
      for (const [path, options] of [["/api/state", {}], ["/api/commands", jsonOptions({})], ["/api/profile", jsonOptions({ name: "QA" }, "PATCH")], ["/api/profile", jsonOptions({})], ["/api/profile/avatar", {}], ["/api/profile/avatar", { method: "POST" }], ["/api/attachments", { method: "POST" }], [`/api/attachments/${evidence?.id || "missing"}`, {}], [`/api/documents/invoice/${second?.invoice.id || "missing"}`, {}], ["/api/reports", {}]]) await expectStatus(await request(B, path, options), 403, `unknown role ${path}`);
    } finally { await setQaCustomerRole("customer"); }
  });
  await check("unknown role cannot obtain a new authenticated session", async () => {
    await setQaCustomerRole("unknown-qa-role");
    try { await expectStatus(await request(null, "/api/auth/login", jsonOptions({ email: email(B), password: qaPassword(B) })), [401, 403], "unknown role login"); }
    finally { await setQaCustomerRole("customer"); }
  });
  await check("own avatar upload is private and served with protected headers", async () => {
    const form = new FormData(); form.set("file", new Blob([png()], { type: "image/png" }), "foto-QA.png");
    await expectStatus(await request(A, "/api/profile/avatar", { method: "POST", body: form }), 200, "own avatar upload");
    const avatar = await request(A, "/api/profile/avatar"); await expectStatus(avatar, 200, "own avatar read");
    assert.equal(avatar.headers.get("cache-control"), "private, no-store"); assert.equal(avatar.headers.get("x-content-type-options"), "nosniff");
    const ownUrl = (await state(A)).users[0].avatar;
    await expectStatus(await request(B, ownUrl), 404, "foreign avatar query cannot select another user");
  });
  await check("password change revokes two sessions and original password is restored", async () => {
    const original = qaPassword(A), temporary = `QA-only-${crypto.randomUUID()}`;
    await login(A, { key: "session-two" });
    let changed = false;
    try {
      await expectStatus(await request(A, "/api/profile", jsonOptions({ currentPassword: original, newPassword: temporary })), 200, "change password"); changed = true;
      await expectStatus(await request(A, "/api/state"), 401, "first old session revoked"); await expectStatus(await request("session-two", "/api/state"), 401, "second old session revoked");
      await login(A, { password: temporary });
    } finally {
      if (changed) { await login(A, { password: temporary }); await expectStatus(await request(A, "/api/profile", jsonOptions({ currentPassword: temporary, newPassword: original })), 200, "restore original QA password"); }
    }
    await login(A);
  });
  await check("logout revokes session; a new login retains historical order data", async () => {
    await expectStatus(await request(A, "/api/auth/logout", { method: "POST" }), 200, "logout");
    await expectStatus(await request(A, "/api/state"), 401, "logout session revoked");
    await login(A); assert.ok((await state(A)).orders.some(o => o.id === full?.order.id));
  });
  console.log(JSON.stringify({ passed: failures.length === 0, checks: checks.length, failures, reconciliation: { fullInvoice: full ? 30000 : null, fullBalance: full ? Math.max(0, invoiceBalance(await state(A), full.invoice.id)) : null, partialInvoice: partial ? 20000 : null, partialReturnedUnits: partial ? 1 : null }, review: { fullOrderId: full?.order.id, fullInvoiceId: full?.invoice.id, partialOrderId: partial?.order.id, partialInvoiceId: partial?.invoice.id }, fixtureRun: fixture.run }, null, 2));
  if (failures.length) process.exitCode = 1;
}

// The local libSQL driver keeps a worker alive; all writes above have completed
// before explicitly ending this one-shot QA process.
main().then(() => process.exit(process.exitCode || 0)).catch(error => { console.error(`QA stopped: ${safeMessage(error)}`); process.exit(1); });
