import { test } from "node:test";
import assert from "node:assert/strict";
import { buyerFilterValue, buyerOptions } from "../lib/domain/buyer-views";
import { salesCSV, salesRows, type SalesFilter } from "../lib/domain/operations-views";
import { emptyState } from "../lib/domain/seed";
import type { BuyerRef } from "../lib/domain/buyers";

const all: SalesFilter = { start: "2026-09-01", end: "2026-09-30", category: "all", division: "all" };

function fixture() {
  const s = emptyState();
  // Operational views deliberately have no customer directory or contact records.
  s.divisions = [{ id: "div-ops", name: "Divisi Operasional", code: "OPS", address: "Alamat divisi" }];
  s.products = [
    { id: "omi", sku: "OMI-QA", name: "Barang OMI", category: "OMI", unit: "pcs", price: 15000, minimum: 0, returnMonths: 1, active: true },
    { id: "smart", sku: "SMART-QA", name: "Barang Smart", category: "Smart", unit: "pcs", price: 30000, minimum: 0, returnMonths: 0, active: true },
  ];
  function sale(id: string, ref: BuyerRef, buyerName: string, accepted: number, price: number, productId = "omi", finalized = true, date = "2026-09-20") {
    s.orders.push({ id, number: `PO-${id}`, ...ref, buyerName, createdBy: ref.customerId || "pic-a", createdAt: "2026-08-30T12:00:00Z", neededAt: "2026-08-31", address: "Private address excluded from export", note: "Private note excluded from export", status: "approved", origin: "Fixture" });
    s.orderLines.push({ id: `ol-${id}`, orderId: id, productId, requestedProductId: productId, qty: accepted || 1, cancelled: 0, price, note: "" });
    s.shipments.push({ id: `sh-${id}`, number: `SJ-${id}`, orderId: id, courierId: "kurir", vehicle: "QA", date: "2026-08-31", status: "received", receiver: "Private recipient", receivedAt: "2026-08-31T12:00:00Z", receivedDate: "2026-08-31", evidence: "Private evidence", note: "" });
    s.shipmentLines.push({ id: `sl-${id}`, shipmentId: `sh-${id}`, orderLineId: `ol-${id}`, batchId: "private-batch", qty: accepted || 1, accepted, returned: 0, returnGood: 0, finalized, finalizedDate: finalized ? date : undefined, price, cost: 4321 });
  }
  sale("customer-a", { divisionId: null, customerId: "a" }, "Nama pembeli A saat memesan", 2, 10000);
  sale("customer-b", { divisionId: null, customerId: "b" }, "Nama pembeli B saat memesan", 1, 25000, "smart");
  sale("division", { divisionId: "div-ops" }, "", 3, 7000);
  sale("a-unfinal", { divisionId: null, customerId: "a" }, "Nama pembeli A saat memesan", 1, 10000, "omi", false);
  sale("a-zero", { divisionId: null, customerId: "a" }, "Nama pembeli A saat memesan", 0, 10000);
  sale("a-old", { divisionId: null, customerId: "a" }, "Nama pembeli A saat memesan", 3, 10000, "omi", true, "2026-08-31");
  return s;
}

/** Parse emitted CSV independently, including quoted commas and escaped quotes. */
function parseCSV(csv: string) {
  const rows: string[][] = [];
  let row: string[] = [], value = "", quoted = false;
  const text = csv.replace(/^\uFEFF/, "");
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (character === '"') {
      if (quoted && text[index + 1] === '"') { value += '"'; index++; }
      else quoted = !quoted;
    } else if (character === "," && !quoted) { row.push(value); value = ""; }
    else if (character === "\r" && text[index + 1] === "\n" && !quoted) { row.push(value); rows.push(row); row = []; value = ""; index++; }
    else value += character;
  }
  row.push(value); rows.push(row); return rows;
}

