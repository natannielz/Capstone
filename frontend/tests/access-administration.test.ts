import { test } from "node:test";
import assert from "node:assert/strict";
import { AccessFailure, MutationLedger, PreviewSession, stableContent, submitLogicalRequest } from "../src/modules/administration/workflow";
import { validReference } from "../src/modules/organizations/access-scope";
import type { GrantRequest, GrantPreview } from "../src/modules/administration/workflow";

const request: GrantRequest = {
  userId: "9007199254740993", membershipId: "9223372036854775807", templateId: "buyer", financePacks: [],
  additionalPermissions: [], scope: { kind: "buyerOwned", organizationId: "1", branchId: "11" },
  effectiveAt: "2026-09-28T03:00:00Z", expiresAt: null,
};
const preview: GrantPreview = { ...request, expiresAt: null, delegablePermissions: [], templateVersion: 1, permissions: ["order.read"], existingPermissions: [], requiresIndependentApproval: true };

test("account references retain exact Int64 strings beyond JavaScript's safe integer boundary", () => {
  assert.equal(validReference(request.userId), true);
  assert.equal(validReference(request.membershipId), true);
  for (const value of ["0", "01", "-1", "1e3", "9223372036854775808", "9.1", "1\n"]) assert.equal(validReference(value), false, value);
  assert.match(stableContent(request), /"9007199254740993"/);
});

test("a stale preview cannot authorize a changed scope, membership, or role draft", () => {
  const session = new PreviewSession();
  const ticket = session.begin(request);
  session.replace({ ...request, membershipId: "8", scope: { kind: "buyingBranch", organizationId: "1", branchId: "12" } });
  assert.equal(session.accept(ticket, preview), false);
  assert.equal(session.value, null);
  const newer = session.begin(request);
  assert.equal(session.accept(newer, preview), true);
  session.invalidate();
  assert.equal(session.value, null);
});

test("out-of-order previews and a fresh preview invalidate previous acceptance", () => {
  const session = new PreviewSession();
  const first = session.begin(request);
  const second = session.begin(request);
  assert.equal(session.accept(first, preview), false);
  assert.equal(session.accept(second, preview), true);
  session.begin(request);
  assert.equal(session.value, null);
});

test("unknown commit and network retries reuse one immutable request key", async () => {
  let sequence = 0;
  const ledger = new MutationLedger(() => `key-${++sequence}`);
  const keys: string[] = [];
  const body = { grant: request, reason: "Tugas cabang disetujui" };
  await assert.rejects(submitLogicalRequest(ledger, "grant-proposal", body, async key => {
    keys.push(key); throw new AccessFailure(503, "commit_outcome_unknown");
  }));
  assert.equal(ledger.isUncertain("grant-proposal"), true);
  await assert.rejects(submitLogicalRequest(ledger, "grant-proposal", { ...body, reason: "Changed" }, async () => "bad"), /resolve_previous_attempt/);
  await assert.rejects(submitLogicalRequest(ledger, "grant-proposal", body, async key => {
    keys.push(key); throw new TypeError("Network disconnected");
  }));
  await submitLogicalRequest(ledger, "grant-proposal", body, async key => { keys.push(key); return { id: "99" }; });
  assert.deepEqual(keys, ["key-1", "key-1", "key-1"]);
  assert.equal(ledger.isUncertain("grant-proposal"), false);
  await submitLogicalRequest(ledger, "grant-proposal", body, async key => { keys.push(key); return { id: "100" }; });
  assert.equal(keys[3], "key-2");
});

test("a denied retry does not resolve a previously unknown outcome", async () => {
  const ledger = new MutationLedger(() => "stable-key");
  await assert.rejects(submitLogicalRequest(ledger, "approve:77", { id: "77", version: "2" }, async () => { throw new AccessFailure(503, "commit_outcome_unknown"); }));
  await assert.rejects(submitLogicalRequest(ledger, "approve:77", { id: "77", version: "2" }, async () => { throw new AccessFailure(403, "stale_or_inactive_actor"); }));
  assert.equal(ledger.isUncertain("approve:77"), true);
  assert.throws(() => ledger.begin("approve:77", { id: "77", version: "3" }), /resolve_previous_attempt/);
});

test("known conflicts invalidate preview and changed content receives a new key", async () => {
  let sequence = 0;
  const ledger = new MutationLedger(() => `key-${++sequence}`);
  const previewSession = new PreviewSession();
  const ticket = previewSession.begin(request);
  previewSession.accept(ticket, preview);
  await assert.rejects(submitLogicalRequest(ledger, "grant-proposal", request, async () => { throw new AccessFailure(409, "version_conflict"); }));
  previewSession.invalidate();
  assert.equal(previewSession.value, null);
  assert.equal(ledger.begin("grant-proposal", { ...request, membershipId: "8" }), "key-2");
});

test("duplicate clicks cannot submit concurrently and object property order does not rotate a retry key", () => {
  const ledger = new MutationLedger(() => "one-key");
  assert.equal(ledger.begin("revoke:22", { reason: "Perubahan tugas", version: "9007199254740993" }), "one-key");
  assert.throws(() => ledger.begin("revoke:22", { version: "9007199254740993", reason: "Perubahan tugas" }), /request_in_progress/);
  ledger.fail("revoke:22", new AccessFailure(409, "version_conflict"));
  assert.equal(ledger.begin("revoke:22", { version: "9007199254740993", reason: "Perubahan tugas" }), "one-key");
});
