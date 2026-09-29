import assert from "node:assert/strict";
import test from "node:test";
import { parsePolicyPreview, parsePolicyReview, parseSourcePolicy, parsePolicyLatest, parsePolicyHistory, preparePolicyProposal,
  preparePolicyDecision, parsePolicyResult, policyOutcome, policyReceipt, sourcePolicyOperation } from "../src/modules/inventory/stock-source-policy-workflow";
import { actorId } from "./stock-count-fixtures";
import { pool, configuration, preview, input, proposal, review, outcome, approvedOutcome, approved } from "./stock-source-policy-fixtures";

test("policy snapshots retain exact quantities and reviewed versions and freeze nested facts", () => {
  assert.deepEqual(parsePolicyPreview(preview), preview); assert.deepEqual(parsePolicyReview(review), review);
  assert.deepEqual(parseSourcePolicy(approved), approved);
  const command = preparePolicyProposal(actorId, preview, input, "Reason");
  assert.equal(command.expectedVersion, "9007199254740995");
  assert.equal(command.body.expectedPolicyVersion, "9007199254740996");
  assert.equal(command.preview.configuration.settings.safetyBufferBaseUnits, "9007199254740993");
  assert.ok(Object.isFrozen(command.preview.configuration.mappings[0].location));
  assert.ok(Object.isFrozen(command.body.input));
  assert.equal("safetyBufferBaseUnits" in command.body.input, false);
});
test("policy parsers reject hidden fields ambiguous numbers inconsistent decisions and unsafe mapping snapshots", () => {
  const faults = [
    { ...preview, proposedBy: actorId }, { ...preview, poolVersion: 9007199254740995 }, { ...preview, suspensionVersion: "02" },
    { ...preview, configurationHash: "a".repeat(64) + "\n" }, { ...preview, configurationHash: "A".repeat(64) },
    { ...preview, configuration: { ...configuration, active: true } },
    { ...preview, configuration: { ...configuration, settings: { ...configuration.settings, bodyHash: "private" } } },
    { ...preview, configuration: { ...configuration, settings: { ...configuration.settings, location: { ...configuration.settings.location, extra: "private" } } } },
    { ...preview, configuration: { ...configuration, mappings: [{ ...configuration.mappings[0], poolVersion: "1" }] } },
    { ...preview, configuration: { ...configuration, mappings: [configuration.mappings[0], configuration.mappings[0]] } },
    { ...preview, configuration: { ...configuration, mappings: [{ ...configuration.mappings[0], location: configuration.settings.location }] } },
  ];
  for (const value of faults) assert.throws(() => parsePolicyPreview(value), /stock_source_policy_response_invalid/);
  for (const value of [
    { ...proposal, input: { ...input, feedIntervalMilliseconds: 0 } }, { ...proposal, input: { ...input, externalTenant: "tenant" } },
    { ...proposal, input: { ...input, feedIntervalMilliseconds: "1" } }, { ...proposal, decisionId: "1" },
    { ...proposal, proposedAt: "2026-09-28T23:59:59Z" }, { ...approved, policyVersion: "1" },
    { ...approved, decision: { ...approved.decision, status: "rejected" } }, { ...approved, policyId: null },
  ]) assert.throws(() => parseSourcePolicy(value), /stock_source_policy_response_invalid/);
});
test("external policies require explicit bounded feed and tenant for the reviewed source", () => {
  const external = { ...preview, configuration: { ...configuration, settings: { ...configuration.settings, mode: "externalLedger",
    control: "externalHold", externalNamespace: "provider", reservationProtocolReference: "External hold contract" } } };
  const externalInput = { ...input, writer: "externalSystem", externalTenant: "Tenant-A", feedIntervalMilliseconds: "922337203685477" };
  assert.equal(preparePolicyProposal(actorId, external, externalInput, "Reason").body.input.feedIntervalMilliseconds, "922337203685477");
  for (const wrong of [{ ...externalInput, externalTenant: null }, { ...externalInput, externalTenant: " Tenant-A" },
    { ...externalInput, feedIntervalMilliseconds: "0" }, { ...externalInput, feedIntervalMilliseconds: "922337203685478" },
    { ...externalInput, writer: "unitToko" }, { ...externalInput, extra: "unsafe" }])
    assert.throws(() => preparePolicyProposal(actorId, external, wrong, "Reason"), /stock_source_policy_input_invalid/);
});
test("stale policy rejection and withdrawal stay reviewable but cannot become approval", () => {
  for (const action of ["reject", "withdraw"]) {
    const stale = { ...review, action, bindingCurrent: false, current: { ...review.current, suspensionVersion: "3" } };
    assert.equal(preparePolicyDecision(actorId, stale, "Review", "Reason").operation, "stock.source.policy." + action);
  }
  for (const wrong of [
    { ...review, bindingCurrent: false }, { ...review, action: "unknown" },
    { ...review, current: { ...review.current, suspensionVersion: "3" } },
    { ...review, current: { ...review.current, policyVersion: "0" } }, { ...review, proposal: approved },
  ]) assert.throws(() => parsePolicyReview(wrong), /stock_source_policy_response_invalid/);
});
test("policy history preserves all states in reverse exact proposal order with strict totals and scope", () => {
  const older = { ...proposal, id: "9007199254740996" };
  assert.deepEqual(parsePolicyHistory({ total: "2", items: [approved, older] }, pool).items, [approved, older]);
  assert.deepEqual(parsePolicyHistory({ total: "2", items: [] }, pool, 2).items, []);
  for (const page of [{ total: "2", items: [older, approved] }, { total: "2", items: [proposal, proposal] },
    { total: "1", items: [{ ...proposal, physicalPoolId: "999" }] }, { total: 1, items: [proposal] },
    { total: "1", items: [] }, { total: "1", items: [proposal], personApproval: {} }])
    assert.throws(() => parsePolicyHistory(page, pool), /stock_source_policy_response_invalid/);
  assert.deepEqual(parsePolicyLatest({ bindingCurrent: false, policy: null }), { bindingCurrent: false, policy: null });
  assert.deepEqual(parsePolicyLatest({ bindingCurrent: false, policy: approved }).policy, approved);
  assert.throws(() => parsePolicyLatest({ bindingCurrent: true, policy: null }));
  assert.throws(() => parsePolicyLatest({ bindingCurrent: false, policy: proposal }));
});
test("policy outcomes reconcile the frozen proposal pool operation and exact applied version", () => {
  const propose = preparePolicyProposal(actorId, preview, input, "Reason"), approve = preparePolicyDecision(actorId, review, "Review", "Reason");
  assert.deepEqual(policyOutcome(outcome, propose), outcome); assert.deepEqual(policyOutcome(approvedOutcome, approve), approvedOutcome);
  for (const wrong of [{ ...approvedOutcome, proposalId: "999" }, { ...approvedOutcome, physicalPoolId: "999" },
    { ...approvedOutcome, policyVersion: "9007199254740998" }, { ...approvedOutcome, policyId: null },
    { ...approvedOutcome, basePolicyVersion: "0" }]) assert.throws(() => policyOutcome(wrong, approve));
  assert.throws(() => policyOutcome({ ...outcome, basePolicyVersion: "0" }, propose));
  for (const action of ["reject", "withdraw"]) {
    const result = { ...outcome, status: action === "reject" ? "rejected" : "withdrawn", basePolicyVersion: null, decisionId: "999" };
    assert.deepEqual(policyOutcome(result, preparePolicyDecision(actorId, { ...review, action }, "Review", "Reason")), result);
  }
  assert.throws(() => parsePolicyResult({ ...outcome, auditId: "1" }));
});
test("receipt validation accepts only the four policy operations and matching committed states", () => {
  assert.equal(policyReceipt({ state: "not_confirmed", result: null }, "stock.source.policy.propose"), null);
  assert.deepEqual(policyReceipt({ state: "committed", result: approvedOutcome }, "stock.source.policy.approve"), approvedOutcome);
  for (const op of ["stock.source.approve", "stock.source.policy", "stock.source.policy.retry", "stock.source.policy.close"])
    assert.throws(() => sourcePolicyOperation(op));
  assert.throws(() => policyReceipt({ state: "committed", result: outcome }, "stock.source.policy.approve"));
  assert.throws(() => policyReceipt({ state: "not_confirmed", result: outcome }, "stock.source.policy.propose"));
});