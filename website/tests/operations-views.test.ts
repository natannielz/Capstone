import { test } from "node:test";
import assert from "node:assert/strict";
import { emptyState } from "../lib/domain/seed";
import { inventoryView, matchesInventoryProduct, matchesInventoryBatch, readInventoryQuery, salesRows, salesCSV, validReportDate } from "../lib/domain/operations-views";

function fixture() {
  const s = emptyState();
  s.products = [{ id: "p", sku: "OMI-P3", name: "Air mineral paket tiga", category: "OMI", unit: "paket", price: 30000, minimum: 5, returnMonths: 1, active: true }, { id: "q", sku: "SMT-01", name: "Tumbler", category: "Smart", unit: "pcs", price: 80000, minimum: 2, returnMonths: 0, active: true }];
  s.batches = [
    { id: "b", productId: "p", code: "LOT-A", qty: 10, held: 2, cost: 20000, expiry: "2026-02-28", location: "Rak A" },
    { id: "expired", productId: "p", code: "LOT-LAMA", qty: 4, held: 0, cost: 20000, expiry: "2026-01-31", location: "Gudang" },
    { id: "q-batch", productId: "q", code: "LOT-B", qty: 8, held: 0, cost: 60000, expiry: "", location: "Rak B" },
  ];
  s.reservations = [{ id: "r", orderLineId: "line", batchId: "b", qty: 3 }];
  return s;
}

test("inventory aggregates exclude expired, held, and reserved stock with SKU filters", () => {
  const s = fixture(), view = inventoryView(s, "2026-01-31"), p = view.products[0];
  assert.equal(p.physical, 14); assert.equal(p.available, 5); assert.equal(p.held, 2); assert.equal(p.reserved, 3); assert.equal(p.expired, 4);
  const query = readInventoryQuery(new URLSearchParams("stockSearch=omi-p3&stockSource=OMI&stockCondition=low"));
  assert.deepEqual(view.products.filter(row => matchesInventoryProduct(row, query)).map(row => row.product.id), ["p"]);
  assert.equal(view.products.filter(row => matchesInventoryProduct(row, { ...query, source: "Smart" })).length, 0);
});

test("inventory reminder clamps month-end and batch search matches code", () => {
  const s = fixture(); s.batches.push({ ...s.batches[0], id: "march", code: "MARCH", expiry: "2026-03-01" });
  const view = inventoryView(s, "2026-01-31");
  assert.equal(view.batches.find(row => row.batch.id === "b")?.expiring, true);
  assert.equal(view.batches.find(row => row.batch.id === "march")?.expiring, false);
  const query = readInventoryQuery(new URLSearchParams("stockTab=batches&stockSearch=lot-lama&stockCondition=expired"));
  assert.deepEqual(view.batches.filter(row => matchesInventoryBatch(row, query, view.products)).map(row => row.batch.id), ["expired"]);
});

test("invalid inventory query and report dates recover to valid defaults", () => {
  const query = readInventoryQuery(new URLSearchParams("stockTab=admin&stockSource=other&stockCondition=anything&stockPage=-9"));
  assert.equal(query.tab, "products"); assert.equal(query.source, "all"); assert.equal(query.condition, "all"); assert.equal(query.page, 1);
  assert.equal(validReportDate("2026-02-31", "2026-01-31"), "2026-01-31");
  assert.equal(validReportDate("2028-02-29", "2026-01-31"), "2028-02-29");
});

function salesFixture() {
  const s = fixture();
  s.divisions = [{ id: "a", name: "Divisi A", code: "A", address: "Jakarta" }, { id: "b", name: "Divisi B", code: "B", address: "Jakarta" }];
  for (const [id, divisionId, productId, date] of [["one", "a", "p", "2026-09-17"], ["two", "b", "p", "2026-09-17"], ["three", "a", "q", "2026-09-17"], ["old", "a", "p", "2026-08-31"]]) {
    s.orders.push({ id, number: `PO-${id}`, divisionId, createdBy: "pic", createdAt: date, neededAt: date, address: "Jakarta", note: "", status: "approved", origin: "demo" });
    s.orderLines.push({ id, orderId: id, productId, requestedProductId: productId, qty: 2, cancelled: 0, price: 30000, note: "" });
    s.shipments.push({ id, orderId: id, number: `SJ-${id}`, courierId: "kurir", vehicle: "", date, status: "received", receiver: "PIC", receivedAt: date, receivedDate: date, evidence: "", note: "" });
    s.shipmentLines.push({ id, shipmentId: id, orderLineId: id, batchId: "b", qty: 2, accepted: 2, returned: 0, returnGood: 0, finalized: true, finalizedDate: date, price: 30000, cost: 20000 });
  }
  return s;
}

test("sales export contains precisely the selected finalization period, source, and division", () => {
  const s = salesFixture(), filter = { start: "2026-09-01", end: "2026-09-30", category: "OMI", division: "a" };
  const rows = salesRows(s, filter), csv = salesCSV(s, filter);
  assert.deepEqual(rows.map(row => row.line.id), ["one"]);
  assert.ok(csv.includes("SJ-one")); assert.ok(!csv.includes("SJ-two")); assert.ok(!csv.includes("SJ-three")); assert.ok(!csv.includes("SJ-old"));
  assert.ok(csv.includes('"60000"')); assert.ok(csv.includes("Tanggal finalisasi"));
  assert.equal(salesRows(s, { ...filter, start: "2026-10-01" }).length, 0);
});

test("sales CSV escapes formula prefixes and embedded quotes without changing quantities", () => {
  const s = salesFixture(); s.products[0].name = '=SUM(1,2) "contoh"';
  const csv = salesCSV(s, { start: "2026-09-01", end: "2026-09-30", category: "OMI", division: "a" });
  assert.ok(csv.includes('"\'=SUM(1,2) ""contoh"""'));
  assert.ok(csv.includes('"2","paket","60000"'));
});
