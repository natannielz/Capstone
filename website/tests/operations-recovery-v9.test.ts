import {test} from "node:test";
import assert from "node:assert/strict";
import {DEMO_ACCOUNTS} from "../lib/domain/accounts";
import {runCommand, journal} from "../lib/domain/engine";
import {DomainError} from "../lib/domain/model";
import {emptyState} from "../lib/domain/seed";
import {lineProgress, productAvailable, scopeState} from "../lib/domain/selectors";

function fixture() {
  let state = emptyState();
  state.users = structuredClone(DEMO_ACCOUNTS);
  state.divisions = [{id: "div-ops", name: "Ops", code: "OPS", address: "Jakarta"}];
  state.products = [
    {id: "p", name: "Produk", sku: "P", category: "OMI", unit: "pcs", price: 10000, minimum: 2, returnMonths: 1, active: true},
    {id: "q", name: "Pengganti", sku: "Q", category: "OMI", unit: "pcs", price: 12000, minimum: 2, returnMonths: 1, active: true},
  ];
  state.batches = [
    {id: "old", productId: "p", code: "BATCH-LAMA", qty: 6, held: 0, cost: 6000, expiry: "2026-09-18", location: "Toko"},
    {id: "fresh", productId: "p", code: "BATCH-BARU", qty: 20, held: 0, cost: 7000, expiry: "2028-01-01", location: "Toko"},
  ];
  journal(state, "2026-09-01", "opening", "Saldo awal", [["inventory", 176000, 0], ["capital", 0, 176000]]);
  return {
    get s() {return state;},
    act(who: string, type: string, data: Record<string, unknown>, date = "2026-09-17") {
      const result = runCommand(state, state.users.find(user => user.id === who)!, {id: crypto.randomUUID(), type, data, date});
      state = result.state;
      return result.result.id;
    },
  };
}
type Fixture = ReturnType<typeof fixture>;
function denied(f: Fixture, action: () => unknown, pattern: RegExp) {
  const before = structuredClone(f.s);
  assert.throws(action, pattern);
  assert.deepEqual(f.s, before, "Rejected operations preserve all state, periods and audit records");
}
function approvedOrder(f: Fixture, qty = 4) {
  const id = f.act("pic-a", "order.create", {neededAt: "2026-09-17", lines: [{productId: "p", qty}]});
  f.act("kepala", "order.review", {id, approve: true});
  return {id, lineId: f.s.orderLines.find(line => line.orderId === id)!.id};
}
function productSettings(id = "p", active: unknown = false) {
  return {id, price: 15000, minimum: 2, returnMonths: 1, active};
}

test("v9 expired reservation recovers to a fresh batch without cancelling the buyer order", () => {
  const f = fixture(), order = approvedOrder(f);
  f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 4});
  const shipment = f.act("staf", "shipment.create", {orderId: order.id, courierId: "kurir", lines: [{orderLineId: order.lineId, qty: 4}]});
  denied(f, () => f.act("kurir", "shipment.dispatch", {id: shipment}, "2026-09-18"), /kedaluwarsa/);
  denied(f, () => f.act("staf", "stock.release", {orderLineId: order.lineId, batchId: "old", qty: 4, reason: "Batch kedaluwarsa"}, "2026-09-18"), /Surat Jalan/);
  f.act("staf", "shipment.cancel", {id: shipment, reason: "Pilih ulang batch"}, "2026-09-18");
  const physical = f.s.batches.map(batch => batch.qty), journalCount = f.s.journals.length;
  f.act("staf", "stock.release", {orderLineId: order.lineId, batchId: "old", qty: 4, reason: "Batch kedaluwarsa"}, "2026-09-18");
  assert.deepEqual(f.s.batches.map(batch => batch.qty), physical);
  assert.equal(f.s.journals.length, journalCount, "Reservation changes do not move physical stock or accounting balances");
  assert.equal(f.s.orders[0].status, "approved");
  assert.equal(f.s.orderLines[0].cancelled, 0);
  assert.match(f.s.audits.at(-1)!.description, /Batch kedaluwarsa/);
  assert.equal(productAvailable(f.s, "p", "2026-09-18"), 20);
  f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 4}, "2026-09-18");
  assert.equal(f.s.reservations.find(item => item.batchId === "fresh")!.qty, 4);
  const replacement = f.act("staf", "shipment.create", {orderId: order.id, courierId: "kurir", lines: [{orderLineId: order.lineId, qty: 4}]}, "2026-09-18");
  f.act("kurir", "shipment.dispatch", {id: replacement}, "2026-09-18");
  assert.equal(f.s.batches.find(batch => batch.id === "old")!.qty, 6);
  assert.equal(f.s.batches.find(batch => batch.id === "fresh")!.qty, 16);
  assert.equal(lineProgress(f.s, f.s.orderLines[0]).remaining, 0);
});

test("v9 releasing unstaged stock preserves each batch's staged shipment and other orders", () => {
  const f = fixture(), order = approvedOrder(f, 8);
  f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 8});
  const shipment = f.act("staf", "shipment.create", {orderId: order.id, courierId: "kurir", lines: [{orderLineId: order.lineId, qty: 5}]});
  const other = approvedOrder(f, 3);
  f.act("staf", "stock.reserve", {orderLineId: other.lineId, qty: 3});
  denied(f, () => f.act("staf", "stock.release", {orderLineId: order.lineId, batchId: "old", qty: 2, reason: "Ubah penyiapan"}), /cadangan bebas/);
  f.act("staf", "stock.release", {orderLineId: order.lineId, batchId: "fresh", qty: 2, reason: "Ubah penyiapan"});
  f.act("staf", "stock.release", {orderLineId: order.lineId, batchId: "old", qty: 1, reason: "Ubah penyiapan"});
  assert.equal(lineProgress(f.s, f.s.orderLines.find(line => line.id === other.lineId)!).reserved, 3);
  assert.equal(lineProgress(f.s, f.s.orderLines.find(line => line.id === order.lineId)!).reserved, 5);
  f.act("kurir", "shipment.dispatch", {id: shipment});
  assert.equal(f.s.batches.find(batch => batch.id === "old")!.qty, 1);
});

