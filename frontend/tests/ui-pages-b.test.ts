import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { createElement as h } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { DataList } from "../src/components/workspace-kit/data-list";
import * as sample from "../src/modules/catalog/PublicSampleWorkspace";

// UI-PAGES-B (Task 3.7): the catalogue, order desk, fulfilment and inventory staff pages on the shared primitives, scored against
// docs/quality/website-ready/ui-standard.md. Each block names the UX finding it pins (findings-ux.md U-032 … U-035, plus the shared
// rules U-003 … U-023 applied to these pages). Geometry that only a browser can measure (h1 x, control heights, overflow, axe) is
// checked with tmp/website-ready/ux/pages-b on the mocked pages; this file pins the source and the stylesheets.
const read = (path: string) => readFileSync(path, "utf8");
const css = (path: string) => existsSync(path) ? read(path).replace(/\/\*[\s\S]*?\*\//g, "") : "";
const source = (name: string) => read("src/modules/" + name);
/** Short failure messages: a whole page module in the assertion output hides the reason. */
const has = (text: string, pattern: RegExp, message?: string) => assert.ok(pattern.test(text), message ?? `expected ${pattern}`);
const lacks = (text: string, pattern: RegExp, message?: string) => { const found = text.match(pattern); assert.ok(!found, `${message ?? "unexpected " + pattern}: found "${found?.[0]}"`); };

const pages: Record<string, string> = {
  "/administration/catalog": "catalog/CatalogAdministration.tsx",
  "/catalog/prices": "catalog/CatalogPriceWorkspace.tsx",
  "/catalog/public": "catalog/PublicSampleWorkspace.tsx",
  "/ordering/reviews": "ordering/OrderReviewWorkspace.tsx",
  "/ordering/substitutions": "ordering/OrderSubstitutionWorkspace.tsx",
  "/fulfillment/preparations": "fulfillment/ShipmentPreparationWorkspace.tsx",
  "/fulfillment/dispatches": "fulfillment/ShipmentDispatchWorkspace.tsx",
  "/fulfillment/shipments": "fulfillment/ShipmentWorkspace.tsx",
  "/fulfillment/courier-assignments": "fulfillment/CourierAssignmentWorkspace.tsx",
  "/fulfillment/receive": "fulfillment/ReceiptWorkspace.tsx",
  "/fulfillment/cases": "fulfillment/CaseWorkspace.tsx",
  "/inventory/sources": "inventory/StockSourceWorkspace.tsx",
  "/inventory/sources/activations": "inventory/StockSourceActivationWorkspace.tsx",
  "/inventory/sources/aliases": "inventory/StockSourceAliasWorkspace.tsx",
  "/inventory/sources/policies": "inventory/StockSourcePolicyWorkspace.tsx",
  "/inventory/sources/revisions": "inventory/StockSourceRevisionWorkspace.tsx",
  "/inventory/sources/suspensions": "inventory/StockSourceSuspensionWorkspace.tsx",
  "/inventory/stock-releases": "inventory/StockReleaseWorkspace.tsx",
  "/inventory/counts": "inventory/StockCountWorkspace.tsx",
  "/inventory/replenishment": "inventory/ReplenishmentWorkspace.tsx",
  "/inventory/source-reconciliations": "inventory/StockSourceReconciliationWorkspace.tsx",
};
/** The module stylesheets only these pages (and their own panels) compose. */
const sheets = [
  "src/modules/inventory/stock-source.module.css", "src/modules/inventory/stock-source-alias.module.css", "src/modules/inventory/stock-source-policy.module.css",
  "src/modules/inventory/stock-source-revision.module.css", "src/modules/inventory/stock-source-suspension.module.css", "src/modules/inventory/stock-count.module.css",
  "src/modules/inventory/replenishment.module.css", "src/modules/catalog/catalog-editor.module.css", "src/modules/fulfillment/fulfillment.module.css",
];

function declarations(sheet: string, property: RegExp): Array<{ property: string; value: string }> {
  const found: Array<{ property: string; value: string }> = [];
  for (const match of sheet.matchAll(/(?:^|[{;\s])([a-z-]+)\s*:\s*([^;{}]+)/g))
    if (property.test(match[1])) found.push({ property: match[1], value: match[2].trim() });
  return found;
}
const scale = new Set(["0", "1px", "2px", "4px", "8px", "12px", "16px", "24px", "32px", "48px", "auto", "inherit"]);
function onScale(value: string): boolean {
  return value.replace(/!important/, "").replace(/calc\((?:[^()]|\([^()]*\))*\)/g, "calc()").trim().split(/\s+/)
    .every(part => scale.has(part.replace(/^-/, "")) || /^var\(--(space-\d+|gutter|control-h|header-h)\)$/.test(part) || /^-?calc\(\)$/.test(part)
      || /^\d+(\.\d+)?(em|ch|%|vh|dvh|fr)$/.test(part));
}
const typeScale = new Set(["12px", "13px", "14px", "15px", "16px", "18px", "20px", "24px", "28px", "inherit", "1em"]);
const radii = /^(0|6px|8px|12px|999px|50%|var\(--radius-(control|card|badge|pill)\))( (0|6px|8px|12px|var\(--radius-(control|card|badge|pill)\))){0,3}$/;
/** The local names a page gives the legacy shared stylesheet (`common`, or `styles` where the page imports only access.module.css). */
function legacyNames(code: string): string[] {
  return [...code.matchAll(/import\s+(\w+)\s+from\s+['"][^'"]*administration\/access\.module\.css['"]/g)].map(match => match[1]);
}

test("U-003: every page in scope renders its one h1 through PageHeader; the old header, brand link and eyebrow are gone", () => {
  for (const [route, file] of Object.entries(pages)) {
    const code = source(file);
    has(code, /from ['"]@\/components\/workspace-kit\/page-header['"]/, `${route}: imports PageHeader`);
    has(code, /<PageHeader\b/, `${route}: renders PageHeader`);
    lacks(code, /<h1\b/, `${route}: no hand-made h1`);
    for (const name of legacyNames(code)) lacks(code, new RegExp(`\\b${name}\\.(header|brand)\\b`), `${route}: no legacy header`);
    lacks(code, /\beyebrow\b|Pengelolaan persediaan/, `${route}: no eyebrow above the title`);
  }
});

test("U-004/U-008/U-011/U-012/U-013/U-022: the legacy look-alikes give way to Section, DataList, Badge, Button, StateView and Facts", () => {
  // `.success` stays allowed: saved-result panels keep their h2 ("Permintaan tersimpan"), which rehearsals wait for by role heading.
  const retired = /\.(accountSection|queue|queueHeader|queueList|queueRow|selectedRow|state|empty|status|facts|primary|secondary|danger|alert)\b/;
  for (const [route, file] of Object.entries(pages)) {
    const code = source(file);
    for (const name of legacyNames(code)) lacks(code, new RegExp(`\\b${name}${retired.source}`), `${route}: legacy class`);
    lacks(code, /className=\{styles\.(badge|facts)\}/, `${route}: page-local badge or facts`);
    // A list's own "Belum ada …" paragraph beside the list (help text under a select keeps its help class and is allowed).
    lacks(code, /items\.length === 0 && <p>|!\w+(\.\w+)?\.items\.length && <p>/, `${route}: hand-made empty text next to the list (one state per region)`);
  }
});

test("U-012/U-033/U-035: queues and record lists render through DataList (one state per region, pager hidden when it fits)", () => {
  for (const file of ["ordering/OrderReviewWorkspace.tsx", "fulfillment/ShipmentPreparationWorkspace.tsx", "fulfillment/ShipmentDispatchWorkspace.tsx",
    "fulfillment/ShipmentWorkspace.tsx", "fulfillment/CourierAssignmentWorkspace.tsx", "fulfillment/CaseWorkspace.tsx", "catalog/PublicSampleWorkspace.tsx",
    "catalog/CatalogAdministration.tsx", "catalog/CatalogPriceWorkspace.tsx", "inventory/StockSourceWorkspace.tsx", "inventory/StockSourceAliasWorkspace.tsx",
    "inventory/StockSourcePolicyWorkspace.tsx", "inventory/StockSourceRevisionWorkspace.tsx", "inventory/StockSourceSuspensionWorkspace.tsx", "inventory/StockCountWorkspace.tsx"]) {
    has(source(file), /<DataList\b/, `${file}: DataList`);
  }
});

test("U-033: order desk rows open on a click anywhere in the row and keep their accessible name; the status is a Badge on the right", () => {
  const review = source("ordering/OrderReviewWorkspace.tsx");
  has(review, /<DataList\b[\s\S]*onSelect=/, "reviews: whole-row open");
  has(review, /openLabel=\{item => 'Tinjau pesanan '/, "reviews: the row keeps the name 'Tinjau pesanan PSN-…'");
  has(review, /renderBadge=\{item => <Badge\b/, "reviews: status badge");
  lacks(review, /renderActions=\{item => <Button[^>]*>Tinjau pesanan/, "reviews: no separate 'Tinjau pesanan' button");
  // The stage switch is a set of view tabs, not primary/secondary buttons.
  has(review, /<Tabs\b[^>]*label="Keputusan sesuai penugasan"/, "reviews: stage tabs");
  const html = renderToStaticMarkup(h(DataList<{ id: string; name: string }>, { label: "Pesanan", items: [{ id: "1", name: "Cabang Uji" }], getKey: item => item.id,
    renderTitle: item => item.name, renderBadge: () => h("span", { className: "status-badge" }, "Menunggu"), onSelect: () => undefined, openLabel: () => "Tinjau pesanan PSN-1" }));
  has(html, /<button type="button" class="data-list-open" aria-label="Tinjau pesanan PSN-1"/, "DataList: openLabel names the row button");
  has(html, /data-list-title">Cabang Uji<\/span><span class="status-badge">/, "DataList: the badge follows the title");
});

test("U-033/U-035/U-003: refresh is a header or section action, never loose in the page body", () => {
  const refresh: Record<string, string[]> = {
    "ordering/OrderReviewWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat pesanan terbaru"],
    "ordering/OrderSubstitutionWorkspace.tsx": ["Muat ulang penggantian"],
    "fulfillment/ShipmentPreparationWorkspace.tsx": ["Muat pesanan terbaru"],
    "fulfillment/ShipmentDispatchWorkspace.tsx": ["Muat persiapan terbaru"],
    "fulfillment/ShipmentWorkspace.tsx": ["Muat kiriman terbaru"],
    "fulfillment/CourierAssignmentWorkspace.tsx": ["Muat penugasan terbaru"],
    "fulfillment/CaseWorkspace.tsx": ["Muat keluhan terbaru"],
    "catalog/CatalogAdministration.tsx": ["Muat ulang sesi dan akses"],
    "catalog/CatalogPriceWorkspace.tsx": ["Muat ulang sesi dan akses"],
    "inventory/StockSourceWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat ulang pengajuan"],
    "inventory/StockSourceActivationWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat ulang riwayat"],
    "inventory/StockSourceAliasWorkspace.tsx": ["Muat ulang sesi dan akses"],
    "inventory/StockSourcePolicyWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat ulang riwayat"],
    "inventory/StockSourceRevisionWorkspace.tsx": ["Muat ulang sesi dan akses"],
    "inventory/StockSourceSuspensionWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat ulang riwayat"],
    "inventory/StockReleaseWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat ulang riwayat"],
    "inventory/StockCountWorkspace.tsx": ["Muat ulang sesi dan akses", "Muat ulang riwayat"],
    "inventory/ReplenishmentWorkspace.tsx": ["Muat riwayat terbaru"],
  };
  for (const [file, labels] of Object.entries(refresh)) {
    const code = source(file);
    for (const label of labels) has(code, new RegExp(`(actions|secondaryAction)=\\{(?:(?!<Section\\b|</Section>)[\\s\\S]){0,700}?>${label}</Button>`), `${file}: "${label}" is a header/section action`);
  }
});

test("U-034: a placeholder owner name (\"… (nama belum tersedia)\") is never the row title", () => {
  assert.equal(sample.sampleOwnerTitle("Toko Melati"), "Pengajuan Toko Melati");
  assert.equal(sample.sampleOwnerTitle("Toko 5 (nama belum tersedia)"), "Pengajuan contoh katalog");
  assert.equal(sample.sampleOwnerTitle("Organisasi 4 · gudang 9 (nama belum tersedia)"), "Pengajuan contoh katalog");
  const proposal = { proposalId: "901", ownProposal: false, ownerLabel: "Toko 5 (nama belum tersedia)", reason: "Contoh barang umum", proposedAt: "2026-10-21T02:00:00+00:00",
    adds: [{ familyId: "7", familyName: "Pensil 2B" }], removes: [] };
  const html = renderToStaticMarkup(h("ul", null, h(sample.ProposalReview, { proposal, canDecide: true, busy: false, onDecide: () => undefined })));
  lacks(html, /data-list-title">[^<]*nama belum tersedia/, "the placeholder is not the title");
  has(html, /data-list-title">Pengajuan contoh katalog</, "a real title");
  has(html, /Pemilik katalog: Toko 5 \(nama belum tersedia\)/, "the owner stays visible as meta");
  has(html, /class="status-badge"[^>]*>Menunggu tinjauan</, "status through Badge");
});

test("U-035: an allowed recipient without a QR link sees the standard state with its next step, not a bare sentence", () => {
  const code = source("fulfillment/ReceiptWorkspace.tsx");
  has(code, /<StateView kind="empty" title="Pindai QR dari kurir"/, "receive: empty state with the next step");
  has(code, />Periksa kiriman</, "receive: the real control stays");
});

test("U-032/U-023/U-006/U-009/U-010: these pages' stylesheets use tokens only — no hex, no off-scale px, no stripes, no outline", () => {
  for (const file of sheets) {
    const sheet = css(file);
    if (!sheet) continue;
    lacks(sheet, /#[0-9a-f]{3,8}\b/i, `${file}: hex colour`);
    lacks(sheet, /(?<![\w-])(white|black)(?![\w-])/, `${file}: named colour`);
    lacks(sheet, /\boutline\s*:/, `${file}: outline`);
    lacks(sheet, /border(-(top|left|right|bottom))?\s*:\s*[2-9]px\s+solid/, `${file}: stripe`);
    lacks(sheet, /\beyebrow\b/, `${file}: eyebrow`);
    for (const { property, value } of declarations(sheet, /^(padding|margin)(-[a-z]+)*$|^(row-|column-)?gap$|^inset$/))
      assert.ok(onScale(value), `${file}: ${property}: ${value}`);
    for (const { value } of declarations(sheet, /^font-size$/)) assert.ok(typeScale.has(value), `${file}: font-size ${value}`);
    for (const { value } of declarations(sheet, /^font-weight$/)) assert.match(value, /^(400|600|700)$/, `${file}: font-weight ${value}`);
    for (const { value } of declarations(sheet, /^border(-[a-z]+)*-radius$/)) assert.match(value, radii, `${file}: radius ${value}`);
  }
});

test("U-032: inventory pages share the standard badge and state; no square page badge or private palette survives", () => {
  for (const file of ["src/modules/inventory/stock-source.module.css", "src/modules/inventory/stock-source-alias.module.css",
    "src/modules/inventory/stock-source-policy.module.css", "src/modules/inventory/stock-source-revision.module.css", "src/modules/inventory/stock-source-suspension.module.css"])
    lacks(css(file), /\.badge\b/, `${file}: page badge`);
  for (const file of Object.values(pages).filter(file => file.startsWith("inventory/"))) has(source(file), /<Badge\b|<StateView\b|<DataList\b/, `${file}: standard badge, state or list`);
});
