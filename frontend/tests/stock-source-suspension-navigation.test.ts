import assert from "node:assert/strict";
import test from "node:test";
import { identityReturnPath, identityDestinations, loginFields, stepUpFields, canUseStockSources, canUseStockSourceSuspensions } from "../src/modules/identity/identity-workflow";
import { recoveryPage, operationLabel } from "../src/modules/administration/operation-recovery-navigation";
import { sourceSession } from "./stock-source-fixtures";
const path = "/inventory/sources/suspensions";
test("suspension navigation preserves exact login and MFA return without granting source-management access", () => {
  assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, "", ""), { returnPath: path });
  assert.equal(stepUpFields({ ...sourceSession, stepUpOperations: ["stock.source.suspend"] }, "stock.source.suspend", path).returnPath, path);
  for (const permission of ["stock.source.read", "stock.source.suspend"]) assert.ok(identityDestinations([permission]).some(item => item.href === path));
  assert.equal(canUseStockSources(["stock.source.suspend"]), false);
  assert.deepEqual(identityDestinations(["stock.source.suspend"]).map(item => item.href), [path]);
  for (const permission of ["stock.count", "stock.source.propose", "stock.source.approve", "technical.configure"]) {
    assert.equal(canUseStockSourceSuspensions([permission]), false);
    assert.equal(identityDestinations([permission]).some(item => item.href === path), false);
  }
  assert.equal(recoveryPage("stock.source.suspend").href, path); assert.equal(operationLabel("stock.source.suspend"), "Penghentian sumber stok");
});
test("suspension login and MFA reject modified return destinations and an ungranted operation", () => {
  for (const suffix of ["/", "/other", "?returnPath=https://outside.test", "#other", "\n"]) {
    assert.notEqual(identityReturnPath(path + suffix), path + suffix);
    assert.throws(() => loginFields(path + suffix, "", ""));
  }
  assert.throws(() => stepUpFields(sourceSession, "stock.source.suspend", path), /step_up_not_allowed/);
});
