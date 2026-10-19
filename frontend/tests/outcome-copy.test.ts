import { test } from "node:test";
import assert from "node:assert/strict";
import { OUTCOME_KINDS, outcomeCopy } from "../src/components/workspace-kit/outcome-copy";

test("each outcome state has fixed Indonesian copy", () => {
  assert.equal(outcomeCopy("unknown").title, "Hasil belum pasti");
  assert.equal(outcomeCopy("unknown").action, "Periksa hasil");
  assert.equal(outcomeCopy("offline").title, "Anda sedang luring");
  assert.equal(outcomeCopy("denied").title, "Akses tidak tersedia");
  assert.equal(outcomeCopy("conflict").title, "Data sudah berubah");
  assert.equal(outcomeCopy("conflict").action, "Muat ulang");
  assert.equal(outcomeCopy("empty-filtered").title, "Tidak ada yang cocok dengan filter");
  assert.equal(outcomeCopy("empty-filtered").action, "Hapus filter");
  assert.equal(outcomeCopy("error").action, "Coba lagi");
});

test("an unknown outcome is never presented as a failure and promises no duplicate", () => {
  const copy = outcomeCopy("unknown");
  assert.equal(/gagal/i.test(copy.title + copy.body), false);
  assert.match(copy.body, /tidak dikirim ulang|tanpa mengirim ulang/i);
});

test("every kind has a title and a body; loading carries no action", () => {
  for (const kind of OUTCOME_KINDS) { const copy = outcomeCopy(kind); assert.ok(copy.title.length > 0 && copy.body.length > 0, kind); }
  assert.equal(outcomeCopy("loading").action, undefined);
});
