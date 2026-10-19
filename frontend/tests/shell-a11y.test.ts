import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { whatsappLink } from "../src/lib/whatsapp";
import { HelpLink, SkipLink, focusMain } from "../src/components/workspace-kit/shell-links";

test("skip_link_targets_main_content", () => {
  const html = renderToStaticMarkup(createElement(SkipLink));
  assert.match(html, /class="skip-link"/);
  assert.match(html, />Lewati ke konten utama</);
  const layout = readFileSync("src/app/layout.tsx", "utf8");
  assert.ok(layout.indexOf("<SkipLink />") > -1 && layout.indexOf("<SkipLink />") < layout.indexOf("{children}"), "skip link comes before the page");
  // Focus moves to the page's <main>, which becomes focusable without entering the tab order.
  const attributes = new Map<string, string>(); let focused = false, scrolled = false;
  const main = { hasAttribute: (name: string) => attributes.has(name), setAttribute: (name: string, value: string) => attributes.set(name, value),
    focus: () => { focused = true; }, scrollIntoView: () => { scrolled = true; } };
  assert.equal(focusMain({ querySelector: () => main } as never), true);
  assert.deepEqual([attributes.get("tabindex"), focused, scrolled], ["-1", true, true]);
  assert.equal(focusMain({ querySelector: () => null } as never), false);
});

test("help_link_absent_without_number", () => {
  assert.equal(whatsappLink(undefined, "Halo"), null);
  assert.equal(whatsappLink("", "Halo"), null);
  assert.equal(whatsappLink("12345", "Halo"), null);
  assert.equal(renderToStaticMarkup(createElement(HelpLink, { phone: undefined })), "");
});

test("WhatsApp links are digits-only with the 62 prefix and never carry amounts or identifiers", () => {
  assert.equal(whatsappLink("0812-3456-7890", "Halo, saya perlu bantuan"), "https://wa.me/6281234567890?text=Halo%2C%20saya%20perlu%20bantuan");
  assert.equal(whatsappLink("+62 812 3456 7890", "Halo"), "https://wa.me/6281234567890?text=Halo");
  assert.equal(whatsappLink("6281234567890", "Halo"), "https://wa.me/6281234567890?text=Halo");
  assert.throws(() => whatsappLink("081234567890", "Tagihan Rp150.000"));
  assert.throws(() => whatsappLink("081234567890", "NIK 3174012345678901"));
  const html = renderToStaticMarkup(createElement(HelpLink, { phone: "081234567890" }));
  assert.match(html, /href="https:\/\/wa.me\/6281234567890\?text=/);
  assert.match(html, />Bantuan via WhatsApp</);
  assert.match(html, /rel="noopener noreferrer"/);
});

test("the mobile menu closes with an Indonesian label", () => {
  const sheet = readFileSync("src/components/ui/sheet.tsx", "utf8");
  assert.doesNotMatch(sheet, />Close</);
  assert.match(sheet, /Tutup/);
});

// The sidebar and the mobile sheet each render the navigation; with the sheet open both are in the DOM, so heading ids
// and landmark labels must stay unique, and only real links carry aria-current.
test("two navigation copies keep unique heading ids and distinct landmark labels", async () => {
  const { NavigationList } = await import("../src/modules/administration/NavigationList");
  const groups = [{ id: "home", label: "Beranda", items: [{ href: "/identity/account", label: "Pekerjaan saya", description: "", current: true, available: true }] }];
  const html = renderToStaticMarkup(createElement("div", null,
    createElement(NavigationList, { groups, locked: false, label: "Ruang kerja" }),
    createElement(NavigationList, { groups, locked: false, label: "Menu ruang kerja" })));
  const ids = [...html.matchAll(/ id="([^"]+)"/g)].map(match => match[1]);
  assert.equal(new Set(ids).size, ids.length, ids.join(","));
  assert.match(html, /aria-label="Ruang kerja"/);
  assert.match(html, /aria-label="Menu ruang kerja"/);
  const source = readFileSync("src/modules/administration/AdministrationNavigation.tsx", "utf8");
  assert.doesNotMatch(source, /<span[^>]*aria-current/);
});
