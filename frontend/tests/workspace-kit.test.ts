import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ListTable } from "../src/components/workspace-kit/list-table";
import { EmptyState } from "../src/components/workspace-kit/empty-state";

const rows = [{ id: "1", name: "Pesanan A" }, { id: "2", name: "Pesanan B" }];
const table = (props: Record<string, unknown>) => renderToStaticMarkup(createElement(ListTable<{ id: string; name: string }>, {
  caption: "Daftar", rows, getKey: row => row.id, columns: [{ key: "name", header: "Nama", render: row => row.name }],
  onOpen() {}, openLabel: row => "Lihat " + row.name, ...props }));

test("list_table_disabled_rows", () => {
  const locked = table({ disabled: true });
  assert.equal((locked.match(/<button[^>]*disabled=""/g) ?? []).length, 2);
  assert.equal((table({}).match(/disabled=""/g) ?? []).length, 0);
});

test("the selected row is marked current, not aria-selected outside a grid", () => {
  const html = table({ selectedKey: "2" });
  assert.match(html, /<tr aria-current="true">/);
  assert.doesNotMatch(html, /aria-selected/);
});

test("each empty state kind says what to do next; done is announced", () => {
  const first = renderToStaticMarkup(createElement(EmptyState, { kind: "first-use", title: "Belum ada pesanan", body: "Pilih kemasan dari katalog.", action: createElement("a", { href: "/catalog/browse" }, "Buka katalog") }));
  assert.match(first, /data-kind="first-use"/); assert.match(first, /<h3>Belum ada pesanan<\/h3>/); assert.match(first, /Buka katalog/);
  assert.doesNotMatch(first, /role="status"/);
  const done = renderToStaticMarkup(createElement(EmptyState, { kind: "done", title: "Semua pekerjaan selesai", body: "Tidak ada yang menunggu Anda." }));
  assert.match(done, /role="status"/); assert.match(done, /data-kind="done"/);
  assert.match(renderToStaticMarkup(createElement(EmptyState, { kind: "no-results", title: "Tidak ditemukan", body: "Ubah kata pencarian." })), /data-kind="no-results"/);
});
