import assert from "node:assert/strict";
import test from "node:test";
import { identityReturnPath, identityDestinations, loginFields, stepUpFields, canUseStockSources } from "../src/modules/identity/identity-workflow";
import { recoveryPage, operationLabel } from "../src/modules/administration/operation-recovery-navigation";
import { sourceSession } from "./stock-source-fixtures";
const path = "/inventory/sources/policies";
test("policy task discovery separates maker checker and history from source management", () => {
  for (const action of ["stock.source.policy.propose", "stock.source.policy.approve"]) {
    assert.deepEqual(identityDestinations([action]).map(item => item.href), [path]);
    assert.equal(canUseStockSources([action]), false);
  }
  assert.ok(identityDestinations(["stock.source.read"]).some(item => item.href === path));
  for (const action of ["stock.count", "stock.source.propose", "stock.source.approve", "stock.source.suspend", "technical.configure", "stock.source.policy.reject"])
    assert.equal(identityDestinations([action]).some(item => item.href === path), false);
});
test("policy sign-in and reviewer verification preserve only the exact return destination", () => {
  assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, "", ""), { returnPath: path });
  const session = { ...sourceSession, stepUpOperations: ["stock.source.policy.approve"] };
  assert.equal(stepUpFields(session, "stock.source.policy.approve", path).returnPath, path);
  for (const suffix of ["/", "/other", "?returnPath=https://outside.test", "#other", "\n"]) {
    assert.notEqual(identityReturnPath(path + suffix), path + suffix);
    assert.throws(() => loginFields(path + suffix, "", ""));
    assert.throws(() => stepUpFields(session, "stock.source.policy.approve", path + suffix));
  }
  for (const operation of ["stock.source.policy.propose", "stock.source.policy.withdraw", "stock.source.policy.reject"])
    assert.throws(() => stepUpFields(session, operation, path), /step_up_not_allowed/);
});
test("all four policy recovery references route to their owning desk with distinct labels", () => {
  const labels = ["Pengajuan kebijakan operasional stok", "Persetujuan kebijakan operasional stok", "Penolakan kebijakan operasional stok", "Penarikan kebijakan operasional stok"];
  ["propose", "approve", "reject", "withdraw"].forEach((action, index) => {
    const operation = "stock.source.policy." + action;
    assert.equal(recoveryPage(operation).href, path); assert.equal(operationLabel(operation), labels[index]);
  });
  for (const operation of ["stock.source.policy", "stock.source.policy.close", "stock.source.policy.approve.extra"])
    assert.notEqual(recoveryPage(operation).href, path);
  assert.equal(recoveryPage("stock.source.propose").href, "/inventory/sources");
});