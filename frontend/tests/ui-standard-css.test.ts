import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { contrastRatio, readTokens } from "../src/lib/design/contrast";

// Website-ready UI standard (docs/quality/website-ready/ui-standard.md), enforced on the shared stylesheets that ~129 pages
// compose. Each block names the UX finding it pins (findings-ux.md, U-001 … U-023).
const read = (path: string) => readFileSync(path, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
const globals = read("src/app/globals.css");
const foundation = read("src/app/foundation.css");
const palette = read("src/modules/workspace-palette.module.css");
const access = read("src/modules/administration/access.module.css");
const identity = read("src/modules/identity/identity.module.css");
const shared: Record<string, string> = { "foundation.css": foundation, "workspace-palette.module.css": palette, "access.module.css": access, "identity.module.css": identity };
const tokens = readTokens(readFileSync("src/app/globals.css", "utf8"));
/** Legacy element defaults skip shared primitives ([data-kit]) without raising their specificity. */
const G = ":where(:not([data-kit], [data-kit] *))";

/** Every `property: value` declaration in a stylesheet (media blocks included). */
function declarations(css: string, property: RegExp): Array<{ property: string; value: string }> {
  const found: Array<{ property: string; value: string }> = [];
  for (const match of css.matchAll(/(?:^|[{;\s])([a-z-]+)\s*:\s*([^;{}]+)/g))
    if (property.test(match[1]) && !match[2].includes("//")) found.push({ property: match[1], value: match[2].trim() });
  return found;
}
/** The declarations of the first rule whose selector list matches exactly. */
function rule(css: string, selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/\s+/g, "\\s*");
  const match = new RegExp(`(?:^|[}\\n])\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css);
  assert.ok(match, `rule ${selector} exists`);
  return match[1];
}
const moduleSheets = (() => {
  const files: string[] = [];
  (function walk(dir: string) { for (const name of readdirSync(dir)) { const path = `${dir}/${name}`;
    if (statSync(path).isDirectory()) walk(path); else if (path.endsWith(".module.css")) files.push(path); } })("src");
  return files;
})();

test("U-001/U-014 tokens: the standard's new tokens exist with their exact values", () => {
  const expected: Record<string, string> = {
    "control-border": "oklch(0.62 0.02 225)", input: "oklch(0.62 0.02 225)", "primary-hover": "oklch(0.46 0.08 207.38)",
    "space-1": "4px", "space-2": "8px", "space-3": "12px", "space-4": "16px", "space-6": "24px", "space-8": "32px", "space-12": "48px",
    "control-h": "44px", "control-h-compact": "36px", "row-h": "48px", "row-h-compact": "36px",
    "radius-control": "8px", "radius-card": "12px", "radius-badge": "6px", "radius-pill": "999px",
    "content-narrow": "720px", "content-default": "1080px", "content-wide": "1280px", gutter: "16px", "header-h": "56px",
  };
  for (const [name, value] of Object.entries(expected)) assert.equal(tokens[name], value, `--${name}`);
  assert.match(globals, /--overlay:color-mix\(in oklab,var\(--foreground\) 45%,transparent\)/);
  assert.match(globals, /@media \(min-width:768px\)\{:root\{--gutter:24px\}\}/);
  assert.match(globals, /@media \(min-width:1280px\)\{:root\{--gutter:32px\}\}/);
  assert.match(globals, /@media \(min-width:1024px\)\{:root\{--header-h:64px\}\}/);
});

test("U-014: control boundaries reach 3:1 and the primary hover keeps white text at 4.5:1", () => {
  for (const surface of ["card", "background"]) assert.ok(contrastRatio(tokens["control-border"], tokens[surface]) >= 3, `--control-border on --${surface}`);
  assert.ok(contrastRatio(tokens["primary-foreground"], tokens["primary-hover"]) >= 4.5);
});

test("U-001: one primary colour; the legacy palette is a pure alias of the standard tokens", () => {
  assert.doesNotMatch(palette, /#[0-9a-f]{3,8}\b/i, "no hex left in the legacy palette");
  for (const [legacy, token] of [["ink", "foreground"], ["canvas", "background"], ["surface", "card"], ["accent", "ring"], ["text-muted", "muted-foreground"],
    ["border", "border"], ["border-input", "control-border"], ["border-interactive", "control-border"], ["surface-selected", "secondary"],
    ["critical-text", "tone-danger-foreground"], ["success-text", "tone-success-foreground"], ["caution-text", "tone-warning-foreground"]])
    assert.match(palette, new RegExp(`--workspace-${legacy}:\\s*var\\(--${token}\\);`), `--workspace-${legacy} → --${token}`);
  for (const [name, css] of [["access", access], ["identity", identity]] as const) {
    assert.match(rule(css, ".primary"), /background:\s*var\(--primary\)/, `${name} .primary fill`);
    assert.match(rule(css, ".primary"), /color:\s*var\(--primary-foreground\)/, `${name} .primary text`);
    assert.match(css, /\.primary:hover:not\(:disabled\)\s*\{[^}]*background:\s*var\(--primary-hover\)/, `${name} .primary hover`);
  }
});

test("U-002: heading defaults are element rules after preflight, with the standard sizes", () => {
  assert.doesNotMatch(foundation, /:where\(h1,h2,h3,h4\)/, "the zero-specificity heading rule lost to preflight");
  const base = /@layer base\{([\s\S]*?)\n\}/.exec(foundation)?.[1] ?? "";
  assert.match(base, /\bh1\{font-size:28px;line-height:34px;font-weight:700;letter-spacing:-\.02em\}/);
  assert.match(base, /\bh2\{font-size:20px;line-height:28px;font-weight:600;letter-spacing:-\.01em\}/);
  assert.match(base, /\bh3\{font-size:16px;line-height:24px;font-weight:600\}/);
  assert.match(base, /@media \(max-width:639px\)\{h1\{font-size:24px;line-height:30px\}h2\{font-size:18px;line-height:26px\}\}/);
});

test("U-003/U-007: shared page headings follow the type scale (no clamp, no divider under the header)", () => {
  for (const [name, css] of [["access", access], ["identity", identity]] as const) {
    assert.doesNotMatch(css, /font-size:\s*clamp|font:\s*\d+\s+clamp/, `${name} has no fluid title size`);
    assert.match(rule(css, `.page h1${G}`), /font-size:\s*28px/, `${name} h1 28px`);
  }
  assert.doesNotMatch(rule(access, ".header"), /border-bottom/, "no divider under the header");
});

test("U-004: shared buttons are exactly 44 px (36 compact), radius 8, 14/600, and never dimmed with opacity", () => {
  for (const [name, css] of [["access", access], ["identity", identity]] as const) {
    const body = rule(css, ".primary, .secondary" + (name === "access" ? ", .danger" : ""));
    assert.match(body, /height:\s*var\(--control-h\)/, `${name} button height`);
    assert.match(body, /padding:\s*0 var\(--space-4\)/, `${name} button padding`);
    assert.match(body, /border-radius:\s*var\(--radius-control\)/, `${name} button radius`);
    assert.match(body, /font-size:\s*14px/); assert.match(body, /font-weight:\s*600/); assert.match(body, /white-space:\s*nowrap/);
    assert.doesNotMatch(css, /opacity:\s*\.\d/, `${name} disabled state uses muted tokens, not opacity`);
  }
  for (const selector of [".review-confirm", ".review-cancel", ".outcome-action", ".ui-button"]) assert.match(rule(foundation, selector), /height:var\(--control-h\)/, selector);
  assert.match(rule(foundation, ".list-table-open"), /height:var\(--control-h-compact\)/);
  assert.match(rule(foundation, ".quantity-stepper-controls button"), /height:var\(--control-h\)/);
});

const allowedRadius = /^(0|var\(--radius-(control|card|badge|pill)\)|50%|6px|8px|12px|999px|inherit)$/;
test("U-005: shared stylesheets use only the standard radii", () => {
  for (const [name, css] of Object.entries(shared))
    for (const { value } of declarations(css, /^border(-[a-z]+)*-radius$/)) assert.match(value, allowedRadius, `${name}: border-radius ${value}`);
});

const scale = new Set(["0", "1px", "2px", "4px", "8px", "12px", "16px", "24px", "32px", "48px", "auto", "inherit"]);
function onScale(value: string): boolean {
  return value.replace(/!important/, "").replace(/calc\((?:[^()]|\([^()]*\))*\)/g, "calc()").trim().split(/\s+/).every(part => scale.has(part.replace(/^-/, "")) || /^var\(--(space-\d+|gutter|control-h|header-h)\)$/.test(part)
    || /^calc\(/.test(part) || /^-?calc/.test(part) || /\)$/.test(part) || /^\d+(\.\d+)?(em|ch|%|vh|dvh|fr)$/.test(part));
}
test("U-006: shared stylesheets keep padding, margin and gap on the 4/8/12/16/24/32/48 scale", () => {
  for (const [name, css] of Object.entries(shared))
    for (const { property, value } of declarations(css, /^(padding|margin)(-[a-z]+)*$|^(row-|column-)?gap$|^inset$/))
      // The one exception: the content column clears the fixed 264 px sidebar (ui-standard §3.1).
      if (!(property === "padding-left" && value === "264px")) assert.ok(onScale(value), `${name}: ${property}: ${value}`);
});

const typeScale = new Set(["12px", "13px", "14px", "15px", "16px", "18px", "20px", "24px", "28px", "inherit", "1em"]);
test("U-007: shared stylesheets use the type scale, weights 400/600/700 and one 15 px body", () => {
  for (const [name, css] of Object.entries(shared)) {
    for (const { value } of declarations(css, /^font-size$/)) assert.ok(typeScale.has(value), `${name}: font-size ${value}`);
    for (const { value } of declarations(css, /^font-weight$/)) assert.match(value, /^(400|600|700|inherit)$/, `${name}: font-weight ${value}`);
    for (const { value } of declarations(css, /^font$/)) assert.match(value, /^(inherit|(400|600|700) (12|13|14|15|16|18|20|24|28)px\/[\d.]+(px)? .+)$/, `${name}: font ${value}`);
  }
  assert.doesNotMatch(access + identity, /font:\s*16px/, "the .page roots no longer switch the body to 16 px");
});

test("U-008: inside a section card, nested cards drop their border and become inset panels", () => {
  assert.match(access, /\.accountSection :is\([^)]*\.state[^)]*\.review[^)]*\)\s*\{[^}]*border:\s*0;[^}]*background:\s*var\(--muted\)/);
  assert.match(access, /\.detail :is\([^)]*\.review[^)]*\.preview[^)]*\)\s*\{[^}]*border:\s*0;[^}]*background:\s*var\(--muted\)/);
});

const stripe = /border(-left|-top|-right|-bottom)\s*:\s*([2-9](\.\d+)?px)\s+solid\s+(?!transparent)/;
test("U-009: no coloured accent stripes in any module stylesheet", () => {
  for (const file of [...moduleSheets, "src/app/foundation.css"]) {
    const css = read(file);
    for (const { property, value } of declarations(css, /^border-(left|top|right|bottom)$/))
      assert.doesNotMatch(`${property}: ${value}`, stripe, `${file}: ${property}: ${value}`);
    assert.doesNotMatch(css, /border-(left|top)-width\s*:\s*[2-9]px/, file);
  }
});

const fixRulesOwned = /public-catalog|public-showcase|public-sample/;
test("U-010: one focus ring — no module stylesheet draws its own outline", () => {
  for (const file of moduleSheets.filter(file => !fixRulesOwned.test(file)))
    for (const { property, value } of declarations(read(file), /^outline(-color|-width|-style|-offset)?$/))
      assert.match(`${property}: ${value}`, /^outline: (none|0)$/, `${file}: ${property}: ${value}`);
  assert.match(foundation, /:where\(a,button,input,select,textarea,summary\):focus-visible\{outline:3px solid var\(--ring\);outline-offset:3px\}/);
  assert.match(foundation, /:where\(\[tabindex\]\):focus-visible\{outline:3px solid var\(--ring\);outline-offset:3px\}/, "programmatic focus targets share the ring");
});

test("U-011: alerts are full-width tone panels without stripes, and the state classes are defined once", () => {
  const alert = rule(access, ".alert, .success");
  assert.match(alert, /max-width:\s*none/); assert.match(alert, /background:\s*var\(--tone-danger\)/);
  assert.match(alert, /border-radius:\s*var\(--radius-card\)/); assert.match(alert, /padding:\s*var\(--space-4\) var\(--space-6\)/);
  assert.match(rule(access, `.page p${G}`), /max-width:\s*72ch/);
  assert.match(access, /\.page p:is\(\.alert, \.success, \.empty, \[role="alert"\], \[role="status"\]\)\s*\{\s*max-width:\s*none;?\s*\}/);
  assert.match(identity, /\.page p:is\(\.alert, \.success, \[role="alert"\], \[role="status"\]\)\s*\{\s*max-width:\s*none;?\s*\}/);
  assert.equal(foundation.match(/(^|\})\s*\.empty-state\{/gm)?.length, 1, ".empty-state is defined once");
  assert.doesNotMatch(foundation, /\.error-message\{/, "the unused .error-message is gone");
  const state = rule(foundation, ".outcome-state,.state-view");
  assert.match(state, /width:100%;max-width:none/); assert.match(state, /border:0/); assert.match(state, /border-radius:var\(--radius-card\)/);
});

test("U-012: legacy tables share the DataTable look (muted header, middle alignment, 12/16 cells)", () => {
  assert.match(rule(access, `.page th${G}`), /background:\s*var\(--muted\)/);
  assert.match(rule(access, `.page th${G}, .page td${G}`), /vertical-align:\s*middle/);
  assert.match(rule(access, `.page th${G}, .page td${G}`), /padding:\s*var\(--space-3\) var\(--space-4\)/);
  assert.match(rule(foundation, ".list-table td"), /padding:var\(--space-3\) var\(--space-4\)/);
  assert.match(rule(foundation, ".list-table tbody tr"), /height:var\(--row-h\)/);
});

test("U-013: every badge look is the one 24 px, radius-6, 12/600 Badge", () => {
  const badge = rule(foundation, ".status-badge");
  for (const part of [/display:inline-flex/, /height:24px/, /padding:0 var\(--space-2\)/, /border-radius:var\(--radius-badge\)/, /font-size:12px/, /line-height:16px/, /font-weight:600/])
    assert.match(badge, part, `.status-badge ${part}`);
  for (const [name, css, selector] of [["access", access, ".status"], ["identity", identity, ".status, .neutralStatus"]] as const) {
    const body = rule(css, selector);
    assert.match(body, /height:\s*24px/, name); assert.match(body, /border-radius:\s*var\(--radius-badge\)/, name); assert.match(body, /font-size:\s*12px/, name);
    assert.doesNotMatch(body, /border:\s*1px/, `${name} badge has no outline`);
  }
});

test("U-014: shared fields are 44 px with the 3:1 border, radius 8, label gap 8 and the 720 px column", () => {
  for (const [name, css] of [["access", access], ["identity", identity]] as const) {
    const field = /\.page input:not\(\[type="?checkbox"?\]\)[^{]*\{([^}]*)\}/.exec(css)?.[1] ?? "";
    assert.match(css, /\.page input:not\(\[type="checkbox"\]\):not\(\[type="radio"\]\):where\(:not\(\[data-kit\], \[data-kit\] \*\)\)/, `${name} fields skip shared primitives`);
    assert.match(field, /height:\s*var\(--control-h\)/, `${name} field height`);
    assert.match(field, /border:\s*1px solid var\(--control-border\)/, `${name} field border`);
    assert.match(field, /border-radius:\s*var\(--radius-control\)/, `${name} field radius`);
    assert.match(field, /max-width:\s*var\(--content-narrow\)/, `${name} field column`);
    assert.match(rule(css, `.page label${G}`), /gap:\s*var\(--space-2\)/, `${name} label gap`);
    assert.match(css, /\.page textarea:where\([^{]*\{[^}]*min-height:\s*96px/, `${name} textarea`);
  }
  assert.match(rule(access, ".form"), /gap:\s*var\(--space-4\)/);
  // Blocks never touch: a paragraph, a button and the next card get 16 px between them (U-014, glued controls).
  // Primitives (data-kit roots, Sections) keep their own spacing; the computed check is tests/ui-kit-cascade-browser.test.ts.
  assert.match(rule(access, ".page > :where(* + *):where(:not([data-kit], :global(.ui-section)))"), /margin-top:\s*var\(--space-4\)/);
  assert.match(rule(access, ".accountSection > :where(* + *):where(:not([data-kit], :global(.ui-section)))"), /margin-top:\s*var\(--space-4\)/);
});

test("U-015/U-016: one left edge and one canvas in both frames", () => {
  const page = rule(access, ".page");
  assert.match(page, /padding:\s*var\(--space-8\) var\(--gutter\) var\(--space-12\)/);
  assert.match(page, /max-width:\s*calc\(var\(--content-default\) \+ 2 \* var\(--gutter\)\)/);
  assert.match(page, /background:\s*var\(--background\)/);
  assert.match(access, /:global\(\[data-workspace-frame\]\) \.page\s*\{[^}]*margin:\s*0;/, "left-aligned to the sidebar, not centred");
  assert.match(access, /:global\(\[data-store-frame\]\) \.page\s*\{[^}]*padding-inline:\s*0;[^}]*max-width:\s*none;[^}]*width:\s*min\(var\(--content-wide\), 100% - 2 \* var\(--gutter\)\)/);
  assert.match(identity, /\.page\s*\{[^}]*padding:\s*0 var\(--gutter\)/);
  const store = read("src/components/store/store-frame.module.css");
  assert.match(rule(store, ".inner"), /width:\s*min\(var\(--content-wide\), 100% - 2 \* var\(--gutter\)\)/);
  assert.match(rule(store, ".inner"), /padding:\s*0;?/);
  for (const file of ["src/modules/catalog/buyer-catalog.module.css", "src/modules/ordering/order-checkout.module.css"]) {
    const css = read(file);
    assert.match(css, /:global\(\[data-store-frame\]\) \.page\.page\s*\{[^}]*padding-inline:\s*0;[^}]*background:\s*var\(--background\)/, file);
  }
});

test("U-019: workspace navigation items are 44 px touch targets", () => {
  assert.match(rule(foundation, ".workspace-nav a"), /min-height:var\(--control-h\)/);
});

test("U-021: at phone width every shared action button is full width", () => {
  assert.match(access, /@media \(max-width: 639px\)\s*\{[^@]*\.page :is\(\.primary, \.secondary, \.danger\):not\(td \*, \.pagination \*\)\s*\{\s*width:\s*100%;?\s*\}/);
  assert.match(identity, /@media \(max-width: 639px\)\s*\{[^@]*\.page :is\(\.primary, \.secondary\)\s*\{\s*width:\s*100%;?\s*\}/);
});

test("U-022: every description list looks like Facts (13 px muted term, 15/600 value, 16/24 gaps)", () => {
  for (const [name, css] of [["access", access], ["identity", identity]] as const) {
    assert.match(rule(css, ".facts"), /gap:\s*var\(--space-4\) var\(--space-6\)/, name);
    assert.match(rule(css, ".facts dt"), /font-size:\s*13px/, name);
    assert.match(rule(css, ".facts dd"), /font-size:\s*15px;\s*font-weight:\s*600/, name);
  }
  const facts = rule(foundation, ".facts-list,.review-panel dl");
  assert.match(facts, /gap:var\(--space-4\) var\(--space-6\)/);
  assert.match(rule(foundation, ".facts-list dt,.review-panel dt"), /font-size:13px/);
  assert.match(rule(foundation, ".facts-list dd,.review-panel dd"), /font-size:15px;line-height:24px;font-weight:600/);
});

// Remaining hex values live in page stylesheets owned by the page agents (U-024 … U-040); this ceiling only falls.
const hexCeiling: Record<string, number> = {
  "src/modules/catalog/buyer-catalog.module.css": 82, "src/components/pilot-site-chrome.module.css": 27, "src/modules/identity/identity-login.module.css": 17,
  "src/modules/inventory/stock-source.module.css": 17, "src/modules/ordering/branch-request.module.css": 17, "src/modules/inventory/stock-source-alias.module.css": 16,
  "src/modules/inventory/stock-source-policy.module.css": 16, "src/modules/inventory/stock-source-revision.module.css": 16, "src/modules/inventory/stock-source-suspension.module.css": 16,
  "src/modules/catalog/catalog-editor.module.css": 15, "src/components/landing/landing.module.css": 13, "src/modules/billing/invoice.module.css": 11,
  "src/modules/inventory/stock-count.module.css": 11, "src/modules/billing/bank-statement.module.css": 10, "src/modules/ordering/order-draft.module.css": 7,
  "src/modules/organizations/organizations.module.css": 7, "src/modules/ordering/order-quote.module.css": 5, "src/modules/inventory/replenishment.module.css": 3,
};
test("U-023: no hex colour in the shared stylesheets, and page stylesheets never gain one", () => {
  for (const [name, css] of Object.entries(shared)) assert.doesNotMatch(css, /#[0-9a-f]{3,8}\b/i, `${name} uses tokens only`);
  for (const file of moduleSheets) {
    const count = (read(file).match(/#[0-9a-f]{3,8}\b/gi) ?? []).length;
    assert.ok(count <= (hexCeiling[file] ?? 0), `${file} has ${count} hex colours (ceiling ${hexCeiling[file] ?? 0})`);
  }
});
