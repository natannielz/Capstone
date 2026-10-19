import { test } from "node:test";
import assert from "node:assert/strict";
import { parseWorkSummary, workAgeText, workCountText } from "../src/modules/identity/work-summary-client";

const now = Date.parse("2026-10-20T10:00:00Z");
const item = { key: "billing.payment-reviews", area: "finance", label: "Bukti pembayaran menunggu pemeriksaan", href: "/billing/payment-reviews",
  count: 3, hasMore: false, oldestAt: "2026-10-20T08:00:00Z", unavailable: false };

test("waiting age reads naturally in Bahasa Indonesia", () => {
  assert.equal(workAgeText("2026-10-20T09:59:30Z", now), "menunggu kurang dari 1 menit");
  assert.equal(workAgeText("2026-10-20T09:55:00Z", now), "menunggu sejak 5 menit");
  assert.equal(workAgeText("2026-10-20T08:00:00Z", now), "menunggu sejak 2 jam");
  assert.equal(workAgeText("2026-10-17T10:00:00Z", now), "menunggu sejak 3 hari");
  assert.equal(workAgeText(null, now), "");
});

test("counts show a plus only when more pages exist", () => {
  assert.equal(workCountText(3, false), "3");
  assert.equal(workCountText(25, true), "25+");
  assert.equal(workCountText(1000, false), "1.000");
});

test("the summary parser accepts the contract and refuses unexpected shapes or destinations", () => {
  const parsed = parseWorkSummary({ items: [item], observedAt: "2026-10-20T10:00:00Z" });
  assert.equal(parsed.items[0].label, item.label);
  assert.throws(() => parseWorkSummary({ items: [{ ...item, extra: true }], observedAt: "2026-10-20T10:00:00Z" }));
  assert.throws(() => parseWorkSummary({ items: [{ ...item, href: "https://elsewhere.example/" }], observedAt: "2026-10-20T10:00:00Z" }), "only app-relative paths");
  assert.throws(() => parseWorkSummary({ items: [{ ...item, count: -1 }], observedAt: "2026-10-20T10:00:00Z" }));
  assert.throws(() => parseWorkSummary({ items: [], observedAt: "kemarin" }));
});
