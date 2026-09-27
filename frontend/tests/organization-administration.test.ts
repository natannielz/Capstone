import { test } from "node:test";
import assert from "node:assert/strict";
import { createUnitTokoClient } from "./helpers/api-client";
import { createOrganizationGateway } from "../src/modules/organizations/organization-client";
import { editOrganizationRequest, newOrganizationRequest, validOrganizationDraft } from "../src/modules/organizations/organization-workflow";
import { AccessFailure, MutationLedger } from "../src/modules/administration/workflow";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";

test("reviewed organization edit preserves immutable references and exact versions without inventing legacy names", () => {
  const request = editOrganizationRequest({ id: "9007199254740993", parentId: "9007199254740995", kind: "branch", status: "suspended",
    scopeVersion: "9007199254740997", profileVersion: null, name: null, code: null, allowedActions: ["reactivate"] }, "reactivate");
  assert.equal(request.scopeId, "9007199254740993"); assert.equal(request.parentId, "9007199254740995");
  assert.equal(request.expectedScopeVersion, "9007199254740997"); assert.equal(request.expectedProfileVersion, "0");
  assert.equal(request.name, ""); assert.equal(request.code, "");
  assert.equal(validOrganizationDraft(request, "Periksa"), false);
  assert.equal(validOrganizationDraft({ ...request, name: "Tebet", code: "TEBET" }, "Periksa"), true);
  assert.equal(validOrganizationDraft({ ...request, name: "Tebet", code: "TEBET" }, "x".repeat(1001)), false);
});

test("organization reads and commands bind the expected account and preserve Int64 precision", async () => {
  const calls: Request[] = [];
  const gateway = createOrganizationGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); calls.push(input);
    assert.equal(input.headers.get("X-Expected-Actor-Id"), "9007199254740999");
    assert.equal(input.cache, "no-store"); assert.equal(input.credentials, "same-origin"); assert.equal(input.headers.has("Authorization"), false);
    if (input.method === "GET") return Response.json({ id: "9007199254740993", scopeVersion: "9007199254740995", allowedActions: [] });
    assert.equal(input.headers.get("If-Match"), '"9007199254740995"'); assert.equal(input.headers.get("Idempotency-Key"), "same-key");
    assert.deepEqual(await input.json(), { reason: "Dampak telah ditinjau" });
    return Response.json({ proposalId: "9007199254740993", scopeId: "9007199254740997", proposalVersion: "9007199254740996", status: "approved" });
  } }));
  assert.equal((await gateway.read("9007199254740993", "9007199254740999")).id, "9007199254740993");
  const result = await gateway.decide("approve", "9007199254740993", "9007199254740995", "Dampak telah ditinjau", "same-key", "9007199254740999");
  assert.equal(result.scopeId, "9007199254740997"); assert.equal(calls.length, 2);
  assert.equal(calls[1].url, "https://unit-toko.test/api/v1/organizations/proposals/9007199254740993/approve");
});

test("uncertain organization proposal retry keeps one immutable key and reference-only recovery journal", async () => {
  const storage = new Map<string, string>();
  const journal = new OperationJournal(() => ({ get length() { return storage.size; }, key: index => [...storage.keys()][index] ?? null,
    getItem: key => storage.get(key) ?? null, setItem: (key, value) => { storage.set(key, value); }, removeItem: key => { storage.delete(key); } }));
  let sequence = 0; const ledger = new MutationLedger(() => `request-${++sequence}`);
  const keys: string[] = []; const bodies: unknown[] = [];
  const gateway = createOrganizationGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); keys.push(input.headers.get("Idempotency-Key")!); bodies.push(await input.json());
    return keys.length === 1 ? Response.json({ code: "commit_outcome_unknown", traceId: "org-trace" }, { status: 503 }) : Response.json({ id: "81", status: "pending", version: "1" });
  } }));
  const body = { change: { ...newOrganizationRequest(), parentId: "10", code: "TEBET", name: "Tebet" }, reason: "Private review reason" };
  const send = () => submitTrackedRequest(ledger, journal, "15", "proposal", "organization.propose", body, key => gateway.propose(body, key, "15"));
  await assert.rejects(send(), error => error instanceof AccessFailure && error.code === "commit_outcome_unknown");
  assert.equal(keys.length, 1, "gateway never automatically retries a command");
  assert.equal(journal.list("15").length, 1); assert.equal(JSON.stringify([...storage.values()]).includes("Private review reason"), false);
  await assert.rejects(submitTrackedRequest(ledger, journal, "15", "proposal", "organization.propose", { ...body, reason: "Changed" }, async () => "must not send"), /resolve_previous_attempt/);
  await send(); assert.deepEqual(keys, ["request-1", "request-1"]); assert.deepEqual(bodies[0], bodies[1]); assert.equal(journal.list("15").length, 0);
});

test("a stale impact or changed-account response remains a known conflict with no silent retry", async () => {
  for (const code of ["organization_impact_changed", "actor_changed"]) {
    let calls = 0;
    const gateway = createOrganizationGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async () => {
      calls++; return Response.json({ code, traceId: "review-trace" }, { status: 409 });
    } }));
    await assert.rejects(gateway.decide("approve", "81", "1", "Reviewed", "fixed-key", "15"),
      error => error instanceof AccessFailure && error.status === 409 && error.code === code && error.traceId === "review-trace");
    assert.equal(calls, 1);
  }
});
