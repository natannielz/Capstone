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

test("the story follows one request; the pilot ends at the review, the full release at receipt and invoice", () => {
  const steps = (html: string) => [...html.matchAll(/<li[^>]*data-step="([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(steps(render(true)), ["Diajukan", "Ditinjau toko"]);
  assert.deepEqual(steps(render(false)), ["Diajukan", "Ditinjau toko", "Dikirim", "Diterima"]);
  const html = render(false);
  assert.match(html, /<h2[^>]*>Satu karton, dari dok ke cabang\.<\/h2>/);
  assert.match(html, /<ol[^>]*aria-label="Perjalanan satu permintaan"/);
  assert.equal([...html.matchAll(/<p[^>]*data-act-time[^>]*>[^<]* WIB<\/p>/g)].length, 4, "each act carries its time from the example record");
  assert.match(html, /Gulir untuk mengikuti satu permintaan/);
  assert.match(html, /data-journey="4"/);
  assert.match(render(true), /data-journey="2"/);
});

test("the story closes with the next step after the last act", () => {
  const html = render(true), closing = html.slice(html.indexOf("data-story-end"));
  assert.match(closing, /<h2[^>]*>Siap mengajukan kebutuhan cabang\?<\/h2>/);
  assert.match(closing, /<a[^>]*href="\/identity\/login"[^>]*>Masuk ke akun<\/a>/);
  assert.match(closing, /<a[^>]*href="\/shop"[^>]*>Lihat katalog<\/a>/);
  assert.ok(html.indexOf("data-story-end") > html.indexOf('data-step="Ditinjau toko"'), "after the last act");
});

test("the catalog section is an honest example table", () => {
  const html = render(true);
  assert.match(html, /<table[^>]*><caption>Contoh\./, "the caption comes first and says it is an example");
  assert.equal([...html.matchAll(/<tr data-row/g)].length, 3);
  assert.match(html, /Isi katalog mengikuti akun organisasi Anda/);
});
