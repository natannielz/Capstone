import { test } from "node:test";
import assert from "node:assert/strict";
import { DEMO_ACCOUNTS } from "../lib/domain/accounts";
import type { Payment } from "../lib/domain/model";
import { emptyState } from "../lib/domain/seed";
import { scopeState } from "../lib/domain/selectors";
import { PAYMENT_PAGE_SIZE, paymentDisplayStatus, paymentListView, readPaymentQuery } from "../lib/domain/payment-views-v15";

function payment(id: string, changes: Partial<Payment> = {}): Payment {
  return { id, divisionId: "div-ops", amount: 100000, date: "2026-09-23", reference: `REF-${id}`, payer: `Pembayar ${id}`, note: "", status: "recorded", verifiedBy: null, evidence: "", createdBy: "pic-a", ...changes };
}
function fixture() {
  const s = emptyState();
  s.users = structuredClone(DEMO_ACCOUNTS);
  s.divisions = [
    { id: "div-ops", name: "Divisi Operasional", code: "OPS", address: "Jakarta" },
    { id: "div-ti", name: "Divisi Teknologi", code: "TI", address: "Jakarta" },
  ];
  s.payments = [
    payment("one", { payer: "Nadia Putri", reference: "BNI-REF-001" }),
    payment("two", { status: "verified", divisionId: "div-ti" }),
    payment("three", { status: "verified", divisionId: null }),
    payment("four", { status: "rejected", divisionId: null }),
    payment("five", { status: "recorded", divisionId: null }),
  ];
  return s;
}
const query = (raw = "") => readPaymentQuery(new URLSearchParams(raw));

test("v15 payment query rejects unsupported statuses and invalid page numbers without changing other query keys", () => {
  for (const page of ["0", "-1", "2.5", "Infinity", "NaN", "9007199254740992"]) {
    assert.equal(query(`paymentPage=${page}`).page, 1);
  }
  const params = new URLSearchParams("view=payments&paymentStatus=constructor&paymentSearch=Nadia&paymentPage=2&section=suppliers");
  const before = params.toString();
  assert.deepEqual(readPaymentQuery(params), { status: "all", search: "Nadia", page: 2 });
  assert.equal(params.toString(), before);
});

test("v15 payment status filters match displayed states without classifying unverified transfers as unidentified", () => {
  const s = fixture();
  assert.equal(paymentDisplayStatus(s.payments[4]), "recorded");
  assert.equal(paymentDisplayStatus(s.payments[3]), "rejected");
  assert.deepEqual(paymentListView(s, query("paymentStatus=recorded")).items.map(row => row.id), ["five", "one"]);
  assert.deepEqual(paymentListView(s, query("paymentStatus=verified")).items.map(row => row.id), ["two"]);
  assert.deepEqual(paymentListView(s, query("paymentStatus=unidentified")).items.map(row => row.id), ["three"]);
  assert.deepEqual(paymentListView(s, query("paymentStatus=rejected")).items.map(row => row.id), ["four"]);
});

test("v15 payment search combines payer, reference and buyer with status, case and whitespace normalization", () => {
  const s = fixture();
  assert.deepEqual(paymentListView(s, query("paymentSearch=%20NADIA%20")).items.map(row => row.id), ["one"]);
  assert.deepEqual(paymentListView(s, query("paymentSearch=bni-ref")).items.map(row => row.id), ["one"]);
  assert.deepEqual(paymentListView(s, query("paymentSearch=teknologi&paymentStatus=verified")).items.map(row => row.id), ["two"]);
  assert.equal(paymentListView(s, query("paymentSearch=teknologi&paymentStatus=recorded")).count, 0);
  assert.equal(paymentListView(s, query("paymentSearch=%20%20")).filtered, false);
});

test("v15 payment pagination has no duplicates and retains latest-record-first order", () => {
  const s = fixture();
  s.payments = Array.from({ length: 29 }, (_, index) => payment(String(index)));
  const first = paymentListView(s, query());
  const second = paymentListView(s, query("paymentPage=2"));
  const third = paymentListView(s, query("paymentPage=3"));
  assert.equal(first.items.length, PAYMENT_PAGE_SIZE);
  assert.deepEqual([first.start, first.end, second.start, second.end, third.start, third.end], [1, 12, 13, 24, 25, 29]);
  assert.deepEqual([...first.items, ...second.items, ...third.items].map(row => row.id), [...s.payments].reverse().map(row => row.id));
  assert.equal(new Set([...first.items, ...second.items, ...third.items].map(row => row.id)).size, 29);
});

test("v15 payment views clamp old page URLs after data changes and distinguish no records from no filter results", () => {
  const s = fixture();
  const clamped = paymentListView(s, query("paymentPage=999"));
  assert.equal(clamped.page, 1);
  assert.equal(clamped.items.length, 5);
  const noMatches = paymentListView(s, query("paymentSearch=not-present&paymentPage=5"));
  assert.deepEqual([noMatches.total, noMatches.count, noMatches.page, noMatches.start, noMatches.end, noMatches.filtered], [5, 0, 1, 0, 0, true]);
  const noData = paymentListView(emptyState(), query());
  assert.deepEqual([noData.total, noData.count, noData.page, noData.filtered], [0, 0, 1, false]);
});

test("v15 payment filtering never widens PIC scope or mutates records and balances", () => {
  const source = fixture();
  const account = source.users.find(item => item.id === "pic-a")!;
  const s = scopeState(source, account);
  const before = structuredClone(s);
  assert.deepEqual(paymentListView(s, query()).items.map(row => row.id), ["one"]);
  assert.equal(paymentListView(s, query("paymentSearch=teknologi")).count, 0);
  paymentListView(s, query("paymentStatus=recorded&paymentPage=4"));
  assert.deepEqual(s, before);
});

test("v15 customer identity and credit records remain searchable as existing payment records", () => {
  const s = fixture();
  s.payments.push(payment("customer-credit", { divisionId: null, customerId: "customer-demo", status: "verified", kind: "credit", sourceCreditId: "credit-1" }));
  const view = paymentListView(s, query("paymentSearch=pelanggan%20demo&paymentStatus=verified"));
  assert.deepEqual(view.items.map(row => row.id), ["customer-credit"]);
});
