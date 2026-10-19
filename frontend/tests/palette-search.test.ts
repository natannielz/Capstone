import { test } from "node:test";
import assert from "node:assert/strict";
import { paletteSearch, isPaletteShortcut } from "../src/components/workspace-kit/palette-search";
import type { NavigationGroup } from "../src/modules/identity/workspace-navigation";

const groups: NavigationGroup[] = [
  { id: "home", label: "Beranda", items: [{ href: "/identity/account", label: "Pekerjaan saya", description: "Ruang kerja dan tugas.", current: false, available: true }] },
  { id: "finance", label: "Keuangan dan pembukuan", items: [
    { href: "/billing/payments", label: "Bukti pembayaran", description: "Unggah bukti transfer.", current: false, available: true },
    { href: "/billing/payment-reviews", label: "Pemeriksaan pembayaran", description: "Bandingkan bukti dan tagihan.", current: true, available: true },
    { href: "/billing/invoices", label: "Tagihan", description: "Terbitkan invoice.", current: false, available: false },
  ] },
];

test("search matches label, description and area, case- and accent-insensitively, prefix matches first", () => {
  assert.deepEqual(paletteSearch(groups, "bayar").map(item => item.href), ["/billing/payments", "/billing/payment-reviews"]);
  assert.deepEqual(paletteSearch(groups, "PEMERIKSAAN").map(item => item.href), ["/billing/payment-reviews"]);
  assert.deepEqual(paletteSearch(groups, "keuangan").map(item => item.href), ["/billing/payments", "/billing/payment-reviews"]);
  assert.deepEqual(paletteSearch(groups, "tagihan").map(item => item.href), ["/billing/payment-reviews"], "unavailable Tagihan excluded; description match kept");
});

test("an empty query lists every available destination with its area", () => {
  const all = paletteSearch(groups, "  ");
  assert.deepEqual(all.map(item => item.href), ["/identity/account", "/billing/payments", "/billing/payment-reviews"]);
  assert.equal(all[1].area, "Keuangan dan pembukuan");
});

test("Ctrl/⌘+K opens the palette except while typing in a field", () => {
  assert.equal(isPaletteShortcut({ key: "k", ctrlKey: true, metaKey: false, targetTag: "DIV", editable: false }), true);
  assert.equal(isPaletteShortcut({ key: "K", ctrlKey: false, metaKey: true, targetTag: "BODY", editable: false }), true);
  assert.equal(isPaletteShortcut({ key: "k", ctrlKey: true, metaKey: false, targetTag: "INPUT", editable: false }), false);
  assert.equal(isPaletteShortcut({ key: "k", ctrlKey: true, metaKey: false, targetTag: "DIV", editable: true }), false);
  assert.equal(isPaletteShortcut({ key: "k", ctrlKey: false, metaKey: false, targetTag: "DIV", editable: false }), false);
});
