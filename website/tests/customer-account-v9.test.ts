import {test} from "node:test";
import assert from "node:assert/strict";
import {customerPaymentLabel, customerProductHref, customerSubstitutionViews} from "../lib/domain/customer-order-views";
import {runCommand} from "../lib/domain/engine";
import {seedState} from "../lib/domain/seed";
import {scopeState, today} from "../lib/domain/selectors";
import type {Payment} from "../lib/domain/model";

function fixture() {
  let state = seedState();
  return {
    get s() {return state;},
    actor(id: string) {return state.users.find(user => user.id === id)!;},
    act(id: string, type: string, data: Record<string, unknown>) {
      const outcome = runCommand(state, state.users.find(user => user.id === id)!, {id: crypto.randomUUID(), type, date: today(), data});
      state = outcome.state;
      return outcome.result.id;
    },
  };
}

function propose(f: ReturnType<typeof fixture>, productId = "kopi--3", replacementId = "teh--6") {
  const product = f.s.products.find(item => item.id === productId)!;
  const orderId = f.act("customer-demo", "order.create", {recipientName: "Penerima", recipientPhone: "0800000000", address: "Alamat pengiriman", lines: [{productId, qty: 2, unitPrice: product.price}]});
  f.act("kepala", "order.review", {id: orderId, approve: true});
  const line = f.s.orderLines.find(item => item.orderId === orderId)!;
  f.act("staf", "substitution.propose", {orderLineId: line.id, productId: replacementId, reason: "Pilihan kemasan tersedia"});
  return {orderId, substitutionId: f.s.substitutions.at(-1)!.id};
}

test("v9 purchased P3/P6 product links select the exact route SKU and price", () => {
  const s = seedState();
  for (const pack of [3, 6]) {
    const purchased = s.products.find(product => product.sku === `OMI-003-P${pack}`)!;
    const url = new URL(customerProductHref(purchased), "https://unit-toko.invalid");
    const selectedId = decodeURIComponent(url.pathname.split("/").at(-1)!);
    const detailProduct = s.products.find(product => product.id === selectedId || product.sku === selectedId);
    assert.equal(detailProduct?.id, purchased.id);
    assert.equal(detailProduct?.sku, `OMI-003-P${pack}`);
    assert.equal(detailProduct?.price, 28500 * pack);
    assert.equal(url.search, "", "selection does not depend on an ignored sku query");
  }
});

test("v9 customer substitution review exposes original item, frozen quantity, and both totals after approval", () => {
  const f = fixture(), {orderId, substitutionId} = propose(f);
  let view = customerSubstitutionViews(scopeState(f.s, f.actor("customer-demo")), orderId)[0];
  assert.equal(view.originalProduct?.sku, "OMI-003-P3");
  assert.equal(view.replacementProduct?.sku, "OMI-002-P6");
  assert.equal(view.quantity, 2);
  assert.equal(view.originalTotal, 171000);
  assert.equal(view.total, 144000);
  assert.equal(view.needsDecision, true);
  f.act("customer-demo", "substitution.decide", {id: substitutionId, approve: true});
  f.act("kepala", "product.update", {id: "teh--6", price: 99000, minimum: 5, returnMonths: 1});
  view = customerSubstitutionViews(scopeState(f.s, f.actor("customer-demo")), orderId)[0];
  assert.equal(view.status, "Disetujui");
  assert.equal(view.needsDecision, false);
  assert.equal(view.quantity, 2, "original line was cancelled, but proposal quantity stays visible");
  assert.equal(view.total, 144000, "the decision uses proposed price, not the latest catalogue price");
});

test("v9 rejected and cancelled customer substitutions remain in history without decision actions", () => {
  const f = fixture(), {orderId, substitutionId} = propose(f);
  f.act("customer-demo", "substitution.decide", {id: substitutionId, approve: false});
  let view = customerSubstitutionViews(scopeState(f.s, f.actor("customer-demo")), orderId)[0];
  assert.equal(view.status, "Ditolak");
  assert.equal(view.needsDecision, false);
  const next = propose(f, "air--3", "air--6");
  f.act("customer-demo", "order.cancel", {id: next.orderId, reason: "Kebutuhan dibatalkan"});
  view = customerSubstitutionViews(scopeState(f.s, f.actor("customer-demo")), next.orderId)[0];
  assert.equal(view.status, "Tidak berlaku");
  assert.equal(view.needsDecision, false);
  assert.match(view.substitution.cancelledReason!, /Kebutuhan dibatalkan/);
});

