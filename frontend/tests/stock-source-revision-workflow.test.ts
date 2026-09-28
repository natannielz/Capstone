import assert from "node:assert/strict";
import test from "node:test";
import { parseCurrentSource, parseSourceRevision, prepareSourceRevision, sourceRevisionOutcome, prepareSourceRevisionDecision, parseSourceRevisionResult, parseCurrentSources, parseSourceRevisions, sourceRevisionReceipt } from "../src/modules/inventory/stock-source-revision-workflow";
import { location, actorId } from "./stock-count-fixtures";
import { current, proposal, outcome, settings } from "./stock-source-revision-fixtures";
test("revision workflow retains exact quantities and freezes old versus proposed settings", () => {
  assert.deepEqual(parseCurrentSource(current), current);
  assert.deepEqual(parseSourceRevision(proposal), proposal);
  const command = prepareSourceRevision(actorId, current, { ...proposal.proposedSettings, reason: proposal.reason });
  assert.ok(Object.isFrozen(command));
  assert.deepEqual(sourceRevisionOutcome(outcome, command), outcome);
});

for (const [name, change] of [
  ["numeric version", { poolVersion: 1 }], ["leading zero", { poolVersion: "01" }], ["overflow", { poolVersion: "9223372036854775808" }],
  ["unexplained first revision", { revisionProposalId: "9" }], ["unexplained later revision", { poolVersion: "2" }],
  ["invented acceptance", { poolState: "accepted" }], ["rounded settings", { settings: { ...settings, safetyBufferBaseUnits: 9007199254740992 } }],
  ["invalid calendar date", { approvedAt: "2026-02-30T00:00:00Z" }],
] as const) test("current source rejects " + name, () => {
  assert.throws(() => parseCurrentSource({ ...current, ...change }), /stock_source_revision_response_invalid/);
});
const decision = { status: "approved", decidedAt: "2026-09-28T00:02:00Z", reviewReference: "Reviewed", reason: "Accepted configuration" };
for (const [name, change] of [
  ["physical identity change", { proposedSettings: { ...proposal.proposedSettings, physicalKey: "different" } }],
  ["owner change", { proposedSettings: { ...proposal.proposedSettings, location: { ...location, channelId: "999" } } }],
  ["no change", { proposedSettings: settings }], ["backdated decision", { status: "approved", appliedPoolVersion: "2", decision: { ...decision, decidedAt: "2026-09-27T00:00:00Z" } }],
  ["unexplained approved version", { status: "approved", appliedPoolVersion: "3", decision }],
  ["applied rejection", { status: "rejected", appliedPoolVersion: "2", decision: { ...decision, status: "rejected" } }],
  ["missing approval", { status: "approved", appliedPoolVersion: "2" }],
] as const) test("revision detail rejects " + name, () => {
  assert.throws(() => parseSourceRevision({ ...proposal, ...change }), /stock_source_revision_response_invalid/);
});
test("revision proposal review freezes a separate copy and preserves fractional-second source age", () => {
  const body = { ...proposal.proposedSettings, reason: proposal.reason };
  const reviewed = prepareSourceRevision(actorId, current, body);
  body.label = "Edited after review";
  assert.equal(reviewed.body.label, "Revised"); assert.equal(reviewed.body.maximumAgeMilliseconds, "60001");
  assert.ok(Object.isFrozen(reviewed.body)); assert.ok(Object.isFrozen(reviewed.current.settings));
  assert.throws(() => prepareSourceRevision(actorId, current, { ...settings, reason: proposal.reason }), /stock_source_revision_input_invalid/);
  assert.throws(() => prepareSourceRevision(actorId, current, { ...body, physicalKey: "another" }), /stock_source_revision_input_invalid/);
});
test("revision decision review and result bind pool proposal status and approved version", () => {
  const reviewed = prepareSourceRevisionDecision(actorId, proposal, "approve", "Review", "Reason");
  const approved = { ...outcome, status: "approved", basePoolVersion: null, appliedPoolVersion: "2", decisionId: "13" };
  assert.deepEqual(sourceRevisionOutcome(approved, reviewed), approved);
  for (const changed of [{ physicalPoolId: "999" }, { proposalId: "999" }, { appliedPoolVersion: "3" }, { basePoolVersion: "1" }])
    assert.throws(() => sourceRevisionOutcome({ ...approved, ...changed }, reviewed), /stock_source_revision_response_invalid/);
  assert.throws(() => prepareSourceRevisionDecision(actorId, { ...proposal, status: "approved", decision, appliedPoolVersion: "2" }, "approve", "Review", "Reason"), /stock_source_revision_input_invalid/);
});
test("revision pages reject duplicate unordered foreign incomplete and wrong-pool results", () => {
  assert.deepEqual(parseCurrentSources({ total: "1", items: [current] }, location).items[0], current);
  assert.deepEqual(parseSourceRevisions({ total: "1", items: [proposal] }, location, current.physicalPoolId).items[0], proposal);
  assert.throws(() => parseSourceRevisions({ total: "1", items: [proposal] }, location, "999"), /stock_source_revision_response_invalid/);
  for (const value of [{ total: "2", items: [proposal] }, { total: "2", items: [proposal, proposal] },
    { total: "2", items: [{ ...proposal, id: "13" }, proposal] }, { total: 1, items: [proposal] }])
    assert.throws(() => parseSourceRevisions(value, location), /stock_source_revision_response_invalid/);
  assert.throws(() => parseCurrentSources({ total: "1", items: [{ ...current, settings: { ...settings, location: { ...location, channelId: "999" } } }] }, location), /stock_source_revision_response_invalid/);
  assert.deepEqual(parseSourceRevisions({ total: "1", items: [] }, location, null, 25).items, []);
});
test("revision receipts preserve unknown outcomes and reject impossible or unrelated results", () => {
  assert.equal(sourceRevisionReceipt({ state: "not_confirmed", result: null }, "stock.source.revision.propose"), null);
  assert.deepEqual(sourceRevisionReceipt({ state: "committed", result: outcome }, "stock.source.revision.propose"), outcome);
  assert.throws(() => sourceRevisionReceipt({ state: "not_confirmed", result: outcome }, "stock.source.revision.propose"), /stock_source_revision_response_invalid/);
  assert.throws(() => sourceRevisionReceipt({ state: "committed", result: outcome }, "stock.source.revision.approve"), /stock_source_revision_response_invalid/);
  assert.throws(() => parseSourceRevisionResult({ ...outcome, decisionId: "99" }), /stock_source_revision_response_invalid/);
});
