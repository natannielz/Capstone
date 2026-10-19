import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { STATUS_LABELS, statusTone } from "../src/lib/status/dictionary";

// The tone each label's former [data-status] colour meant (globals.css / polish.css before Phase B).
const expected: Record<string, string> = {
  "Menunggu tinjauan": "warning", "Menunggu verifikasi": "warning", "Menunggu persetujuan": "warning", "Siap dikirim": "warning",
  "Diproses": "info", "Dalam perjalanan": "info", "Dalam pengiriman": "info",
  "Terpenuhi": "success", "Diterima": "success", "Lunas": "success", "Terverifikasi": "success", "Disetujui": "success", "Transaksi final": "success", "Aktif": "success",
  "Lewat jatuh tempo": "danger", "Ditolak": "danger", "Gagal dikirim": "danger", "Belum teridentifikasi": "danger",
};

test("every previously styled status label keeps the tone its colour meant", () => {
  for (const [label, tone] of Object.entries(expected)) assert.equal(statusTone(label), tone, label);
  for (const label of Object.keys(expected)) assert.ok(STATUS_LABELS.has(label), label);
});

test("unknown label falls back to neutral", () => {
  assert.equal(statusTone("Status baru dari layanan"), "neutral");
  assert.equal(statusTone(""), "neutral");
  assert.equal(statusTone("  Lunas  "), "success");
});

test("status colour is chosen by tone, never by label text in CSS", () => {
  for (const file of readdirSync("src/app").filter(name => name.endsWith(".css")))
    assert.equal(/\[data-status=/.test(readFileSync(`src/app/${file}`, "utf8")), false, `${file} still keys colour on label text`);
});
