import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { IdentityLogin } from "../src/modules/identity/IdentityLogin";

// Phase G task 6: a focused sign-in page. The form comes first in reading order; the account types and the decorative
// carton drawing sit in the dock panel beside it.
test("the sign-in page leads with the form and says who the accounts are for", () => {
  const html = renderToStaticMarkup(createElement(IdentityLogin));
  assert.match(html, /<a[^>]*href="#identity-content"[^>]*>Langsung ke formulir masuk<\/a>/);
  assert.match(html, /<main[^>]*id="identity-content"[^>]*>[\s\S]*<h1[^>]*>Masuk ke Unit Toko<\/h1>/);
  assert.ok(html.indexOf("<main") < html.indexOf("<aside"), "the form precedes the context panel in reading order");
  assert.deepEqual([...html.matchAll(/<dt>([^<]+)<\/dt>/g)].map(match => match[1]), ["PIC cabang BNI", "Akun usaha yang disetujui", "Petugas toko"]);
  assert.match(html, /<div[^>]*aria-hidden="true"[^>]*><svg/, "the carton drawing is decorative");
  assert.match(html, /role="status"[^>]*>[\s\S]*Memeriksa layanan akun/, "the session check is announced");
  assert.doesNotMatch(html, /RUANG KERJA UNIT TOKO|AKUN ORGANISASI ANDA|Navigasi utama/, "no all-caps eyebrows and no site navigation");
  assert.match(html, /<a[^>]*href="\/"[^>]*>Beranda<\/a>/);
});