test("v9 customer keeps inactive proposed SKU history but never sees another buyer's proposal or inactive SKU", () => {
  const f = fixture(), {orderId} = propose(f);
  const foreignOrder = f.act("pic-a", "order.create", {neededAt: today(), lines: [{productId: "tas--3", qty: 1}]});
  f.act("kepala", "order.review", {id: foreignOrder, approve: true});
  f.act("staf", "substitution.propose", {orderLineId: f.s.orderLines.find(line => line.orderId === foreignOrder)!.id, productId: "tumbler--6", reason: "Usulan divisi privat"});
  f.s.products.find(product => product.id === "teh--6")!.active = false;
  f.s.products.find(product => product.id === "tumbler--6")!.active = false;
  const scoped = scopeState(f.s, f.actor("customer-demo"));
  assert.ok(scoped.products.some(product => product.id === "teh--6"));
  assert.ok(!scoped.products.some(product => product.id === "tumbler--6"));
  assert.equal(scoped.substitutions.length, 1);
  assert.equal(customerSubstitutionViews(scoped, orderId)[0].replacementProduct?.sku, "OMI-002-P6");
  assert.equal(customerSubstitutionViews(scoped, foreignOrder).length, 0);
});

test("v9 payment labels distinguish rejected proof from verified and allocated funds", () => {
  const s = seedState();
  const payment: Payment = {id: "new-payment", customerId: "customer-demo", divisionId: null, amount: 10000, date: today(), kind: "transfer", reference: "QA", payer: "Pelanggan", note: "", status: "recorded", verifiedBy: null, evidence: "", createdBy: "customer-demo"};
  assert.equal(customerPaymentLabel(s, payment), "Menunggu verifikasi");
  payment.status = "rejected";
  assert.equal(customerPaymentLabel(s, payment), "Ditolak");
  payment.status = "verified";
  assert.equal(customerPaymentLabel(s, payment), "Terverifikasi");
  s.allocations.push({id: "allocation", paymentId: payment.id, invoiceId: "invoice", amount: 1000, date: today()});
  assert.equal(customerPaymentLabel(s, payment), "Dialokasikan");
});

test("v9 administrator editing divisions and suppliers retains linked history and RBAC", () => {
  const f = fixture();
  const order = structuredClone(f.s.orders.find(item => item.divisionId === "div-ops")!);
  const purchaseId = f.act("staf", "purchase.create", {kind: "DDO", supplierId: "sup-omi", lines: [{productId: "kopi", qty: 2, cost: 23000}]});
  const count = f.s.divisions.length, supplierCount = f.s.suppliers.length;
  f.act("admin", "admin.division", {id: "div-ops", name: "Operasional baru", code: "OPS-BARU", address: "Alamat baru"});
  f.act("admin", "admin.supplier", {id: "sup-omi", name: "Nama pemasok baru", category: "OMI"});
  assert.equal(f.s.divisions.length, count);
  assert.equal(f.s.suppliers.length, supplierCount);
  assert.deepEqual(f.s.orders.find(item => item.id === order.id), order, "existing destination snapshot is unchanged");
  assert.equal(f.s.purchases.find(item => item.id === purchaseId)?.supplierId, "sup-omi");
  for (const user of f.s.users.filter(user => user.role !== "admin")) {
    assert.throws(() => f.act(user.id, "admin.division", {id: "div-ops", name: "Unauthorized", code: "OPS-BARU", address: "No"}), /akses/);
    assert.throws(() => f.act(user.id, "admin.supplier", {id: "sup-omi", name: "Unauthorized", category: "No"}), /akses/);
  }
  assert.throws(() => f.act("admin", "admin.division", {id: "div-ops", name: "Duplicate", code: "TI", address: "No"}), /sudah digunakan/);
});
