import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { resolve } from "node:path";
import { createElement as h, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import postcss from "postcss";
import tailwind from "@tailwindcss/postcss";
import { transform } from "lightningcss";
import { Button } from "../src/components/workspace-kit/button";
import { PageHeader } from "../src/components/workspace-kit/page-header";
import { Section } from "../src/components/workspace-kit/section";
import { Tabs } from "../src/components/workspace-kit/tabs";
import { Field } from "../src/components/workspace-kit/field";
import { Pagination } from "../src/components/workspace-kit/pagination";

// A real-browser cascade check: the app stylesheet (Tailwind + foundation, layered) and the legacy page modules (CSS modules,
// unlayered, so they outrank every layer) are loaded the way Next loads them, and shared primitives placed inside a legacy
// `.page` (and inside a legacy `.accountSection`) must still render with their own values. A source regex cannot see an
// unlayered rule winning; computed styles can. (lightningcss stands in for webpack's css-loader: both emit hashed,
// unlayered class rules in source order, which is all the cascade depends on.)
//
// Playwright is resolved like the repository's browser scripts: PLAYWRIGHT_PACKAGE or UNIT_TOKO_PLAYWRIGHT_PACKAGE (a
// playwright package.json), then tests/browser/node_modules/playwright, then the local Codex runtime. Without Playwright or a
// launchable browser the test SKIPS; with UNITTOKO_REQUIRE_BROWSER_TESTS=1 it FAILS instead.
type BrowserPage = { setContent(html: string): Promise<void>; evaluate<T, A>(fn: (arg: A) => T, arg?: A): Promise<T> };
type Browser = { newPage(options: object): Promise<BrowserPage>; close(): Promise<void> };
type Playwright = { chromium: { launch(options: object): Promise<Browser> } };
const candidates = [process.env.PLAYWRIGHT_PACKAGE, process.env.UNIT_TOKO_PLAYWRIGHT_PACKAGE, resolve("tests/browser/node_modules/playwright/package.json"),
  "C:/Users/User/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/package.json"].filter((path): path is string => !!path);
function loadPlaywright(): Playwright | null {
  for (const path of candidates) {
    if (!existsSync(path)) continue;
    try { return createRequire(resolve(path))("playwright") as Playwright; } catch { /* try the next location */ }
  }
  return null;
}
const required = process.env.UNITTOKO_REQUIRE_BROWSER_TESTS === "1";
const channel = process.env.UNIT_TOKO_BROWSER_CHANNEL || (process.platform === "win32" ? "msedge" : undefined);

async function stylesheets() {
  const app = await postcss([tailwind({ base: "src/components/workspace-kit" })]).process(readFileSync("src/app/globals.css", "utf8"), { from: "src/app/globals.css" });
  const cssModule = (file: string) => {
    const result = transform({ filename: file, code: readFileSync(file), cssModules: true });
    const names: Record<string, string> = {};
    for (const [key, value] of Object.entries(result.exports ?? {})) names[key] = value.name;
    return { css: result.code.toString(), names };
  };
  return { app: app.css, access: cssModule("src/modules/administration/access.module.css"), identity: cssModule("src/modules/identity/identity.module.css") };
}

/** Primitives rendered as DIRECT children of the container (no wrapper), so the legacy `> :where(* + *)` flow rules reach them. */
function primitives(prefix: string): string {
  const parts: ReactElement[] = [
    h(PageHeader, { title: "Judul halaman", description: "Satu kalimat tujuan.", primaryAction: h(Button, { variant: "primary", id: `${prefix}-header-primary` }, "Simpan") }),
    h("p", null, "Paragraf halaman lama."),
    h(Section, { title: "Bagian pertama", id: `${prefix}-one` }, h("p", null, "Isi")),
    h(Section, { title: "Bagian kedua", id: `${prefix}-two` }, h("p", null, "Isi")),
    h(Button, { variant: "primary", id: `${prefix}-button` }, "Ajukan"),
    h(Tabs, { label: "Tampilan", current: "a", onSelect() {}, items: [{ label: "Buat permintaan", value: "a" }, { label: "Riwayat permintaan", value: "b" }] }),
    h(Field, { id: `${prefix}-field`, label: "Alasan", children: h("input", {}) }),
    h(Pagination, { label: "Halaman", start: 1, end: 25, total: 60, hasPrevious: false, hasNext: true, onPrevious() {}, onNext() {} }),
  ];
  return parts.map(part => renderToStaticMarkup(part)).join("");
}
/** A Section pair and a Button directly inside a legacy section card (its own flow rule). */
function sectionChildren(prefix: string): string {
  return [h("h2", null, "Bagian lama"), h(Section, { title: "Sub satu", id: `${prefix}-one` }, h("p", null, "Isi")),
    h(Section, { title: "Sub dua", id: `${prefix}-two` }, h("p", null, "Isi")), h(Button, { id: `${prefix}-button` }, "Muat ulang")].map(part => renderToStaticMarkup(part)).join("");
}

test("shared primitives keep their own look inside legacy access and identity pages", async t => {
  const playwright = loadPlaywright();
  if (!playwright) {
    if (required) assert.fail(`UNITTOKO_REQUIRE_BROWSER_TESTS=1 but Playwright was not found (looked in ${candidates.join(", ")})`);
    t.skip("Playwright not installed (set PLAYWRIGHT_PACKAGE, or npm install in tests/browser)"); return;
  }
  let browser: Browser;
  try { browser = await playwright.chromium.launch(channel ? { channel } : {}); }
  catch (error) {
    if (required) throw error;
    t.skip(`no launchable browser (${String(error).split("\n")[0]})`); return;
  }
  const css = await stylesheets();
  const html = `<!doctype html><html><head><style>${css.app}</style><style>${css.access.css}</style><style>${css.identity.css}</style></head><body>
    <main class="${css.access.names.page}" id="access"><div class="${css.access.names.header}"><div><h1>Halaman lama</h1><p>Tujuan.</p></div></div><button type="button" class="${css.access.names.primary}" id="access-legacy">Tombol lama</button>${primitives("access")}<section class="${css.access.names.accountSection}" id="card">${sectionChildren("card")}</section></main>
    <main class="${css.identity.names.page}" id="identity">${primitives("identity")}</main></body></html>`;
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    await page.setContent(html);
    const geometry = (root: string) => page.evaluate((root: string) => {
      const box = (element: Element) => element.getBoundingClientRect();
      const q = (selector: string) => document.querySelector(`#${root} ${selector}`)!;
      const button = document.querySelector(`#${root}-button`)!;
      return {
        sectionGap: Math.round(box(q(`[aria-labelledby="${root}-two"]`)).top - box(q(`[aria-labelledby="${root}-one"]`)).bottom),
        buttonMarginTop: getComputedStyle(button).marginTop, buttonParent: button.parentElement!.id || button.parentElement!.tagName,
      };
    }, root);
    for (const root of ["access", "identity"]) {
      const measured = await page.evaluate((root: string) => {
        const box = (element: Element) => element.getBoundingClientRect();
        const style = (element: Element) => { const s = getComputedStyle(element); return { size: s.fontSize, weight: s.fontWeight, line: s.lineHeight, height: Math.round(box(element).height), width: Math.round(box(element).width) }; };
        const q = (selector: string) => document.querySelector(`#${root} ${selector}`)!;
        const h1 = q(".page-header h1"), description = q(".page-header-description");
        return {
          button: style(q(`#${root}-button`)), headerButton: style(q(`#${root}-header-primary`)), tab: style(q(".ui-tab")),
          pager: style(q(".list-table-pagination .ui-button")), input: style(q(`#${root}-field`)), label: style(q(".ui-field-label")), h1: style(h1),
          h1ToDescription: Math.round(box(description).top - box(h1).bottom),
        };
      }, root);
      for (const name of ["button", "headerButton"] as const)
        assert.deepEqual([measured[name].size, measured[name].weight, measured[name].line, measured[name].height], ["14px", "600", "20px", 44], `${root} ${name} ${JSON.stringify(measured[name])}`);
      assert.deepEqual([measured.tab.size, measured.tab.weight, measured.tab.height], ["15px", "600", 44], `${root} tab ${JSON.stringify(measured.tab)}`);
      assert.deepEqual([measured.pager.height, measured.pager.width], [44, 44], `${root} pager button`);
      assert.deepEqual([measured.input.size, measured.input.height], ["15px", 44], `${root} field control`);
      assert.deepEqual([measured.label.size, measured.label.weight], ["14px", "600"], `${root} field label`);
      assert.deepEqual([measured.h1.size, measured.h1.weight], ["28px", "700"], `${root} page title`);
      assert.equal(measured.h1ToDescription, 8, `${root} h1 → purpose line`);
    }
    // Flow-rule exclusions: Sections and kit roots that are direct children of a legacy .page / .accountSection keep their own
    // spacing (24 px between Sections from @layer components; no 16 px legacy flow margin on a kit root).
    for (const root of ["access", "identity", "card"]) {
      const measured = await geometry(root);
      assert.equal(measured.buttonParent, root === "identity" || root === "access" ? root : "card", `${root} button is a direct child`);
      assert.equal(measured.sectionGap, 24, `${root}: Sections directly inside the legacy container are 24 px apart`);
      assert.equal(measured.buttonMarginTop, "0px", `${root}: the legacy flow rule skips the kit Button`);
    }
    const legacy = await page.evaluate(() => { const s = getComputedStyle(document.querySelector("#access-legacy")!); return [s.fontSize, s.fontWeight, Math.round(document.querySelector("#access-legacy")!.getBoundingClientRect().height), s.marginTop]; });
    assert.deepEqual(legacy, ["14px", "600", 44, "16px"], "legacy .primary also measures 44 px, 14/600, and legacy blocks still get the 16 px flow margin");
  } finally { await browser.close(); }
});
