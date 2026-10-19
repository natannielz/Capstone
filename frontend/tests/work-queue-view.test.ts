import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { WorkQueue } from "../src/modules/identity/WorkQueue";

const observedAt = "2026-10-20T10:00:00Z";
const item = (overrides: Record<string, unknown>) => ({ key: "ordering.requests.incoming", area: "Pesanan", label: "Permintaan cabang menunggu peninjauan",
  href: "/ordering/requests/incoming", count: 7, hasMore: false, oldestAt: "2026-10-20T07:00:00Z", unavailable: false, ...overrides });

// "Pekerjaan saya" leads with how much is waiting: each queue is one link whose name carries the count, oldest first.
test("each waiting queue is one link that names its count and how long it has waited", () => {
  const html = renderToStaticMarkup(createElement(WorkQueue, { observedAt, items: [
    item({}), item({ key: "billing.payment-review", area: "Pembayaran", label: "Bukti pembayaran perlu diperiksa", href: "/billing/payment-review", count: 25, hasMore: true, oldestAt: "2026-10-19T10:00:00Z" }),
  ] }));
  assert.match(html, /<ol[^>]*aria-label="Pekerjaan yang menunggu"/);
  assert.match(html, /<a[^>]*href="\/ordering\/requests\/incoming"[^>]*>[\s\S]*?>7<[\s\S]*?Permintaan cabang menunggu peninjauan[\s\S]*?<\/a>/);
  assert.match(html, /menunggu sejak 3 jam/);
  assert.match(html, />25\+</, "a partial first page shows a plus");
  assert.match(html, /menunggu setidaknya 1 hari/);
  assert.match(html, />Pembayaran</, "the area is shown");
});

test("a queue that could not be read says so instead of showing zero", () => {
  const html = renderToStaticMarkup(createElement(WorkQueue, { observedAt, items: [item({ count: 0, oldestAt: null, unavailable: true })] }));
  assert.match(html, />—</);
  assert.match(html, /Belum dapat dimuat\. Buka ruang kerjanya untuk memeriksa\./);
  assert.doesNotMatch(html, />0</);
});
