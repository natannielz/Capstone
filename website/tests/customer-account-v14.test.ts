import {test} from "node:test";
import assert from "node:assert/strict";
import {customerOrderActions, customerOrderSectionId, safeCustomerOrderListHref} from "../lib/domain/customer-order-views";
import {emptyState} from "../lib/domain/seed";
import type {Payment} from "../lib/domain/model";

function fixture() {
  const s = emptyState();
  s.products.push({id: "item", name: "Kopi", sku: "KOPI", category: "OMI", unit: "pak", price: 10000, minimum: 0, returnMonths: 0, active: true});
  s.orders.push({id: "order", number: "PO-001", divisionId: null, customerId: "customer", createdBy: "customer", createdAt: "2026-09-20T08:00:00Z", neededAt: "2026-09-20", address: "Alamat", note: "", status: "approved", origin: "Etalase pelanggan"});
  s.orderLines.push({id: "line", orderId: "order", productId: "item", requestedProductId: "item", qty: 2, cancelled: 0, price: 10000, note: ""});
  s.shipments.push({id: "shipment", number: "SJ-001", orderId: "order", courierId: "courier", vehicle: "Motor", date: "2026-09-20", status: "received", receiver: "Penerima", receivedAt: "2026-09-20T10:00:00Z", receivedDate: "2026-09-20", evidence: "", note: ""});
  s.shipmentLines.push({id: "shipment-line", shipmentId: "shipment", orderLineId: "line", batchId: "batch", qty: 2, accepted: 2, returned: 0, returnGood: 0, finalized: true, price: 10000, cost: 6000});
  s.invoices.push({id: "invoice", number: "INV-001", divisionId: null, customerId: "customer", date: "2026-09-20", dueDate: "2026-09-27", status: "issued", taxBps: 0, taxNote: ""});
  s.invoiceLines.push({id: "invoice-line", invoiceId: "invoice", shipmentLineId: "shipment-line", qty: 2, price: 10000, subtotal: 20000, tax: 0});
  return s;
}

function payment(): Payment {
  return {id: "payment", invoiceId: "invoice", divisionId: null, customerId: "customer", amount: 20000, date: "2026-09-20", kind: "transfer", reference: "SIM-01", payer: "Penerima", note: "", status: "recorded", verifiedBy: null, evidence: "", createdBy: "customer"};
}

test("v14 payment actions distinguish missing proof, store verification, rejection, allocation and settlement", () => {
  const s = fixture();
  assert.deepEqual(customerOrderActions(s, "order").map(action => action.kind), ["payment"]);
  s.payments.push(payment());
  assert.deepEqual(customerOrderActions(s, "order").map(action => action.kind), ["proof"], "recording a payment must not prompt duplicate payment");
  assert.equal(decodeURIComponent(customerOrderActions(s, "order")[0].href.slice(1)), customerOrderSectionId("payment", "payment"));
  s.attachments.push({id: "proof", ownerId: "customer", divisionId: null, customerId: "customer", shipmentId: null, targetId: "payment", scope: "payment", name: "Bukti.pdf", mime: "application/pdf", size: 100, createdAt: "2026-09-20T10:00:00Z"});
  assert.equal(customerOrderActions(s, "order").length, 0, "verification is a store task");
  s.payments[0].status = "rejected";
  assert.deepEqual(customerOrderActions(s, "order").map(action => action.kind), ["payment"]);
  s.payments[0].status = "verified";
  assert.equal(customerOrderActions(s, "order").length, 0, "available invoice-linked funds await allocation");
  s.allocations.push({id: "allocation", paymentId: "payment", invoiceId: "invoice", amount: 20000, date: "2026-09-20"});
  assert.equal(customerOrderActions(s, "order").length, 0, "settled invoice needs no payment action");
});

test("v14 receipt action is available only for dispatched deliveries and points to its shipment", () => {
  const s = fixture();
  s.invoices = []; s.invoiceLines = [];
  for (const status of ["ready", "received", "failed", "cancelled"] as const) {
    s.shipments[0].status = status;
    assert.equal(customerOrderActions(s, "order").length, 0);
  }
  s.shipments[0].status = "dispatched";
  const [action] = customerOrderActions(s, "order");
  assert.equal(action.kind, "receipt");
  assert.equal(action.href, "#customer-shipment-shipment");
  assert.match(action.detail, /setelah barang tiba/);
});

test("v14 substitution tasks disappear when a decision or changed order makes the proposal inactive", () => {
  const s = fixture();
  s.invoices = []; s.invoiceLines = []; s.shipments = []; s.shipmentLines = [];
  s.substitutions.push({id: "substitution", orderLineId: "line", productId: "item", price: 9000, qty: 2, reason: "Kemasan pengganti", status: "pending", requestedBy: "staf", decidedBy: null});
  assert.deepEqual(customerOrderActions(s, "order").map(action => action.kind), ["substitution"]);
  s.substitutions[0].status = "approved";
  assert.equal(customerOrderActions(s, "order").length, 0);
  s.substitutions[0].status = "pending";
  s.orders[0].status = "cancelled";
  assert.equal(customerOrderActions(s, "order").length, 0);
  assert.equal(customerOrderActions(s, "missing-order").length, 0);
});

test("v14 order return context retains valid search/filter/page but rejects arbitrary navigation", () => {
  const href = safeCustomerOrderListHref("/account/orders?q=kopi+rapat&status=needs-pic&page=2&from=https://outside.example&notice=unused#anything");
  assert.equal(href, "/account/orders?q=kopi+rapat&status=needs-pic&page=2");
  for (const value of [null, "https://outside.example/account/orders", "//outside.example/account/orders", "/\\outside.example/account/orders", "/workspace", "/account/orders/one", "/account/orders\n"]) {
    assert.equal(safeCustomerOrderListHref(value), "/account/orders");
  }
  assert.equal(safeCustomerOrderListHref("/account/orders?status=admin&page=Infinity"), "/account/orders");
  assert.equal(safeCustomerOrderListHref("/account/orders?page=-2"), "/account/orders");
  assert.equal(safeCustomerOrderListHref("/account/orders?page=1.5"), "/account/orders");
});
