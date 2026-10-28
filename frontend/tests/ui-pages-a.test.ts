import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import * as cartBranchModule from "../src/modules/ordering/cart-branch";
import type { OrderContextOption } from "../src/modules/ordering/order-quote-client";
import { createElement as h } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Section } from "../src/components/workspace-kit/section";
import { StateView } from "../src/components/workspace-kit/state-view";
import { Tabs } from "../src/components/workspace-kit/tabs";
import { buyerCategoryLabel } from "../src/modules/catalog/catalog-buyer-client";

// UI-PAGES-A (Task 3.6): the storefront and buyer pages on the shared primitives, scored against
// docs/quality/website-ready/ui-standard.md. Each block names the UX finding it pins (findings-ux.md U-024 … U-031, plus the
// shared rules U-003 … U-023 applied to these pages). Geometry that only a browser can measure (h1 x, control heights,
// overflow, axe) is checked with tmp/website-ready/ux/pages-a on the mocked pages; this file pins the source and the CSS.
const read = (path: string) => readFileSync(path, "utf8");
const css = (path: string) => existsSync(path) ? read(path).replace(/\/\*[\s\S]*?\*\//g, "") : "";
const source = (name: string) => read("src/modules/" + name);
/** Short failure messages: a whole page module in the assertion output hides the reason. */
const has = (text: string, pattern: RegExp, message?: string) => assert.ok(pattern.test(text), message ?? `expected ${pattern}`);
const lacks = (text: string, pattern: RegExp, message?: string) => { const found = text.match(pattern); assert.ok(!found, `${message ?? "unexpected " + pattern}: found "${found?.[0]}"`); };

const pages: Record<string, string> = {
  "/shop": "catalog/BuyerCatalog.tsx",
  "/katalog": "catalog/PublicCatalog.tsx",
  "/cart": "ordering/CartWorkspace.tsx",
  "/ordering/checkout": "ordering/OrderCheckoutWorkspace.tsx",
  "/ordering/orders": "ordering/OrderHistoryWorkspace.tsx",
  "/ordering/requests": "ordering/BranchRequestWorkspace.tsx",
  "/identity/account": "identity/IdentityAccount.tsx",
  "/identity/login": "identity/IdentityLogin.tsx",
};
/** The module stylesheets these pages (and the store frame around them) own. */
const sheets = [
  "src/modules/catalog/storefront-catalog.module.css", "src/modules/catalog/buyer-catalog.module.css", "src/modules/catalog/public-catalog.module.css",
  "src/modules/ordering/cart.module.css", "src/modules/ordering/order-checkout.module.css", "src/modules/ordering/order-draft.module.css",
  "src/modules/ordering/branch-request.module.css", "src/modules/identity/identity-login.module.css", "src/components/store/store-frame.module.css",
];

function declarations(sheet: string, property: RegExp): Array<{ property: string; value: string }> {
  const found: Array<{ property: string; value: string }> = [];
  for (const match of sheet.matchAll(/(?:^|[{;\s])([a-z-]+)\s*:\s*([^;{}]+)/g))
    if (property.test(match[1])) found.push({ property: match[1], value: match[2].trim() });
  return found;
}
const scale = new Set(["0", "1px", "2px", "4px", "8px", "12px", "16px", "24px", "32px", "48px", "auto", "inherit"]);
function onScale(value: string): boolean {
  return value.replace(/!important/, "").replace(/calc\((?:[^()]|\([^()]*\))*\)/g, "calc()").replace(/env\([^)]*\)/g, "env()").trim().split(/\s+/)
    .every(part => scale.has(part.replace(/^-/, "")) || /^var\(--(space-\d+|gutter|control-h|header-h)\)$/.test(part) || /^-?calc\(\)$/.test(part)
      || part === "env()" || /^\d+(\.\d+)?(em|ch|%|vh|dvh|fr)$/.test(part));
}
const typeScale = new Set(["12px", "13px", "14px", "15px", "16px", "18px", "20px", "24px", "28px", "inherit", "1em"]);
const radii = /^(0|6px|8px|12px|999px|50%|var\(--radius-(control|card|badge|pill)\))( (0|6px|8px|12px|var\(--radius-(control|card|badge|pill)\))){0,3}$/;

test("U-003: every page in scope renders its one h1 through PageHeader; the old header classes are gone", () => {
  for (const [route, file] of Object.entries(pages)) {
    const code = source(file);
    has(code, /from ['"](@\/components|\.\.\/\.\.\/components)\/workspace-kit\/page-header['"]/, `${route}: imports PageHeader`);
    has(code, /<PageHeader\b/, `${route}: renders PageHeader`);
    lacks(code, /common\.header|styles\.(storeHeading|heading|title|accountHeader|detailHeading)\b/, `${route}: no legacy header class`);
  }
});

test("U-023/U-006/U-005/U-007: these pages' stylesheets use tokens only, the spacing scale, the radii and the type scale", () => {
  // Every problem is listed at once, so the report shows the whole distance to the standard.
  const problems: string[] = [];
  for (const file of sheets) {
    if (!existsSync(file)) { problems.push(`${file}: missing`); continue; }
    const sheet = css(file);
    for (const hex of sheet.match(/#[0-9a-f]{3,8}\b/gi) ?? []) problems.push(`${file}: hex ${hex}`);
    for (const rgb of sheet.match(/\brgba?\([^)]*\)/gi) ?? []) problems.push(`${file}: ${rgb}`);
    for (const { property, value } of declarations(sheet, /^(padding|margin)(-[a-z]+)*$|^(row-|column-)?gap$|^inset$/))
      if (!onScale(value)) problems.push(`${file}: ${property}: ${value}`);
    for (const { value } of declarations(sheet, /^border(-[a-z-]+)?-radius$/)) if (!radii.test(value)) problems.push(`${file}: border-radius ${value}`);
    for (const { value } of declarations(sheet, /^font-size$/)) if (!typeScale.has(value)) problems.push(`${file}: font-size ${value}`);
    for (const { value } of declarations(sheet, /^font-weight$/)) if (!/^(400|600|700|inherit)$/.test(value)) problems.push(`${file}: font-weight ${value}`);
    if (/font-family:\s*var\(--font-display\)/.test(sheet)) problems.push(`${file}: display type (landing page only)`);
    if (/(^|[;{\s])outline\s*:(?!\s*none)/.test(sheet)) problems.push(`${file}: own outline`);
  }
  assert.deepEqual(problems, []);
});

test("U-004/U-011: the pages use Button and StateView, not the legacy access buttons, alerts or states", () => {
  for (const [route, file] of Object.entries(pages)) {
    const code = source(file);
    lacks(code, /\b(common|shared)\.(primary|secondary|danger|alert|state|empty|accountSection|header)\b/, `${route}: no legacy common.* look-alike`);
    lacks(code, /\bstyles\.(primary|secondary|stateAction|accessState|signIn)\b/, `${route}: no page-local button or state look-alike`);
  }
});

test("U-024: guest and signed-in catalogues share one grid, card, chip row and toolbar stylesheet", () => {
  const buyer = source("catalog/BuyerCatalog.tsx"), guest = source("catalog/PublicCatalog.tsx"), parts = source("catalog/PublicCatalogParts.tsx");
  for (const [name, code] of [["BuyerCatalog", buyer], ["PublicCatalog", guest]] as const)
    has(code, /import catalog from ['"]\.\/storefront-catalog\.module\.css['"]/, `${name} imports the shared catalogue sheet`);
  has(parts, /import catalog from ['"]\.\/storefront-catalog\.module\.css['"]/, "the guest card uses the shared card");
  for (const name of ["grid", "card", "chips"]) {
    has(buyer, new RegExp(`catalog\\.${name}\\b`), `signed-in catalogue uses catalog.${name}`);
    has(guest + parts, new RegExp(`catalog\\.${name}\\b`), `guest catalogue uses catalog.${name}`);
  }
  const shared = css("src/modules/catalog/storefront-catalog.module.css");
  has(shared, /\.grid\s*\{[^}]*grid-template-columns:\s*repeat\(auto-fill,\s*minmax\(200px,\s*1fr\)\)/, "5 columns at 1280 (standard §3.3)");
  has(shared, /\.card\s*\{[^}]*border-radius:\s*var\(--radius-card\)/);
  const buyerSheet = css("src/modules/catalog/buyer-catalog.module.css"), guestSheet = css("src/modules/catalog/public-catalog.module.css");
  for (const [name, sheet] of [["buyer-catalog", buyerSheet], ["public-catalog", guestSheet]] as const)
    lacks(sheet, /(^|\})\s*\.(grid|card|chips|toolbar|breadcrumb)\s*\{/, `${name} no longer defines its own grid, card, chips, toolbar or breadcrumb`);
});

test("U-025: one product page layout (photo 5 / facts and buy box 7), one level of boxing, category by name", () => {
  const buyer = source("catalog/BuyerCatalog.tsx"), guest = source("catalog/PublicCatalog.tsx");
  const shared = css("src/modules/catalog/storefront-catalog.module.css");
  has(shared, /\.product\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*5fr\)\s+minmax\(0,\s*7fr\)/);
  has(buyer, /className=\{catalog\.product\}/, "the signed-in product page uses the shared layout");
  has(guest, /className=\{catalog\.product\}/, "the guest product page uses the shared layout");
  lacks(buyer, /\{item\.categoryCode\}/, "a category is shown by its name, never its code");
  const sheet = css("src/modules/catalog/buyer-catalog.module.css");
  lacks(sheet, /\.packagePage\s*\{[^}]*border/, "the product section draws no outer card");
  lacks(sheet, /\.buyerPhotoFrame\s*\{[^}]*border:/, "the photo frame is not a second bordered card");
  lacks(css("src/modules/ordering/cart.module.css"), /\.buyBox\s*\{[^}]*border:/, "the buy box is not a third bordered card");
});

const option = (branchId: string): OrderContextOption => ({ context: { organizationId: "10", branchId, divisionId: null }, organizationName: "BNI", branchName: "Cabang " + branchId, divisionName: null });
const memory = (): Pick<Storage, "getItem" | "setItem"> => { const values = new Map<string, string>(); return { getItem: key => values.get(key) ?? null, setItem: (key, value) => void values.set(key, value) }; };

test("U-026: the cart opens the only branch the buyer may order for; with several it still waits for the buyer", () => {
  const cartViewBranch = (cartBranchModule as Record<string, unknown>).cartViewBranch as ((actorId: string, options: OrderContextOption[], store?: Pick<Storage, "getItem" | "setItem">) => OrderContextOption | null) | undefined;
  assert.equal(typeof cartViewBranch, "function", "cart-branch exports cartViewBranch");
  const store = memory();
  assert.equal(cartViewBranch!("7", [option("2")], store)?.context.branchId, "2", "a single branch is preselected for viewing");
  assert.equal(cartViewBranch!("7", [option("2"), option("3")], store), null, "several branches: the buyer chooses");
  assert.equal(cartViewBranch!("7", [], store), null);
  cartBranchModule.rememberCartBranch("7", option("3").context, store);
  assert.equal(cartViewBranch!("7", [option("2"), option("3")], store)?.context.branchId, "3", "the last explicit choice wins");
  assert.equal(store.getItem("unit-toko.cart-branch.8"), null);
  assert.equal(cartViewBranch!("8", [option("4")], store)?.context.branchId, "4");
  assert.equal(store.getItem("unit-toko.cart-branch.8"), null, "viewing never records a choice the buyer did not make");
  assert.equal(cartBranchModule.cartBranch("7", [option("2")], memory()), null, "the add-to-cart rule is unchanged: it never picks a branch");
});

test("U-026: /cart uses PageHeader and Section; an empty or unselected cart is a centred state with a next action", () => {
  const code = source("ordering/CartWorkspace.tsx");
  has(code, /<Section\b/);
  has(code, /cartViewBranch\(/, "the cart page preselects through the pure helper");
  const centred = code.match(/<StateView\b[^>]*align="center"/g) ?? [];
  assert.ok(centred.length >= 2, "the empty cart and the unselected cart are centred states");
  has(css("src/app/foundation.css"), /\.state-view\[data-align="center"\]\{[^}]*justify-items:center[^}]*text-align:center/);
});

test("U-027: /ordering/orders shows DataList rows in one section with the filter in a Toolbar and one primary per region", () => {
  const code = source("ordering/OrderHistoryWorkspace.tsx");
  has(code, /<DataList\b/); has(code, /<Toolbar\b/); has(code, /<Section\b/);
  lacks(code, /styles\.(orderCards?|orderCardHead|orderCardFoot|filters)\b/, "no hand-built order cards or bordered details");
  const rows = code.slice(code.indexOf("<DataList"), code.indexOf("/>", code.indexOf("renderActions")));
  lacks(rows, /variant=["']primary["']/, "rows repeat no primary button");
  lacks(code, /formatMoney\(|orderAmount\(/, "buyer money format, no ,00");
});

test("U-028: checkout uses the standard h1, a 720 px form column and the summary beside it", () => {
  const sheet = css("src/modules/ordering/order-checkout.module.css");
  lacks(sheet, /max-width:\s*1360px/, "no private 1360 px container");
  has(sheet, /\.body\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)\s+360px/, "form 1fr, summary 360 (standard §3.1)");
  has(sheet, /\.form\s*\{[^}]*max-width:\s*var\(--content-narrow\)/, "fields in the 720 px column");
  has(source("ordering/OrderDeliveryPanel.tsx"), /<Field\b/, "delivery fields use Field");
});

test("U-029: the store header is one landmark with a 64 px main bar and one button treatment", () => {
  const frame = read("src/components/store/store-frame.tsx"), sheet = css("src/components/store/store-frame.module.css");
  const header = frame.indexOf("<header"), utility = frame.indexOf("styles.utility");
  assert.ok(header >= 0 && utility > header, "the utility links sit inside <header>");
  has(sheet, /\.mainBar\s*\{[^}]*min-height:\s*var\(--header-h\)/);
  has(sheet, /\.utility \.inner\s*\{[^}]*min-height:\s*32px/, "utility row 32 px (standard §3.1)");
  for (const name of ["workspace", "iconLink", "accountButton"]) lacks(sheet, new RegExp(`\\.${name}\\s*\\{[^}]*border:`), `.${name} draws no border of its own`);
  has(frame, /buttonClassName\(\{ variant: 'tertiary'/, "header actions share the tertiary Button look");
  has(frame, /buttonClassName\(\{ variant: 'primary'/, "Masuk is the header's one primary");
});

test("U-030: sign-in uses the shared tokens, Button and Field on a 720 px panel", () => {
  const code = source("identity/IdentityLogin.tsx"), sheet = css("src/modules/identity/identity-login.module.css");
  has(code, /<Field\b/, "invitation inputs use Field");
  has(code, /buttonClassName\(|<Button\b/, "links and buttons use the Button look");
  has(sheet, /\.sheet\s*\{[^}]*width:\s*min\(100%,\s*var\(--content-narrow\)\)/);
  lacks(sheet, /--(dock|stage|label|paper|ink|marker|kraft-text|rule)\s*:/, "no private palette");
  lacks(sheet, /button\[type="submit"\]\s*\{/, "the submit button keeps the shared look");
});

test("U-031: /shop toolbar on one row through Toolbar; the result line omits the viewer; no pager under 0 results", () => {
  const code = source("catalog/BuyerCatalog.tsx");
  has(code, /<Toolbar\b/);
  lacks(code, /\{account\.name\}\s*·/, "the result line never names the viewer");
  has(code, /items\.length > 0 && <nav className=\{catalog\.pagination\}/, "the pager hides when nothing matches");
  const shared = css("src/modules/catalog/storefront-catalog.module.css");
  has(shared, /\.view button\[aria-pressed="true"\]\s*\{[^}]*background:\s*var\(--secondary\)/, "the selected view segment is --secondary, not the primary fill");
  lacks(shared, /\.sort select\s*\{[^}]*(?<!min-)width:\s*\d/, "the sort select is never narrower than its longest option");
});

test("U-018/U-013/U-022: request pages use Tabs for their views, Badge for status and Facts for the record", () => {
  const code = source("ordering/BranchRequestWorkspace.tsx"), facts = source("ordering/BranchRequestFacts.tsx");
  has(code, /<Tabs\b/);
  lacks(code, /tab === 'new' \? common\.primary/, "the current tab is not a primary button");
  has(code, /<Badge\b/); lacks(code + facts, /styles\.badge\b/);
  has(facts, /<Facts\b/); lacks(facts, /styles\.meta\b/);
});

test("kit additions for these pages: Section keeps a region name, StateView centres a storefront state, view Tabs can be blocked", () => {
  const section = renderToStaticMarkup(h(Section, { title: "Pesanan yang diajukan", label: "Daftar pesanan saya", busy: true }, "isi"));
  has(section, /<section class="ui-section" data-variant="card" aria-label="Daftar pesanan saya" aria-busy="true">/, "the region keeps its old name");
  has(section, /<h2 id="section-pesanan-yang-diajukan">Pesanan yang diajukan<\/h2>/);
  lacks(renderToStaticMarkup(h(Section, { title: "Barang di keranjang" })), /aria-label=/, "without a label the h2 names the region");
  const empty = renderToStaticMarkup(h(StateView, { kind: "empty", align: "center", title: "Keranjang masih kosong", action: h("a", { href: "/shop" }, "Mulai belanja") }));
  has(empty, /data-kind="empty" data-align="center"/); has(empty, /<h2 class="state-view-title">Keranjang masih kosong<\/h2>/, "a full-page state titles the page body");
  has(renderToStaticMarkup(h(StateView, { kind: "empty", title: "Belum ada" })), /<h3 class="state-view-title">/, "inside a section it stays an h3");
  const tabs = renderToStaticMarkup(h(Tabs, { label: "Bagian permintaan", items: [{ label: "Buat permintaan", value: "new" }, { label: "Riwayat permintaan", value: "history" }], current: "new", disabled: true }));
  assert.equal((tabs.match(/disabled=""/g) ?? []).length, 2); has(tabs, /aria-pressed="true"[^>]*>Buat permintaan<\/button>/);
});

test("U-025: a category shows by name — the server's name, else the code in sentence case", () => {
  assert.equal(buyerCategoryLabel("PANTRY", [{ code: "PANTRY", name: "Pantry kopi dan teh", count: 9 }]), "Pantry kopi dan teh");
  assert.equal(buyerCategoryLabel("PANTRY"), "Pantry");
  assert.equal(buyerCategoryLabel("AIR-MINUMAN"), "Air minuman");
  assert.equal(buyerCategoryLabel("ALAT_TULIS"), "Alat tulis");
});

// Review round (the rules UI-PAGES-B's review enforced): no single-tab Tabs, an error never sits above an empty card, and the
// busy state shows on the button that was pressed.
test("review: Tabs only when there is more than one view", () => {
  has(source("ordering/BranchRequestWorkspace.tsx"), /views\.length > 1 &&[^\n]*<Tabs\b/, "one view needs no tabs");
});

test("review: the cart shows a failed read as the error, not as an empty cart or an empty branch list", () => {
  const code = source("ordering/CartWorkspace.tsx");
  has(code, /options\.length === 0\) return error !== null \? <StateView kind="error"/, "a failed branch read is an error state");
  has(code, /const failed = error !== null && rows\.length === 0/, "a failed change on an empty cart shows the error alone");
});

test("review: the pressed button carries the busy state", () => {
  has(source("ordering/AddToCartPanel.tsx"), /<Button variant="primary"[^>]*busy=\{busy\}/);
  has(source("ordering/OrderDeliveryPanel.tsx"), /<Button type="submit" variant="primary"[^>]*busy=\{busy\}/);
  has(source("catalog/BuyerCatalog.tsx"), /busy=\{detailBusy === item\.skuId\}/);
  const checkout = source("ordering/OrderCheckoutWorkspace.tsx");
  has(checkout, /busy=\{acting === 'prepare'\}/); has(checkout, /busy=\{acting === 'prepare:' \+ item\.group\.groupKey\}/); has(checkout, /busy=\{acting === 'submit:' \+ item\.group\.groupKey\}/);
  const orders = source("ordering/OrderHistoryWorkspace.tsx");
  has(orders, /busy=\{acting === 'again:' \+ id\}/); has(orders, /busy=\{acting === 'open:' \+ id\}/);
  has(source("ordering/BranchRequestWorkspace.tsx"), /busy=\{acting === 'open:' \+ item\.id\}/);
});
