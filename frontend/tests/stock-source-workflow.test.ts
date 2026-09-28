import assert from "node:assert/strict";
import test from "node:test";
import { actorId, location, warehouse } from "./stock-count-fixtures";
import {
  prepareSourceProposal, prepareSourceDecision, parseSourceDetail, parseSourceHistory, parseSourceResult,
  sourceOutcome, sourceReceipt, sourceWarehouses, sourceAgeSeconds, type SourceDraft,
} from "../src/modules/inventory/stock-source-workflow";

const draft = (): SourceDraft => ({ physicalKey: "pool-utama", label: "Alokasi gudang", mode: "webLedger",
  control: "exclusiveWebAllocation", maximumAgeSeconds: "60", safetyBufferBaseUnits: "0",
  externalNamespace: "", reservationProtocolReference: "", discrepancyOwnerReference: "PIC gudang",
  handoffReference: "Dokumen serah terima", reason: "Pengajuan alokasi" });
const proposed = { proposalId: "9007199254740993", proposalVersion: "1", status: "proposed", decisionId: null, physicalPoolId: null };
const detail = () => ({ ...prepareSourceProposal(actorId, location, draft()).body, id: proposed.proposalId,
  proposalVersion: "1", status: "proposed", proposedAt: "2026-09-28T00:00:00Z", decision: null, physicalPoolId: null });

test("source proposal preserves exact base units and makes a detached review snapshot", () => {
  const input = { ...draft(), safetyBufferBaseUnits: "9223372036854775807" };
  const command = prepareSourceProposal(actorId, location, input);
  assert.equal(command.body.maximumAgeMilliseconds, "60000");
  assert.equal(command.body.safetyBufferBaseUnits, "9223372036854775807");
  assert.equal(command.operation, "stock.source.propose");
  input.label = "changed";
  assert.equal(command.body.label, "Alokasi gudang");
  assert.ok(Object.isFrozen(command) && Object.isFrozen(command.body) && Object.isFrozen(command.body.location));
  assert.equal(sourceAgeSeconds("60001"), "60.001");
});

test("source configuration requires explicit modes, quantities and complete external handoff", () => {
  for (const change of [{ mode: "" }, { control: "" }, { maximumAgeSeconds: "" }, { maximumAgeSeconds: "01" },
    { maximumAgeSeconds: "0" }, { maximumAgeSeconds: "922337203686" }, { maximumAgeSeconds: "1.5" },
    { safetyBufferBaseUnits: "-1" }, { safetyBufferBaseUnits: "00" }, { safetyBufferBaseUnits: 1 },
    { physicalKey: "POOL" }, { label: "\ud800" }, { reason: "" }, { mode: "externalLedger", control: "externalHold" },
    { reservationProtocolReference: "unneeded" }])
    assert.throws(() => prepareSourceProposal(actorId, location, { ...draft(), ...change } as SourceDraft), /stock_source_input_invalid/);
  const external = prepareSourceProposal(actorId, location, { ...draft(), mode: "externalLedger", control: "externalHold",
    externalNamespace: "omi-synthetic", reservationProtocolReference: "Protokol reservasi uji" });
  assert.equal(external.body.externalNamespace, "omi-synthetic");
});

test("source outcomes reject mismatched statuses, numeric IDs and invalid pool relationships", () => {
  const command = prepareSourceProposal(actorId, location, draft());
  assert.equal(sourceOutcome(proposed, command).proposalId, proposed.proposalId);
  for (const change of [{ proposalId: 9007199254740992 }, { proposalVersion: "2" }, { decisionId: "20" },
    { physicalPoolId: "21" }, { status: "active" }, { status: "approved" }])
    assert.throws(() => sourceOutcome({ ...proposed, ...change }, command), /stock_source_response_invalid/);
  assert.throws(() => parseSourceResult({ ...proposed, status: "approved", decisionId: "20", physicalPoolId: null }), /stock_source_response_invalid/);
});

test("source detail validates immutable settings and strips unexpected private fields", () => {
  const parsed = parseSourceDetail({ ...detail(), proposedBy: "private", bodyHash: "private" });
  assert.equal(parsed.id, proposed.proposalId);
  assert.equal("proposedBy" in parsed, false);
  assert.equal("bodyHash" in parsed, false);
  for (const change of [{ maximumAgeMilliseconds: "00" }, { proposedAt: "2026-02-30T00:00:00Z" },
    { status: "approved" }, { physicalPoolId: "20" }, { decision: { status: "rejected", decidedAt: "2026-09-29T00:00:00Z", reviewReference: "ref", reason: "reason" } }])
    assert.throws(() => parseSourceDetail({ ...detail(), ...change }), /stock_source_response_invalid/);
});

test("source history validates location, page counts, duplicates and canonical totals", () => {
  const row = detail();
  assert.equal(parseSourceHistory({ total: "1", items: [row] }, location).items.length, 1);
  for (const value of [{ total: "0", items: [row] }, { total: "2", items: [row, row] }, { total: 1, items: [row] },
    { total: "1", items: [{ ...row, location: { ...location, warehouseId: "999" } }] }])
    assert.throws(() => parseSourceHistory(value, location), /stock_source_response_invalid/);
});

test("review and withdrawal keep the original immutable proposal identity", () => {
  const row = parseSourceDetail(detail());
  for (const decision of ["approve", "reject", "withdraw"] as const) {
    const command = prepareSourceDecision(actorId, row, decision, "Tinjauan", "Alasan keputusan");
    assert.equal(command.proposalId, row.id); assert.equal(command.expectedVersion, "1");
    assert.ok(Object.isFrozen(command.proposal) && Object.isFrozen(command.body));
    const status = decision === "approve" ? "approved" : decision === "reject" ? "rejected" : "withdrawn";
    const result = { ...proposed, status, decisionId: "22", physicalPoolId: decision === "approve" ? "23" : null };
    assert.equal(sourceOutcome(result, command).status, status);
    assert.throws(() => sourceOutcome({ ...result, proposalId: "999" }, command), /stock_source_response_invalid/);
  }
});

test("receipt recovery reports absence without inventing success and binds the operation", () => {
  assert.equal(sourceReceipt({ state: "not_confirmed", result: null }, "stock.source.propose"), null);
  assert.equal(sourceReceipt({ state: "committed", result: proposed }, "stock.source.propose")?.proposalId, proposed.proposalId);
  assert.throws(() => sourceReceipt({ state: "committed", result: proposed }, "stock.source.approve"), /stock_source_response_invalid/);
  assert.throws(() => sourceReceipt({ state: "not_confirmed", result: proposed }, "stock.source.propose"), /stock_source_response_invalid/);
  assert.throws(() => sourceReceipt({ state: "closed", result: null }, "stock.source.propose"), /stock_source_response_invalid/);
});

test("source warehouse choices preserve scope and reject duplicate or invented references", () => {
  assert.deepEqual(sourceWarehouses({ items: [warehouse] }), [warehouse]);
  assert.throws(() => sourceWarehouses({ items: [warehouse, warehouse] }), /stock_source_response_invalid/);
  assert.throws(() => sourceWarehouses({ items: [{ ...warehouse, location: { ...location, warehouseId: 102 } }] }), /stock_source_response_invalid/);
});
