import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Landing } from "../src/components/landing/Landing";
import { kemasanSummary } from "../src/components/landing/KemasanLabel";

const render = (pilot: boolean) => renderToStaticMarkup(createElement(Landing, { pilot }));

test("the landing page states the package rule and offers sign-in and the catalog", () => {
  const html = render(true);
  assert.match(html, /<h1[^>]*>Kebutuhan cabang, dihitung per kemasan\.<\/h1>/);
  assert.match(html, /<a[^>]*href="\/identity\/login"[^>]*>Masuk ke akun<\/a>/);
  assert.match(html, /<a[^>]*href="\/shop"[^>]*>Lihat katalog<\/a>/);
  assert.doesNotMatch(html, /→|home13-|home-topline/, "no arrow-suffixed buttons and no legacy landing classes");
});

test("the 3D stage is decorative and the label carries the same information as text", () => {
  const html = render(true);
  assert.match(html, /<div[^>]*aria-hidden="true"[^>]*data-stage="kemasan"/, "stage is hidden from assistive technology");
  assert.match(html, /<label for="contoh-jumlah">Jumlah karton<\/label>/);
  assert.match(html, /aria-live="polite"[^>]*>2 karton berisi 48 botol</);
  assert.match(html, /Isi per kemasan/); assert.match(html, /24 botol per karton/); assert.match(html, /Minimum/);
  assert.equal(kemasanSummary(3), "3 karton berisi 72 botol");
  assert.equal(kemasanSummary(1), "1 karton berisi 24 botol");
});

test("the status trail follows the release: the pilot ends at the review, the full release at the invoice", () => {
  const steps = (html: string) => [...html.matchAll(/<li data-step="([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(steps(render(true)), ["Diajukan", "Ditinjau toko", "Hasil peninjauan"]);
  assert.deepEqual(steps(render(false)), ["Diajukan", "Ditinjau toko", "Disiapkan", "Dikirim", "Diterima cabang", "Tagihan terbit"]);
  assert.match(render(true), /<ol[^>]*aria-label="Contoh riwayat permintaan"/);
});

test("the catalog section is an honest example table", () => {
  const html = render(true);
  assert.match(html, /<table[^>]*><caption>Contoh\./, "the caption comes first and says it is an example");
  assert.equal([...html.matchAll(/<tr data-row/g)].length, 3);
  assert.match(html, /Isi katalog mengikuti akun organisasi Anda/);
});
