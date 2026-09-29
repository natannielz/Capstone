import assert from "node:assert/strict";
import test from "node:test";
import { parseSuspensionPreview, parseSourceSuspension, parseSourceSuspensions, prepareSourceSuspension,
  parseSuspensionResult, suspensionOutcome, suspensionReceipt } from "../src/modules/inventory/stock-source-suspension-workflow";
import { actorId, location } from "./stock-count-fixtures";
import { pool, preview, outcome, detail } from "./stock-source-suspension-fixtures";

test("suspension preview preserves exact versions and freezes every affected location", () => {
  const input = structuredClone(preview), result = parseSuspensionPreview(input);
  input.affectedLocations[0].channelId = "999";
  assert.deepEqual(result, preview); assert.equal(Object.isFrozen(result.affectedLocations[0]), true);
});
test("suspension preview rejects malformed versions hashes private fields and mixed warehouse scopes", () => {
  for (const patch of [{ poolVersion: 1 }, { suspensionVersion: "01" }, { suspensionVersion: "9223372036854775808" },
    { configurationHash: "A".repeat(64) }, { configurationHash: "a".repeat(64) + "\n" }, { active: "false" }, { auditId: "secret" },
    { affectedLocations: [] }, { affectedLocations: [location, location] }, { affectedLocations: [...preview.affectedLocations].reverse() },
    { affectedLocations: [location, { ...location, warehouseId: "999", channelId: "104" }] },
    { affectedLocations: [{ ...location, personId: "secret" }] }])
    assert.throws(() => parseSuspensionPreview({ ...preview, ...patch }), /stock_source_suspension_response_invalid/);
});
test("suspension review freezes the exact preview reason actor and expected versions", () => {
  const selected = structuredClone(preview), command = prepareSourceSuspension(actorId, selected, " Reason ");
  selected.configurationHash = "b".repeat(64);
  assert.equal(command.operation, "stock.source.suspend"); assert.equal(command.poolId, pool); assert.equal(command.expectedVersion, preview.poolVersion);
  assert.deepEqual(command.body, { expectedSuspensionVersion: "0", configurationHash: preview.configurationHash, reason: "Reason" });
  assert.equal(Object.isFrozen(command.preview.affectedLocations), true);
  for (const input of [{ ...preview, suspensionVersion: "9223372036854775807" }, { ...preview, physicalPoolId: "0" }])
    assert.throws(() => prepareSourceSuspension(actorId, input, "Reason"), /stock_source_suspension_input_invalid/);
  assert.throws(() => prepareSourceSuspension(actorId, preview, " "));
});
test("suspension history exposes only historical facts with an included canonical owner", () => {
  assert.deepEqual(parseSourceSuspension(detail), detail);
  for (const patch of [{ ownerLocation: { ...location, channelId: "999" } }, { physicalKey: "not valid" },
    { wasActive: "false" }, { suspensionVersion: "0" }, { suspendedAt: "2026-02-30T00:00:00Z" }, { suspendedBy: actorId }])
    assert.throws(() => parseSourceSuspension({ ...detail, ...patch }), /stock_source_suspension_response_invalid/);
});
test("suspension result must match pool and reviewed versions plus exactly one stop", () => {
  const command = prepareSourceSuspension(actorId, preview, "Reason"); assert.deepEqual(suspensionOutcome(outcome, command), outcome);
  for (const patch of [{ physicalPoolId: "999" }, { poolVersion: "1" }, { suspensionVersion: "2" }])
    assert.throws(() => suspensionOutcome({ ...outcome, ...patch }, command));
  for (const patch of [{ status: "active" }, { suspensionId: 1 }, { suspensionVersion: "0" }, { secret: true }])
    assert.throws(() => parseSuspensionResult({ ...outcome, ...patch }));
});
test("suspension receipts cannot turn missing or malformed outcomes into confirmation", () => {
  assert.equal(suspensionReceipt({ state: "not_confirmed", result: null }), null);
  assert.deepEqual(suspensionReceipt({ state: "committed", result: outcome }), outcome);
  for (const value of [{ state: "not_confirmed", result: outcome }, { state: "committed", result: null },
    { state: "suspended", result: outcome }, { state: "committed", result: outcome, request: "private" }]) assert.throws(() => suspensionReceipt(value));
});
test("suspension history validates descending revisions exact page counts identity and chronology", () => {
  const newest = { ...detail, id: "9007199254741000", suspensionVersion: "2", suspendedAt: "2026-09-29T00:01:00Z" };
  assert.deepEqual(parseSourceSuspensions({ total: "2", items: [newest, detail] }, pool), { total: "2", items: [newest, detail] });
  assert.deepEqual(parseSourceSuspensions({ total: "2", items: [detail] }, pool, 1, 1).items, [detail]);
  assert.deepEqual(parseSourceSuspensions({ total: "2", items: [] }, pool, 2).items, []);
  for (const items of [[detail, newest], [detail, detail], [{ ...newest, physicalPoolId: "999" }, detail],
    [{ ...newest, suspendedAt: "2026-09-28T00:00:00Z" }, detail], [{ ...newest, poolVersion: "1" }, detail],
    [newest, { ...detail, id: newest.id }], [{ ...newest, physicalKey: "foreign" }, detail]])
    assert.throws(() => parseSourceSuspensions({ total: "2", items }, pool));
  assert.throws(() => parseSourceSuspensions({ total: "2", items: [detail] }, pool));
  assert.throws(() => parseSourceSuspensions({ total: "1", items: [detail] }, "999"));
  assert.throws(() => parseSourceSuspensions({ total: "1", items: [detail] }, pool, -1));
});
