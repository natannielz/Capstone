import assert from "node:assert/strict";
import test from "node:test";
import { parseCurrentAlias, parseSourceAlias, prepareSourceAlias, prepareSourceAliasDecision, parseSourceAliasResult,
  sourceAliasOperation, sourceAliasOutcome, sourceAliasReceipt, parseSourceAliases } from "../src/modules/inventory/stock-source-alias-workflow";
import { actorId, location } from "./stock-count-fixtures";
import { current, requestedLocation, proposal, approved, mapped, outcome, approval } from "./stock-source-alias-fixtures";
const changedSource = { ...current.source, poolVersion: "2", revisionProposalId: "90", approvedAt: "2026-09-28T00:03:00Z" };

test("alias absence and current binding preserve exact numbers and remain configuration", () => {
  assert.deepEqual(parseCurrentAlias(current), current);
  assert.deepEqual(parseCurrentAlias(mapped), mapped);
  assert.equal(parseCurrentAlias(mapped).source.settings.safetyBufferBaseUnits, "9007199254740993");
  assert.equal(parseCurrentAlias(mapped).source.settings.maximumAgeMilliseconds, "60001");
  assert.equal(Object.isFrozen(parseCurrentAlias(mapped).mapping), true);
});
test("alias state must agree with enabled flag and bound source version", () => {
  const disabled = { ...mapped, bindingState: "disabled", mapping: { ...approved, enabled: false, baseAliasVersion: "1", appliedAliasVersion: "2" } };
  assert.equal(parseCurrentAlias(disabled).bindingState, "disabled");
  assert.equal(parseCurrentAlias({ ...mapped, source: changedSource, bindingState: "stale" }).bindingState, "stale");
  for (const value of [{ ...mapped, bindingState: "absent" }, { ...current, bindingState: "current" }, { ...mapped, source: changedSource }, { ...disabled, bindingState: "current" }])
    assert.throws(() => parseCurrentAlias(value), /stock_source_alias_response_invalid/);
});
test("alias current view rejects foreign identities ownership keys and future settings", () => {
  for (const patch of [{ physicalPoolId: "999" }, { requestedLocation: { ...requestedLocation, channelId: "105" } },
    { physicalKey: "foreign" }, { ownerLocation: { ...location, warehouseId: "999" } }, { poolVersion: "2" }])
    assert.throws(() => parseCurrentAlias({ ...mapped, mapping: { ...approved, ...patch } }), /stock_source_alias_response_invalid/);
});
test("alias historical detail rejects impossible versions outcomes chronology and extra fields", () => {
  assert.deepEqual(parseSourceAlias(approved), approved);
  for (const patch of [{ proposalVersion: "2" }, { baseAliasVersion: 0 }, { baseAliasVersion: "9223372036854775807" },
    { enabled: "true" }, { status: "approved" }, { appliedAliasVersion: "1" }, { auditId: "secret" }])
    assert.throws(() => parseSourceAlias({ ...proposal, ...patch }), /stock_source_alias_response_invalid/);
  assert.throws(() => parseSourceAlias({ ...approved, appliedAliasVersion: "2" }));
  assert.throws(() => parseSourceAlias({ ...approved, decision: { ...approved.decision, decidedAt: "2026-09-27T00:00:00Z" } }));
});
test("alias scope must be a different channel in the same physical warehouse", () => {
  for (const requested of [location, { ...requestedLocation, warehouseId: "999" }, { ...requestedLocation, shopId: "999" }, { ...requestedLocation, organizationId: "999" }])
    assert.throws(() => parseSourceAlias({ ...proposal, requestedLocation: requested }));
});
test("alias proposals freeze the selected source requested channel enabled state and both versions", () => {
  const selected = structuredClone(current); const command = prepareSourceAlias(actorId, selected, true, "  Requested  ");
  assert.equal(command.expectedVersion, "1"); assert.equal(command.body.expectedAliasVersion, "0");
  assert.deepEqual(command.body, { requestedLocation, expectedAliasVersion: "0", enabled: true, reason: "Requested" });
  selected.requestedLocation.channelId = "999";
  assert.equal(command.body.requestedLocation.channelId, "104"); assert.equal(Object.isFrozen(command.current), true);
});
test("alias no-op and absent disable proposals fail before dispatch", () => {
  assert.throws(() => prepareSourceAlias(actorId, current, false, "Reason"), /stock_source_alias_input_invalid/);
  assert.throws(() => prepareSourceAlias(actorId, mapped, true, "Reason"), /stock_source_alias_input_invalid/);
  assert.throws(() => prepareSourceAlias(actorId, current, "true" as unknown as boolean, "Reason"));
});
test("alias explicitly reapproves the same enabled state against newer source settings", () => {
  const command = prepareSourceAlias(actorId, { ...mapped, source: changedSource, bindingState: "stale" }, true, "Reapprove");
  assert.equal(command.expectedVersion, "2"); assert.equal(command.body.expectedAliasVersion, "1");
});
test("alias decisions retain immutable proposal identity and reject an already terminal review", () => {
  for (const action of ["approve", "reject", "withdraw"] as const) {
    const command = prepareSourceAliasDecision(actorId, proposal, action, " Review ", " Reason ");
    assert.equal(command.proposalId, proposal.id); assert.equal(command.expectedVersion, "1");
    assert.deepEqual(command.body, { reviewReference: "Review", reason: "Reason" });
  }
  assert.throws(() => prepareSourceAliasDecision(actorId, approved, "approve", "Review", "Reason"));
});
test("alias command results enforce exact and separate base applied and pool versions", () => {
  assert.deepEqual(parseSourceAliasResult(outcome), outcome); assert.deepEqual(parseSourceAliasResult(approval), approval);
  for (const patch of [{ decisionId: "11" }, { appliedAliasVersion: "1" }, { poolVersion: 1 }, { baseAliasVersion: "01" }, { unknown: true }])
    assert.throws(() => parseSourceAliasResult({ ...outcome, ...patch }));
});
test("alias outcomes must correspond to the frozen command including the binding version", () => {
  const command = prepareSourceAlias(actorId, current, true, "Reason");
  assert.deepEqual(sourceAliasOutcome(outcome, command), outcome);
  for (const patch of [{ physicalPoolId: "999" }, { poolVersion: "2" }, { baseAliasVersion: "1" }])
    assert.throws(() => sourceAliasOutcome({ ...outcome, ...patch }, command));
  const decision = prepareSourceAliasDecision(actorId, proposal, "approve", "Review", "Reason");
  assert.deepEqual(sourceAliasOutcome(approval, decision), approval);
  assert.throws(() => sourceAliasOutcome({ ...approval, appliedAliasVersion: "2" }, decision));
  assert.throws(() => sourceAliasOutcome({ ...approval, proposalId: "999" }, decision));
});
test("alias receipt parsing cannot convert unknown or malformed results into confirmation", () => {
  assert.equal(sourceAliasReceipt({ state: "not_confirmed", result: null }, "stock.source.alias.propose"), null);
  assert.deepEqual(sourceAliasReceipt({ state: "committed", result: approval }, "stock.source.alias.approve"), approval);
  for (const value of [{ state: "not_confirmed", result: outcome }, { state: "committed", result: null }, { state: "approved", result: approval }])
    assert.throws(() => sourceAliasReceipt(value, "stock.source.alias.propose"));
  assert.throws(() => sourceAliasReceipt({ state: "committed", result: approval }, "stock.source.alias.reject"));
  assert.throws(() => sourceAliasOperation("stock.source.propose"));
});
test("alias page count sort pool and channel are validated before displaying history", () => {
  assert.deepEqual(parseSourceAliases({ total: "1", items: [approved] }, current.source.physicalPoolId, requestedLocation), { total: "1", items: [approved] });
  assert.throws(() => parseSourceAliases({ total: "2", items: [approved] }, current.source.physicalPoolId, requestedLocation));
  assert.throws(() => parseSourceAliases({ total: "1", items: [approved] }, "999", requestedLocation));
  assert.throws(() => parseSourceAliases({ total: "1", items: [approved] }, current.source.physicalPoolId, { ...requestedLocation, channelId: "105" }));
  assert.throws(() => parseSourceAliases({ total: "2", items: [approved, approved] }, current.source.physicalPoolId, requestedLocation));
});
