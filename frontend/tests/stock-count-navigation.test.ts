import { test } from "node:test";
import assert from "node:assert/strict";
import { identityDestinations, identityReturnPath, loginFields } from "../src/modules/identity/identity-workflow";
import { recoveryPage, operationLabel } from "../src/modules/administration/operation-recovery-navigation";

test("count and read permissions independently show the actual staff route", () => {
  for (const actions of [["stock.count"], ["stock.read"], ["stock.count", "stock.read"]])
    assert.equal(identityDestinations(actions).filter(item => item.href === "/inventory/counts").length, 1);
  for (const actions of [[], ["catalog.manage"], ["technical.configure"], ["stock.adjust"]])
    assert.equal(identityDestinations(actions).some(item => item.href === "/inventory/counts"), false);
});
test("stock sign-in returns are exact and reject altered paths", () => {
  assert.equal(identityReturnPath("/inventory/counts"), "/inventory/counts");
  assert.deepEqual(loginFields("/inventory/counts", "", ""), { returnPath: "/inventory/counts" });
  for (const path of ["/inventory/counts/", "/inventory/counts?warehouse=a%201", "/inventory/counts#other", "//inventory/counts", "/inventory/%63ounts"]) {
    assert.equal(identityReturnPath(path), "/workspace");
    assert.throws(() => loginFields(path, "", ""), /invalid_return_path/);
  }
});
test("inventory receipt navigation points to counting and preserves other recovery namespaces", () => {
  assert.deepEqual(recoveryPage("inventory.stock-count.record"), { href: "/inventory/counts", label: "halaman hitung barang" });
  assert.equal(operationLabel("inventory.stock-count.record"), "Pengamatan hitungan barang");
  assert.equal(recoveryPage("catalog.sku.create").href, "/administration/catalog");
  assert.equal(recoveryPage("person.review.propose").href, "/administration/person-reviews");
  assert.notEqual(recoveryPage("inventory.stock-count.record.extra").href, "/inventory/counts");
});
