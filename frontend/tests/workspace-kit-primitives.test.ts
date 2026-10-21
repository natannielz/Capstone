import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createElement as h } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Button, buttonClassName } from "../src/components/workspace-kit/button";
import { PageHeader } from "../src/components/workspace-kit/page-header";
import { Section } from "../src/components/workspace-kit/section";
import { Toolbar } from "../src/components/workspace-kit/toolbar";
import { Field } from "../src/components/workspace-kit/field";
import { DataList } from "../src/components/workspace-kit/data-list";
import { DataTable, Pagination } from "../src/components/workspace-kit/data-table";
import { Badge } from "../src/components/workspace-kit/badge";
import { StatusBadge } from "../src/components/workspace-kit/status-badge";
import { Tabs } from "../src/components/workspace-kit/tabs";
import { StateView, regionState } from "../src/components/workspace-kit/state-view";
import { OutcomeState } from "../src/components/workspace-kit/outcome-state";
import { Facts } from "../src/components/workspace-kit/facts";

const count = (html: string, pattern: RegExp) => (html.match(pattern) ?? []).length;

test("Button sizes map to the 44 px and 36 px classes; variants and busy state are explicit", () => {
  assert.equal(buttonClassName({ variant: "primary" }), "ui-button ui-button--primary");
  assert.equal(buttonClassName({ variant: "secondary", size: "compact" }), "ui-button ui-button--secondary ui-button--compact");
  assert.equal(buttonClassName({ variant: "tertiary", size: "icon-compact" }), "ui-button ui-button--tertiary ui-button--icon ui-button--compact");
  const html = renderToStaticMarkup(h(Button, { variant: "primary", busy: true }, "Simpan"));
  assert.match(html, /^<button type="button" class="ui-button ui-button--primary"/);
  assert.match(html, /aria-busy="true"/); assert.match(html, /disabled=""/); assert.match(html, /class="ui-spinner"/); assert.match(html, />Simpan<\/button>$/);
  assert.match(renderToStaticMarkup(h(Button, { type: "submit" }, "Kirim")), /type="submit" class="ui-button ui-button--secondary"/, "secondary is the default");
  const foundation = readFileSync("src/app/foundation.css", "utf8");
  assert.match(foundation, /\.ui-button\{[^}]*height:var\(--control-h\)/); assert.match(foundation, /\.ui-button--compact\{height:var\(--control-h-compact\)/);
});

test("PageHeader renders one h1, the purpose line and at most one primary action", () => {
  const html = renderToStaticMarkup(h(PageHeader, { title: "Tagihan", description: "Terbitkan dan periksa invoice.",
    primaryAction: h(Button, { variant: "primary" }, "Terbitkan"), secondaryAction: h(Button, {}, "Muat ulang"), meta: h(Badge, { status: "Lunas" }) }));
  assert.equal(count(html, /<h1[ >]/g), 1);
  assert.match(html, /<h1>Tagihan<\/h1>/);
  assert.match(html, /<p class="page-header-description">Terbitkan dan periksa invoice\.<\/p>/);
  assert.equal(count(html, /ui-button--primary/g), 1);
  assert.ok(html.indexOf(">Muat ulang<") < html.indexOf(">Terbitkan<"), "secondary first, primary outermost right");
  assert.match(html, /^<header class="page-header" data-kit="">/);
  assert.doesNotMatch(renderToStaticMarkup(h(PageHeader, { title: "Pesanan" })), /page-header-actions|page-header-description/);
  const crumbs = renderToStaticMarkup(h(PageHeader, { title: "Air mineral", breadcrumb: h("a", { href: "/shop" }, "Katalog") }));
  assert.match(crumbs, /<nav aria-label="Lokasi halaman" class="page-header-crumb"><a href="\/shop">Katalog<\/a><\/nav>/);
});

test("Section is a labelled region with its h2, optional description and actions", () => {
  const html = renderToStaticMarkup(h(Section, { title: "Invoice terbit", description: "Invoice dalam akses Anda.", actions: h(Button, {}, "Muat ulang") }, h("p", null, "Isi")));
  assert.match(html, /^<section class="ui-section" data-variant="card" aria-labelledby="section-invoice-terbit">/);
  assert.match(html, /<h2 id="section-invoice-terbit">Invoice terbit<\/h2>/);
  assert.match(html, /<p class="ui-section-description">Invoice dalam akses Anda\.<\/p>/);
  assert.match(html, /<div class="ui-section-body"><p>Isi<\/p><\/div>/);
  assert.match(renderToStaticMarkup(h(Section, { title: "A", id: "x", variant: "plain" })), /data-variant="plain" aria-labelledby="x"/);
});

test("Toolbar puts search and filters left, sort and view right, and a polite result line below", () => {
  const html = renderToStaticMarkup(h(Toolbar, { label: "Saring pesanan", search: h("input", { "aria-label": "Cari" }), sort: h("select", { "aria-label": "Urutkan" }), resultText: "6 kemasan" }));
  assert.match(html, /<div class="ui-toolbar" role="group" aria-label="Saring pesanan"><div class="ui-toolbar-main"><input aria-label="Cari"\/><\/div><div class="ui-toolbar-side"><select aria-label="Urutkan"><\/select><\/div><\/div>/);
  assert.match(html, /<p class="ui-toolbar-result" aria-live="polite">6 kemasan<\/p>/);
});

test("Field ties label, hint and error to its control with aria-describedby", () => {
  const html = renderToStaticMarkup(h(Field, { id: "alasan", label: "Alasan penerbitan", help: "Tulis singkat.", error: "Alasan wajib diisi.", optional: true,
    children: h("textarea", { "aria-describedby": "lain" }) }));
  assert.match(html, /<label for="alasan" class="ui-field-label">Alasan penerbitan<span class="ui-field-optional"> \(opsional\)<\/span><\/label>/);
  assert.match(html, /<textarea aria-describedby="lain alasan-help alasan-error" id="alasan" aria-invalid="true"><\/textarea>/);
  assert.match(html, /<p id="alasan-help" class="ui-field-help">Tulis singkat\.<\/p>/);
  assert.match(html, /<p id="alasan-error" class="ui-field-error">.*Alasan wajib diisi\.<\/p>/);
  const plain = renderToStaticMarkup(h(Field, { id: "jumlah", label: "Jumlah", width: "number", children: h("input", { type: "number" }) }));
  assert.match(plain, /data-width="number"/); assert.match(plain, /<input type="number" id="jumlah"\/>/); assert.doesNotMatch(plain, /aria-describedby|aria-invalid/);
});

type Row = { id: string; name: string; status: string };
const rows: Row[] = [{ id: "1", name: "PSN-000001", status: "Lunas" }, { id: "2", name: "PSN-000002", status: "Ditolak" }];

test("DataList rows are divided list items; one destination makes the whole row the button", () => {
  const html = renderToStaticMarkup(h(DataList<Row>, { label: "Pesanan", items: rows, getKey: row => row.id, renderTitle: row => row.name,
    renderMeta: row => "Cabang " + row.id, renderBadge: row => h(Badge, { status: row.status }), onSelect() {}, selectedKey: "2" }));
  assert.match(html, /^<ul class="data-list" aria-label="Pesanan">/);
  assert.equal(count(html, /<li class="data-list-row"/g), 2);
  assert.match(html, /<li class="data-list-row" aria-current="true"><button type="button" class="data-list-open">/);
  assert.match(html, /<span class="data-list-title">PSN-000002<\/span>/); assert.match(html, /<span class="data-list-meta">Cabang 2<\/span>/);
  assert.doesNotMatch(html, /<button[^>]*>(?:(?!<\/button>).)*<(p|div)[ >]/, "a row button holds phrasing content only");
  const empty = renderToStaticMarkup(h(DataList<Row>, { label: "Pesanan", items: [], getKey: row => row.id, renderTitle: row => row.name, empty: { title: "Belum ada pesanan", message: "Pilih kemasan dari katalog." } }));
  assert.match(empty, /data-kind="empty"/); assert.doesNotMatch(empty, /<ul/);
});

test("DataTable shows exactly one state: an error replaces the rows and the empty message", () => {
  const columns = [{ key: "name", header: "Nomor", render: (row: Row) => row.name }, { key: "status", header: "Status", render: (row: Row) => h(Badge, { status: row.status }) }];
  const base = { caption: "Pesanan", columns, getKey: (row: Row) => row.id, empty: { title: "Belum ada pesanan", message: "Pesanan baru tampil di sini." } };
  const failed = renderToStaticMarkup(h(DataTable<Row>, { ...base, rows: [], error: { message: "Layanan belum tersedia.", onRetry() {} } }));
  assert.match(failed, /data-kind="error"/); assert.match(failed, /role="alert"/); assert.match(failed, />Coba lagi</);
  assert.doesNotMatch(failed, /Belum ada pesanan|<table/);
  const empty = renderToStaticMarkup(h(DataTable<Row>, { ...base, rows: [] }));
  assert.match(empty, /data-kind="empty"/); assert.doesNotMatch(empty, /<table|Sebelumnya/);
  const loading = renderToStaticMarkup(h(DataTable<Row>, { ...base, rows: [], loading: true }));
  assert.match(loading, /data-kind="loading"/); assert.doesNotMatch(loading, /Belum ada pesanan/);
  const table = renderToStaticMarkup(h(DataTable<Row>, { ...base, rows, density: "compact" }));
  assert.match(table, /<table class="list-table" data-density="compact">/); assert.equal(count(table, /<tr>/g), 3);
  assert.equal(regionState({ loading: true, error: true, empty: true }), "error");
  assert.equal(regionState({ loading: true, empty: true }), "loading");
  assert.equal(regionState({ empty: true }), "empty");
  assert.equal(regionState({ loading: true, empty: false }), "content", "a refresh keeps the rows visible");
});

test("Pagination says the range and hides itself when everything fits", () => {
  const html = renderToStaticMarkup(h(Pagination, { label: "Halaman pesanan", start: 1, end: 25, total: 120, hasPrevious: false, hasNext: true, onPrevious() {}, onNext() {} }));
  assert.match(html, /<nav aria-label="Halaman pesanan" class="list-table-pagination"><span>1–25 dari 120<\/span>/);
  assert.match(html, /aria-label="Sebelumnya"[^>]*disabled=""/); assert.match(html, /aria-label="Berikutnya"/);
  assert.equal(renderToStaticMarkup(h(Pagination, { label: "Halaman", start: 1, end: 3, hasPrevious: false, hasNext: false, onPrevious() {}, onNext() {} })), "");
});

test("Badge takes its tone from the status dictionary or an explicit tone; StatusBadge renders through it", () => {
  assert.equal(renderToStaticMarkup(h(Badge, { status: "Ditolak" })), '<span data-tone="danger" class="status-badge">Ditolak</span>');
  assert.equal(renderToStaticMarkup(h(Badge, { tone: "info", label: "Baru" })), '<span data-tone="info" class="status-badge">Baru</span>');
  assert.equal(renderToStaticMarkup(h(StatusBadge, { text: "Lunas" })), '<span data-tone="success" class="status-badge">Lunas</span>');
});

test("Tabs: page links mark the current page; in-page views stay buttons with aria-pressed", () => {
  const links = renderToStaticMarkup(h(Tabs, { label: "Permintaan", current: "/ordering/requests", items: [{ label: "Buat permintaan", href: "/ordering/requests" }, { label: "Riwayat", href: "/ordering/requests/history" }] }));
  assert.match(links, /^<nav aria-label="Permintaan" class="ui-tabs" data-kit="">/);
  assert.match(links, /<a class="ui-tab" aria-current="page" href="\/ordering\/requests">Buat permintaan<\/a>/);
  assert.match(links, /<a class="ui-tab" href="\/ordering\/requests\/history">Riwayat<\/a>/);
  const views = renderToStaticMarkup(h(Tabs, { label: "Tampilan", current: "riwayat", onSelect() {}, items: [{ label: "Buat permintaan", value: "buat" }, { label: "Riwayat permintaan", value: "riwayat" }] }));
  assert.match(views, /<button type="button" class="ui-tab" aria-pressed="false">Buat permintaan<\/button>/);
  assert.match(views, /<button type="button" class="ui-tab" aria-pressed="true">Riwayat permintaan<\/button>/);
});

test("StateView renders one state with its role; denied is neutral; OutcomeState renders through it", () => {
  const error = renderToStaticMarkup(h(StateView, { kind: "error", title: "Tagihan belum dapat dimuat", message: "Isian Anda tetap tersimpan.", detail: "Kode: mock-503", action: h(Button, {}, "Coba lagi") }));
  assert.match(error, /^<div class="state-view" data-kind="error" role="alert" data-kit="">/); assert.match(error, /Kode: mock-503/);
  const denied = renderToStaticMarkup(h(StateView, { kind: "denied", title: "Halaman ini di luar tugas akun Anda", message: "Hubungi pengelola organisasi." }));
  assert.match(denied, /data-kind="denied"/); assert.match(denied, /<h2 class="state-view-title">/); assert.doesNotMatch(denied, /role="alert"/);
  const empty = renderToStaticMarkup(h(StateView, { kind: "empty", title: "Belum ada invoice", message: "Invoice baru tampil di sini." }));
  assert.match(empty, /<h3 class="state-view-title">Belum ada invoice<\/h3>/); assert.doesNotMatch(empty, /role=/);
  const loading = renderToStaticMarkup(h(StateView, { kind: "loading", title: "Memuat tagihan", rows: 2 }));
  assert.match(loading, /role="status" aria-busy="true"/); assert.equal(count(loading, /outcome-skeleton/g), 2); assert.match(loading, /list-table-sr">Memuat tagihan/);
  const outcome = renderToStaticMarkup(h(OutcomeState, { kind: "error", onAction() {} }));
  assert.match(outcome, /class="state-view" data-kind="error" role="alert"/); assert.match(outcome, /Terjadi kendala sistem/); assert.match(outcome, />Coba lagi<\/button>/);
  assert.match(renderToStaticMarkup(h(OutcomeState, { kind: "conflict" })), /data-kind="unknown"/);
});

test("Facts renders one description list", () => {
  const html = renderToStaticMarkup(h(Facts, { items: [{ term: "Ditagihkan kepada", value: "BNI Wilayah Uji" }, { term: "Total", value: "Rp1.250.000" }] }));
  assert.equal(html, '<dl class="facts-list" data-columns="2"><div><dt>Ditagihkan kepada</dt><dd>BNI Wilayah Uji</dd></div><div><dt>Total</dt><dd>Rp1.250.000</dd></div></dl>');
});

test("the workspace frame's skip target holds no stray text outside the page landmarks (U-020)", () => {
  const frame = readFileSync("src/components/workspace-kit/workspace-frame.tsx", "utf8");
  assert.match(frame, /<span id="konten-utama" tabIndex=\{-1\} \/>/);
  assert.doesNotMatch(frame, />Konten utama<\/span>/);
});

test("the kit README documents every primitive", () => {
  const readme = readFileSync("src/components/workspace-kit/README.md", "utf8");
  for (const name of ["PageHeader", "Section", "Toolbar", "Field", "DataList", "DataTable", "Pagination", "Badge", "Button", "Tabs", "StateView", "Facts", "data-kit"])
    assert.match(readme, new RegExp(name), name);
});
