import { test } from "node:test";
import assert from "node:assert/strict";
import { identityDestinations } from "../src/modules/identity/identity-workflow";
import { workspaceDirectory } from "../src/modules/identity/workspace-directory";
import { navigationClick, NAVIGATION_HREFS, workspaceNavigation } from "../src/modules/identity/workspace-navigation";

// A broad actor: every action string the destinations check for.
const everything = ["order.request.read", "order.request.submit", "order.request.decide", "order.read", "order.spend.approve", "order.accept", "order.cancel",
  "substitution.propose", "substitution.accept", "case.read", "case.request", "shipment.prepare", "shipment.dispatch", "shipment.assign", "shipment.read", "delivery.attempt",
  "receipt.accept", "invoice.read", "invoice.issue", "bank.receipt.read", "bank.statement.read", "receiving.account.read", "payment.review.read", "payment.evidence",
  "pricing.manage", "pricing.approve", "catalog.manage", "catalog.read", "stock.read", "stock.convert", "stock.release.propose", "stock.release.approve", "stock.count",
  "stock.source.read", "stock.source.reconciliation.read", "access.audit.read", "access.approve", "membership.approve", "technical.configure", "organization.approve",
  "supplier.read", "procurement.read", "procurement.payable.read", "accounting.book.read", "accounting.report.read", "operations.anomaly.read", "commercial.policy.read", "report.read"];

test("every discovered destination belongs to a navigation area (none silently hidden)", () => {
  const destinations = identityDestinations(everything);
  const grouped = new Set(workspaceDirectory(everything).flatMap(area => area.items.map(item => item.href)));
  assert.deepEqual(destinations.map(item => item.href).filter(href => !grouped.has(href)), []);
});

test("navigation groups follow the directory areas, starting with Pekerjaan saya and ending with Akun saya", () => {
  const groups = workspaceNavigation(["payment.review.read", "invoice.read"], "/billing/payment-reviews");
  assert.equal(groups[0].items[0].href, "/identity/account");
  assert.equal(groups[0].items[0].label, "Pekerjaan saya");
  const finance = groups.find(group => group.id === "finance");
  assert.ok(finance);
  const review = finance.items.find(item => item.href === "/billing/payment-reviews");
  assert.deepEqual(review && { current: review.current, available: review.available }, { current: true, available: true });
  assert.equal(groups.at(-1)?.items.at(-1)?.label, "Akun saya");
});

test("the current page stays listed, marked unavailable, when its permission was withdrawn", () => {
  const groups = workspaceNavigation([], "/billing/payment-reviews");
  const item = groups.flatMap(group => group.items).find(entry => entry.href === "/billing/payment-reviews");
  assert.deepEqual(item && { current: item.current, available: item.available }, { current: true, available: false });
});

test("pilot release hides non-pilot destinations even when an action grants them", () => {
  const hrefs = workspaceNavigation(everything, null, { pilot: true }).flatMap(group => group.items.map(item => item.href));
  assert.ok(hrefs.includes("/ordering/requests"));
  assert.equal(hrefs.includes("/billing/payment-reviews"), false);
  assert.equal(hrefs.includes("/procurement/suppliers"), false);
});

test("locked navigation never navigates", () => {
  let prevented = 0; const event = { preventDefault: () => { prevented++; } };
  navigationClick(true)(event); navigationClick(false)(event);
  assert.equal(prevented, 1);
});

test("every legacy current id used by workspaces resolves to an href", () => {
  for (const [id, href] of Object.entries(NAVIGATION_HREFS)) assert.match(href, /^\//, id);
  assert.equal(NAVIGATION_HREFS["payment-reviews"], "/billing/payment-reviews");
});
