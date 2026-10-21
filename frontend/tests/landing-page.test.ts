import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Landing } from "../src/components/landing/Landing";
import { kardusSummary } from "../src/components/landing/KemasanLabel";

const render = (pilot: boolean) => renderToStaticMarkup(createElement(Landing, { pilot }));

test("the landing page states the package rule and offers sign-in and the catalog", () => {
  const html = render(true);
  assert.match(html, /<h1[^>]*>Kebutuhan cabang, dihitung per kemasan\.<\/h1>/);
  assert.match(html, /<a[^>]*href="\/identity\/login"[^>]*>Masuk ke akun<\/a>/);
  assert.match(html, /<a[^>]*href="\/katalog"[^>]*>Lihat katalog<\/a>/);
  assert.doesNotMatch(html, /→|home13-|home-topline/, "no arrow-suffixed buttons and no legacy landing classes");
});

test("guest links open the guest catalog directly, and each door opens its own home (G-020, G-022)", () => {
  for (const pilot of [true, false]) {
    const html = render(pilot), hrefs = [...html.matchAll(/<a[^>]*href="([^"]+)"/g)].map(match => match[1]);
    assert.equal(hrefs.includes("/shop"), false, "no catalog link takes the guest through a redirect hop");
    assert.match(html, /<a[^>]*href="\/workspace"[^>]*>Buka ruang kerja<\/a>/);
    assert.match(html, /<a[^>]*href="\/workspace"[^>]*>Ruang kerja petugas<\/a>/);
    assert.match(html, /<a[^>]*href="\/ordering\/requests"[^>]*>Lihat permintaan saya<\/a>/);
    assert.match(html, /<a[^>]*href="\/katalog"[^>]*>Katalog toko<\/a>/);
  }
});

test("the 3D stage is decorative and the request card carries the same information as text", () => {
  const html = render(true);
  assert.match(html, /<div[^>]*aria-hidden="true"[^>]*data-stage="kemasan"/, "stage is hidden from assistive technology");
  assert.match(html, /<h2[^>]*id="isi-kardus"[^>]*>Isi kardus permintaan PT-00000142<\/h2>/);
  assert.match(html, /<label for="contoh-jumlah">Jumlah dus<\/label>/);
  assert.match(html, /aria-live="polite"[^>]*>5 kemasan dalam 1 kardus, termasuk 24 sachet kopi</);
  for (const item of ["Kopi bubuk sachet", "Kertas HVS A4 70 gsm", "Teh celup melati", "Pulpen hitam", "Penyanitasi tangan 500 ml"]) assert.match(html, new RegExp(item));
  assert.equal(kardusSummary(3), "7 kemasan dalam 1 kardus, termasuk 72 sachet kopi");
  assert.equal(kardusSummary(1), "5 kemasan dalam 1 kardus, termasuk 24 sachet kopi");
});

test("the story follows one request; the pilot ends at the review, the full release at receipt and invoice", () => {
  const steps = (html: string) => [...html.matchAll(/<li[^>]*data-step="([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(steps(render(true)), ["Diajukan", "Ditinjau toko"]);
  assert.deepEqual(steps(render(false)), ["Diajukan", "Ditinjau toko", "Dikirim", "Diterima"]);
  const html = render(false);
  assert.match(html, /<h2[^>]*>Satu kardus, dari permintaan sampai cabang\.<\/h2>/);
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
  assert.match(closing, /<a[^>]*href="\/katalog"[^>]*>Lihat katalog<\/a>/);
  assert.ok(html.indexOf("data-story-end") > html.indexOf('data-step="Ditinjau toko"'), "after the last act");
});

test("the catalog section is an honest example table drawn from the catalog", () => {
  const html = render(true);
  assert.match(html, /<table[^>]*><caption>Contoh dari katalog toko\./, "the caption comes first and says it is an example");
  assert.equal([...html.matchAll(/<tr data-row/g)].length, 8);
  assert.match(html, /Kertas HVS A4 70 gsm/); assert.match(html, /Rim isi 500 lembar/);
  assert.match(html, /Isi katalog mengikuti akun organisasi Anda/);
});

test("the full release offers the story as a film that plays only when asked and makes no promise of timing; the pilot does not", () => {
  const html = render(false);
  assert.match(html, /<h2[^>]*id="judul-film"[^>]*>Lihat satu permintaan berjalan\.<\/h2>/);
  assert.match(html, /Lama setiap langkah mengikuti jadwal peninjauan toko dan pengiriman/);
  assert.doesNotMatch(html, /24 detik/, "the film's length is not presented as the process time");
  assert.match(html, /<video[^>]*controls[^>]*preload="none"[^>]*poster="\/media\/satu-kardus-poster\.jpg"[^>]*aria-describedby="isi-film"/);
  assert.match(html, /<source src="\/media\/satu-kardus\.webm" type="video\/webm"\/>/);
  assert.doesNotMatch(html, /<video[^>]*autoplay/i, "nothing plays by itself on the page");
  assert.doesNotMatch(render(true), /judul-film/);
});

test("benefits state what the system does, and the pilot claims only what the pilot offers", () => {
  const full = render(false), pilot = render(true);
  assert.equal([...full.matchAll(/<section[^>]*id="manfaat"[\s\S]*?<\/section>/g)].length, 1);
  for (const title of ["Jumlah selalu per kemasan", "Satu nomor sampai tagihan", "Tagihan sesuai barang diterima", "Diperiksa orang yang berbeda"]) assert.match(full, new RegExp(title));
  assert.doesNotMatch(pilot, /Tagihan sesuai barang diterima|Satu nomor sampai tagihan/, "the pilot has no shipping or billing");
  assert.match(pilot, /Jumlah selalu per kemasan/);
});
