import { test } from "node:test";
import assert from "node:assert/strict";
import type { components } from "../src/api/generated/schema";
import { buildHistoricalGrant, historicalBranches, historicalDivisions, historicalParentMemberships, historicalReviewedBody, type HistoricalDirectoryNode } from "../src/modules/administration/historical-grant";
import { grantProfileLabel } from "../src/modules/administration/permission-labels";
import { AccessFailure, MutationLedger, PreviewSession, stableContent, type GrantPreview } from "../src/modules/administration/workflow";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";

const now = Date.parse("2026-09-28T00:00:00Z");
const membership: components["schemas"]["AccessMembershipDto"] = { id: "9007199254740993", scope: { kind: "buyingOrganization", organizationId: "10" },
  status: "active", effectiveAt: "2026-01-01T00:00:00Z", expiresAt: null, allowedActions: [], version: "1" };
const directory: HistoricalDirectoryNode[] = [
  { id: "11", kind: "branch", parentId: "10", name: "Cabang Jakarta", code: "JKT", status: "suspended", version: "1", scope: { kind: "buyingBranch", organizationId: "10", branchId: "11" } },
  { id: "12", kind: "division", parentId: "11", name: "Operasional", code: "OPS", status: "active", version: "1", scope: { kind: "buyingDivision", organizationId: "10", branchId: "11", divisionId: "12" } },
  { id: "21", kind: "branch", parentId: "20", name: "Other organization", code: null, status: "active", version: "1", scope: { kind: "buyingBranch", organizationId: "20", branchId: "21" } },
];
const input = { userId: "9007199254740994", membership, directory, kind: "buyingBranch" as const, branchId: "11", divisionId: "", permissions: ["history.read"], effective: "2026-09-28T00:00:00Z", expiry: "" };
const request = buildHistoricalGrant(input, now)!;
const preview: GrantPreview = { ...request, expiresAt: null, templateId: "historical-read", templateVersion: 1, delegablePermissions: [], existingPermissions: [], requiresIndependentApproval: true };

test("historical permissions must be explicitly selected and never widen to transaction or delegation rights", () => {
  for (const permissions of [[], ["order.read"], ["history.read", "order.submit"], ["history.read", "history.read"]])
    assert.equal(buildHistoricalGrant({ ...input, permissions }, now), null);
  assert.deepEqual(buildHistoricalGrant({ ...input, permissions: ["report.read", "history.read"] }, now)?.permissions, ["history.read", "report.read"]);
  assert.deepEqual(Object.keys(request).sort(), ["effectiveAt", "expiresAt", "membershipId", "permissions", "scope", "userId"]);
  assert.equal(grantProfileLabel("historical-read", []), "Akses baca riwayat");
});

test("only an approved active parent organization membership and authoritative descendant target can form a request", () => {
  assert.equal(historicalParentMemberships([membership], now).length, 1);
  for (const invalid of [{ ...membership, status: "pending" }, { ...membership, status: "suspended" },
    { ...membership, expiresAt: "2026-09-27T00:00:00Z" }, { ...membership, effectiveAt: "2026-10-01T00:00:00Z" },
    { ...membership, scope: { kind: "buyingBranch" as const, organizationId: "10", branchId: "11" } }])
    assert.equal(buildHistoricalGrant({ ...input, membership: invalid }, now), null);
  assert.deepEqual(historicalBranches(directory, "10").map(node => node.id), ["11"]);
  assert.deepEqual(historicalDivisions(directory, "11").map(node => node.id), ["12"]);
  assert.equal(buildHistoricalGrant({ ...input, branchId: "21" }, now), null);
  assert.equal(buildHistoricalGrant({ ...input, kind: "buyingDivision", divisionId: "99" }, now), null);
  assert.equal(buildHistoricalGrant({ ...input, kind: "buyingDivision", divisionId: "12" }, now)?.scope.divisionId, "12");
  assert.equal(buildHistoricalGrant({ ...input, directory: directory.map(node => ({ ...node, status: "revoked" })) }, now), null);
  assert.equal(buildHistoricalGrant({ ...input, expiry: "2026-09-27T00:00:00Z" }, now), null);
});

test("review freezes scope, permissions and reason and rejects excessive reasons", () => {
  const body = historicalReviewedBody(request, "  Peninjauan laporan cabang  ")!;
  assert.equal(body.reason, "Peninjauan laporan cabang");
  assert.equal(historicalReviewedBody(request, "x".repeat(501)), null);
  assert.equal(historicalReviewedBody(request, "   "), null);
  assert.notEqual(body.grant.scope, request.scope);
  assert.notEqual(body.grant.permissions, request.permissions);
  assert.throws(() => body.grant.permissions.push("report.read"), TypeError);
  assert.throws(() => { body.grant.scope.branchId = "21"; }, TypeError);
});

test("changes to actor, recipient, parent membership, scope, permission or reason invalidate historical previews", () => {
  const session = new PreviewSession<string>();
  const body = historicalReviewedBody(request, "Review")!;
  const content = stableContent({ actorId: "9007199254740995", body });
  const variants = [
    { actorId: "9007199254740996", body },
    { actorId: "9007199254740995", body: { ...body, reason: "Changed reason" } },
    ...[{ userId: "99" }, { membershipId: "98" }, { scope: { ...request.scope, branchId: "21" } }, { permissions: ["report.read"] },
      { effectiveAt: "2026-09-29T00:00:00Z" }, { expiresAt: "2026-10-01T00:00:00Z" }]
      .map(change => ({ actorId: "9007199254740995", body: { ...body, grant: { ...request, ...change } } })),
  ];
  for (const changed of variants) {
    const ticket = session.begin(content); session.replace(stableContent(changed));
    assert.equal(session.accept(ticket, preview), false); assert.equal(session.value, null);
  }
  const ticket = session.begin(content); assert.equal(session.accept(ticket, preview), true);
  session.invalidate(); assert.equal(session.value, null, "actor reset clears accepted previews");
});

test("uncertain historical submission preserves identical reviewed body/key and body-free recovery reference", async () => {
  const storage = new Map<string, string>();
  const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  const ledger = new MutationLedger(() => "historical-key");
  const body = historicalReviewedBody(request, "Private review reason")!;
  const sent: { key: string; body: unknown }[] = [];
  const submit = (send: (key: string) => Promise<unknown>, payload = body) => submitTrackedRequest(ledger, journal, "1001", "historical-grant-proposal", "access.grant.propose", payload, send);
  await assert.rejects(submit(async key => { sent.push({ key, body }); throw new AccessFailure(503, "commit_outcome_unknown"); }));
  const stored = [...storage.values()].join();
  assert.doesNotMatch(stored, /Private review|history.read|membershipId|branchId/);
  assert.equal(journal.list("1001")[0].operation, "access.grant.propose");
  await assert.rejects(submit(async () => "bad", { ...body, reason: "Changed" }), /resolve_previous_attempt/);
  await submit(async key => { sent.push({ key, body }); return { id: "90" }; });
  assert.deepEqual(sent.map(item => item.key), ["historical-key", "historical-key"]);
  assert.equal(sent[0].body, sent[1].body); assert.equal(storage.size, 0);
});