test("v7 buyer filters preserve v6 division URLs and distinguish customers sharing null division", () => {
  const s = fixture();
  assert.equal(buyerFilterValue({ divisionId: "div-ops" }), "div-ops");
  assert.equal(buyerFilterValue({ divisionId: null, customerId: "a" }), "customer:a");
  assert.equal(buyerFilterValue({ divisionId: null, customerId: "b" }), "customer:b");
  assert.equal(buyerFilterValue({ divisionId: null }), "");
  assert.equal(buyerFilterValue({ divisionId: "div-ops", customerId: "a" }), "");
  assert.deepEqual(salesRows(s, { ...all, division: "customer:a" }).map(row => row.order.id), ["customer-a"]);
  assert.deepEqual(salesRows(s, { ...all, division: "customer:b" }).map(row => row.order.id), ["customer-b"]);
  assert.deepEqual(salesRows(s, { ...all, division: "div-ops" }).map(row => row.order.id), ["division"]);
  assert.equal(salesRows(s, { ...all, division: "customer:missing" }).length, 0);
});

test("v7 buyer options use transaction snapshots without disclosing customer directory or duplicating buyers", () => {
  const s = fixture(), options = buyerOptions(s);
  assert.equal(s.users.length, 0);
  assert.deepEqual(options.map(option => option.value).sort(), ["customer:a", "customer:b", "div-ops"]);
  assert.equal(options.find(option => option.value === "customer:a")?.label, "Pelanggan · Nama pembeli A saat memesan");
  assert.equal(options.find(option => option.value === "customer:b")?.label, "Pelanggan · Nama pembeli B saat memesan");
  assert.deepEqual(options.find(option => option.value === "customer:b")?.ref, { divisionId: null, customerId: "b" });
  assert.ok(!JSON.stringify(options).includes("Private"));
});

test("v7 customer CSV matches finalized filtered rows and frozen prices, without another buyer or private fields", () => {
  const s = fixture(), filter = { ...all, division: "customer:a" };
  const exported = salesCSV(s, filter), rows = parseCSV(exported);
  assert.ok(exported.startsWith("\uFEFF"));
  assert.deepEqual(rows[3], ["Pembeli", "Pelanggan · Nama pembeli A saat memesan"]);
  assert.equal(rows[6][0], "2026-09-20", "filter and export use finalization date, not August delivery date");
  assert.deepEqual(rows[6].slice(1), ["SJ-customer-a", "Nama pembeli A saat memesan", "OMI-QA", "Barang OMI", "OMI", "2", "pcs", "20000"]);
  assert.equal(rows.length, 8, "one included sale plus metadata and total");
  assert.equal(rows.at(-1)?.[8], "20000", "historical sale price remains 10,000 despite current 15,000 price");
  for (const forbidden of ["Nama pembeli B", "SJ-division", "SJ-a-old", "SJ-a-zero", "SJ-a-unfinal", "Private", "4321", "private-batch"]) assert.ok(!exported.includes(forbidden), forbidden);
});

test("v7 mixed buyer register reconciles all totals and category filters keep the selected buyer", () => {
  const s = fixture();
  assert.equal(salesRows(s, all).length, 3);
  assert.equal(parseCSV(salesCSV(s, all)).at(-1)?.[8], "66000");
  assert.equal(salesRows(s, { ...all, division: "customer:b", category: "OMI" }).length, 0);
  assert.equal(parseCSV(salesCSV(s, { ...all, division: "customer:b", category: "Smart" })).at(-1)?.[8], "25000");
});

test("v7 customer names containing CSV formula prefixes and punctuation remain inert cells", () => {
  const s = fixture();
  for (const order of s.orders.filter(order => order.customerId === "a")) order.buyerName = '=HYPERLINK("https://example.invalid","Nama, QA")';
  const rows = parseCSV(salesCSV(s, { ...all, division: "customer:a" }));
  assert.equal(rows[6][2], '\'=HYPERLINK("https://example.invalid","Nama, QA")');
  assert.equal(rows[6].length, 9, "quoted commas do not create extra columns");
  assert.equal(rows.at(-1)?.[8], "20000");
});
