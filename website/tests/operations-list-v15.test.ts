import { test } from "node:test";
import assert from "node:assert/strict";
import { emptyState } from "../lib/domain/seed";
import { accountListRows, paginateOperations, purchaseListRows, readAccountListQuery, readPurchaseListQuery } from "../lib/domain/operations-list-v15";
import type { Purchase } from "../lib/domain/model";

function fixture() {
  const s = emptyState();
  s.divisions = [{ id: "ops", name: "Divisi Operasional", code: "OPS", address: "Jakarta" }];
  s.suppliers = [{ id: "supplier", name: "Sumber Sejahtera", category: "OMI" }];
  s.products = [{ id: "paper", sku: "KRT-A4", name: "Kertas rapat A4", category: "OMI", unit: "rim", price: 40000, minimum: 5, returnMonths: 0, active: true }];
  s.orders = [{ id: "order", number: "PO-DIV-41", divisionId: "ops", createdBy: "pic", createdAt: "2026-09-21", neededAt: "2026-09-23", address: "Jakarta", note: "", status: "approved", origin: "demo" }];
  s.purchases = (["draft", "ordered", "complete", "closed"] as Purchase["status"][]).map((status, i) => ({ id: `purchase-${i}`, number: `PG-00${i}`, supplierId: "supplier", kind: "OMI reguler", orderId: i === 1 ? "order" : null, date: "2026-09-21", status, note: "", fundingStatus: "none", fundingAmount: 0 }));
  s.purchaseLines = [{ id: "line", purchaseId: "purchase-1", productId: "paper", qty: 10, received: 0, cost: 30000 }];
  s.users = [
    { id: "pic", name: "Rani Putri", email: "rani@unit.demo", role: "pic", divisionId: "ops", active: true },
    { id: "staff", name: "Rani Wijaya", email: "staf@unit.demo", role: "staf", divisionId: null, active: false },
    { id: "customer", name: "Pembeli Demo", email: "pembeli@unit.demo", role: "customer", divisionId: null, active: true },
  ];
  return s;
}

test("purchase search follows supplier, linked order, product name and SKU", () => {
  const s = fixture();
  for (const search of [" KRT-a4 ", "kertas rapat", "po-div-41"]) {
    const query = readPurchaseListQuery(new URLSearchParams({ purchaseQ: search }));
    assert.deepEqual(purchaseListRows(s, query).map(row => row.id), ["purchase-1"]);
  }
  assert.equal(purchaseListRows(s, readPurchaseListQuery(new URLSearchParams("purchaseQ=sejahtera"))).length, 4);
  assert.deepEqual(purchaseListRows(s, readPurchaseListQuery(new URLSearchParams("purchaseQ=PG-003"))).map(row => row.status), ["closed"]);
});

test("open procurement means draft or ordered; status and search combine without mutating source", () => {
  const s = fixture(), before = structuredClone(s);
  assert.deepEqual(purchaseListRows(s, readPurchaseListQuery(new URLSearchParams("purchaseStatus=open"))).map(row => row.status), ["ordered", "draft"]);
  assert.equal(purchaseListRows(s, readPurchaseListQuery(new URLSearchParams("purchaseStatus=closed&purchaseQ=KRT-A4"))).length, 0);
  assert.deepEqual(s, before);
});

test("accounts match name/email/role/division and respect both role and active filters", () => {
  const s = fixture();
  for (const search of ["operasional", "RANI@UNIT", "PIC Divisi"]) {
    assert.deepEqual(accountListRows(s, readAccountListQuery(new URLSearchParams({ accountQ: search }))).map(user => user.id), ["pic"]);
  }
  assert.deepEqual(accountListRows(s, readAccountListQuery(new URLSearchParams("accountQ=rani&accountStatus=inactive&accountRole=staf"))).map(user => user.id), ["staff"]);
  assert.equal(accountListRows(s, readAccountListQuery(new URLSearchParams("accountRole=customer&accountStatus=inactive"))).length, 0);
  assert.deepEqual(accountListRows(s, readAccountListQuery(new URLSearchParams("accountQ=Pelanggan"))).map(user => user.id), ["customer"]);
});

test("invalid URL values recover safely, preserving supported sections and roles", () => {
  for (const bad of ["-1", "0", "NaN", "Infinity", "3.1", "9007199254740992"]) {
    assert.equal(readPurchaseListQuery(new URLSearchParams({ purchasePage: bad })).page, 1);
    assert.equal(readAccountListQuery(new URLSearchParams({ accountPage: bad })).page, 1);
  }
  const purchase = readPurchaseListQuery(new URLSearchParams("purchaseStatus=paid"));
  assert.equal(purchase.status, "all");
  const account = readAccountListQuery(new URLSearchParams("accountRole=superadmin&accountStatus=deleted&adminSection=secrets"));
  assert.equal(account.role, "all"); assert.equal(account.status, "all"); assert.equal(account.section, "users");
  assert.equal(readAccountListQuery(new URLSearchParams("adminSection=suppliers&accountRole=customer")).section, "suppliers");
  assert.equal(readAccountListQuery(new URLSearchParams("accountRole=customer")).role, "customer");
});

test("pagination handles empty data, extreme URL pages, and reduced results after filtering", () => {
  const rows = Array.from({ length: 25 }, (_, i) => i + 1);
  const last = paginateOperations(rows, 9999, 12);
  assert.deepEqual(last.rows, [25]); assert.equal(last.page, 3); assert.equal(last.first, 25); assert.equal(last.last, 25);
  const filtered = paginateOperations(rows.filter(row => row <= 3), 3, 12);
  assert.deepEqual(filtered.rows, [1, 2, 3]); assert.equal(filtered.page, 1);
  assert.deepEqual(paginateOperations([], 9, 12), { rows: [], total: 0, page: 1, pages: 1, first: 0, last: 0 });
  assert.equal(paginateOperations(rows, Infinity, 0).rows.length, 12);
  assert.deepEqual(rows, Array.from({ length: 25 }, (_, i) => i + 1));
});

test("empty and partial related data remain searchable without broadening role/status results", () => {
  const s = fixture();
  s.suppliers = []; s.products = []; s.orders = []; s.divisions = [];
  assert.equal(purchaseListRows(s, readPurchaseListQuery(new URLSearchParams("purchaseQ=PG-001"))).length, 1);
  assert.equal(purchaseListRows(s, readPurchaseListQuery(new URLSearchParams("purchaseQ=missing"))).length, 0);
  assert.deepEqual(accountListRows(s, readAccountListQuery(new URLSearchParams("accountQ=Lintas+unit&accountRole=staf"))).map(user => user.id), ["staff"]);
  assert.equal(accountListRows(s, readAccountListQuery(new URLSearchParams("accountQ=Lintas+unit&accountRole=customer"))).length, 0);
});