test("v9 reservation release enforces role, reason, quantity and chronology without partial changes", () => {
  const f = fixture(), order = approvedOrder(f);
  f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 4});
  const data = {orderLineId: order.lineId, batchId: "old", qty: 1, reason: "Hitung ulang penyiapan"};
  for (const actor of f.s.users.filter(user => user.role !== "staf")) {
    const before = structuredClone(f.s);
    assert.throws(() => f.act(actor.id, "stock.release", data), (error: unknown) => error instanceof DomainError && error.status === 403);
    assert.deepEqual(f.s, before);
  }
  denied(f, () => f.act("staf", "stock.release", {...data, reason: " "}), /Alasan/);
  denied(f, () => f.act("staf", "stock.release", {...data, qty: 1.5}), /bilangan bulat/);
  denied(f, () => f.act("staf", "stock.release", {...data, batchId: "fresh"}), /cadangan bebas/);
  f.act("staf", "stock.release", data, "2026-09-19");
  denied(f, () => f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 1}, "2026-09-18"), /mendahului/);
  denied(f, () => f.act("staf", "stock.release", data, "2026-09-18"), /mendahului/);
});

test("v9 a closed period cannot be changed by releasing reservations", () => {
  const f = fixture(), order = approvedOrder(f);
  f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 4});
  f.act("laporan", "period.submit", {month: "2026-09"});
  f.act("pimpinan", "period.approve", {id: "2026-09"});
  f.act("akuntansi", "period.close", {id: "2026-09"});
  denied(f, () => f.act("staf", "stock.release", {orderLineId: order.lineId, batchId: "old", qty: 1, reason: "Revisi"}), /Periode sudah ditutup/);
});

test("v9 a used product can be retired and reactivated while existing approved orders retain price and fulfillment", () => {
  const f = fixture(), order = approvedOrder(f);
  f.act("kepala", "product.update", productSettings());
  assert.equal(f.s.products[0].active, false);
  for (const who of ["pic-a", "kepala", "customer-demo"]) {
    denied(f, () => f.act(who, "order.create", {divisionId: "div-ops", neededAt: "2026-09-17", address: "Jakarta", recipientName: "Demo", recipientPhone: "08123456789", lines: [{productId: "p", qty: 1, unitPrice: 15000}]}), /Produk tidak aktif/);
  }
  const customerView = scopeState(f.s, f.s.users.find(user => user.id === "customer-demo")!);
  assert.equal(customerView.products.some(product => product.id === "p"), false, "Inactive products without customer history leave the storefront");
  f.act("staf", "stock.reserve", {orderLineId: order.lineId, qty: 4});
  const shipment = f.act("staf", "shipment.create", {orderId: order.id, courierId: "kurir", lines: [{orderLineId: order.lineId, qty: 4}]});
  f.act("kurir", "shipment.dispatch", {id: shipment});
  const line = f.s.shipmentLines.find(item => item.shipmentId === shipment)!;
  f.act("pic-a", "shipment.receive", {id: shipment, lines: [{id: line.id, accepted: 4}]});
  f.act("staf", "sale.finalize", {id: shipment});
  assert.equal(f.s.orderLines[0].price, 10000);
  assert.equal(f.s.shipmentLines[0].price, 10000);
  f.act("kepala", "product.update", productSettings("p", true));
  f.act("pic-a", "order.create", {neededAt: "2026-09-17", lines: [{productId: "p", qty: 1}]});
  assert.equal(f.s.orderLines.at(-1)!.price, 15000);
});

test("v9 pending substitution cannot introduce an inactive SKU and remains rejectable", () => {
  const f = fixture(), order = approvedOrder(f);
  f.act("staf", "substitution.propose", {orderLineId: order.lineId, productId: "q", reason: "Pilihan lain"});
  const substitution = f.s.substitutions[0].id;
  f.act("kepala", "product.update", productSettings("q"));
  denied(f, () => f.act("pic-a", "substitution.decide", {id: substitution, approve: true}), /dinonaktifkan/);
  f.act("pic-a", "substitution.decide", {id: substitution, approve: false});
  denied(f, () => f.act("staf", "substitution.propose", {orderLineId: order.lineId, productId: "q", reason: "Pilihan baru"}), /aktif/);
  assert.equal(f.s.orderLines.length, 1);
  assert.equal(f.s.orderLines[0].cancelled, 0);
});

test("v9 only Kepala changes selling status and legacy product settings preserve inactivity", () => {
  const f = fixture();
  for (const actor of f.s.users.filter(user => user.role !== "kepala")) {
    assert.throws(() => f.act(actor.id, "product.update", productSettings()), (error: unknown) => error instanceof DomainError && error.status === 403);
  }
  denied(f, () => f.act("kepala", "product.update", productSettings("p", "false")), /Status penjualan/);
  f.act("kepala", "product.update", productSettings());
  f.act("kepala", "product.update", {id: "p", price: 16000, minimum: 3, returnMonths: 2});
  assert.equal(f.s.products[0].active, false);
  assert.equal(f.s.products[0].price, 16000);
});
