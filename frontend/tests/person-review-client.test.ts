import { test } from "node:test";
import assert from "node:assert/strict";
import { createUnitTokoClient } from "../src/api/client";
import { createPersonReviewGateway } from "../src/modules/identity/person-review-client";
import { personReviewCommand, type PersonReviewDraft } from "../src/modules/identity/person-review-workflow";
import { AccessFailure, MutationLedger } from "../src/modules/administration/workflow";
import { OperationJournal, reconcileOperationRecovery, submitTrackedRequest } from "../src/modules/administration/operation-journal";

const actorId = "9007199254740993", groupId = "9007199254740995", version = "9007199254740997", proposalId = "9007199254741001";
const account = { userId: "21", displayName: "Petugas Tebet", verifiedEmail: "tebet@example.test", status: "active" };
const result = { proposalId, version: "1", status: "pending", groupId, groupVersion: version, recordedAt: "2026-09-28T01:30:00Z" };
const target = { groupId, version, status: "active", accounts: [account] };
const summary = { result, requester: { ...account, userId: "41" }, reason: "Alasan semula", affectedAccountCount: "1", allowedActions: ["approve", "reject", "withdraw"] };
const preview = { reference: "12345678-1234-4123-8123-123456789abc", kind: "propose", expiresAt: "2026-09-28T02:05:00Z", target, proposal: null };
const base = "https://unit-toko.test/api/v1/identity/administration/person-reviews";
const now = Date.parse("2026-09-28T02:00:00Z");
function gateway(handler: (request: Request) => Promise<Response>) {
  return createPersonReviewGateway(createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "memory-only-csrf" });
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("X-CSRF-Token"), "memory-only-csrf");
    assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.credentials, "same-origin"); assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error");
    return handler(input);
  } }));
}

test("private queue and named search keep purpose and terms in scoped POST bodies", async () => {
  const calls: string[] = [];
  const client = gateway(async request => {
    calls.push(request.url); assert.equal(new URL(request.url).search, "");
    if (request.url.endsWith("/proposals/query")) {
      assert.deepEqual(await request.json(), { purpose: "access-review", status: "pending", offset: 25, limit: 25 });
      return Response.json({ total: "9007199254741003", items: [summary] });
    }
    assert.equal(request.url, base + "/targets/search");
    assert.deepEqual(await request.json(), { purpose: "access-review", term: "Petugas Tebet", offset: 0, limit: 25 });
    return Response.json({ total: "1", items: [target] });
  });
  assert.equal((await client.list(actorId, "pending", 25)).total, "9007199254741003");
  assert.equal((await client.targets(actorId, "  Petugas Tebet  ")).items[0].groupId, groupId);
  assert.equal(calls.length, 2);
});

test("detail lookup must return the exact selected proposal without raw source data", async () => {
  const client = gateway(async request => {
    assert.equal(request.url, base + "/proposals/" + proposalId + "/query");
    assert.deepEqual(await request.json(), { purpose: "access-review" });
    return Response.json({ summary, accounts: [account], decision: null, sourceSubject: "hidden" });
  });
  assert.equal("sourceSubject" in await client.detail(actorId, proposalId), false);
  await assert.rejects(gateway(async () => Response.json({ summary, accounts: [account], decision: null })).detail(actorId, "91"), /person_response_invalid/);
});

for (const action of ["propose", "approve"] as const) test(action + " preview binds its exact selected version", async () => {
  const draft: PersonReviewDraft = { actorId, action, groupId, groupVersion: version, reason: "Alasan lengkap", ...(action === "approve" ? { proposalId, proposalVersion: "1" } : {}) };
  const client = gateway(async request => {
    assert.equal(request.url, base + (action === "propose" ? "/targets/" + groupId : "/proposals/" + proposalId) + "/preview");
    assert.equal(request.headers.get("If-Match"), action === "propose" ? '"' + version + '"' : '"1"');
    assert.deepEqual(await request.json(), { purpose: "access-review" });
    assert.equal(request.headers.has("Idempotency-Key"), false);
    return Response.json({ ...preview, kind: action, proposal: action === "approve" ? summary : null });
  });
  assert.equal((await client.preview(draft)).reference, preview.reference);
});

for (const [action, status] of Object.entries({ propose: "pending", approve: "approved", reject: "rejected", withdraw: "withdrawn" }) as ["propose" | "approve" | "reject" | "withdraw", string][]) {
  test(action + " sends only the reviewed body, actor, key and expected proposal revision", async () => {
    const draft: PersonReviewDraft = { actorId, action, groupId, groupVersion: version, reason: "Alasan keputusan tetap", ...(action !== "propose" ? { proposalId, proposalVersion: "1" } : {}) };
    const command = personReviewCommand(draft, action === "propose" ? preview : action === "approve" ? { ...preview, kind: "approve", proposal: summary } : null, now);
    const client = gateway(async request => {
      assert.equal(request.url, base + "/proposals" + (action === "propose" ? "" : "/" + proposalId + "/" + action));
      assert.equal(request.headers.get("Idempotency-Key"), "exact-retry-key");
      assert.equal(request.headers.get("If-Match"), action === "propose" ? null : '"1"');
      assert.deepEqual(await request.json(), action === "propose" || action === "approve" ? { previewReference: preview.reference, reason: draft.reason } : { reason: draft.reason });
      return Response.json({ ...result, status, version: action === "propose" ? "1" : "2" });
    });
    assert.equal((await client.submit(command, "exact-retry-key")).status, status);
  });
}

test("lost or malformed write outcomes never trigger gateway retry or appear confirmed", async () => {
  const command = personReviewCommand({ actorId, action: "propose", groupId, groupVersion: version, reason: "Alasan" }, preview, now);
  let requests = 0;
  await assert.rejects(gateway(async () => { requests++; return Response.json({ code: "commit_outcome_unknown" }, { status: 503 }); }).submit(command, "once"),
    error => error instanceof AccessFailure && error.code === "commit_outcome_unknown");
  assert.equal(requests, 1);
  for (const invalid of [{ ...result, groupId: "90" }, { ...result, status: "approved" }, { ...result, version: 1 }])
    await assert.rejects(gateway(async () => Response.json(invalid)).submit(command, "once"), /person_response_invalid/);
});

test("person receipt recovery uses only the private namespace route and null retains its journal", async () => {
  const values = new Map<string, string>();
  const journal = new OperationJournal(() => ({ get length() { return values.size; }, key(index) { return [...values.keys()][index] ?? null; }, getItem(key) { return values.get(key) ?? null; }, setItem(key, value) { values.set(key, value); }, removeItem(key) { values.delete(key); } }));
  const entry = journal.remember(actorId, "person.review.propose", "uncertain-key");
  let confirmed = false;
  const client = gateway(async request => {
    assert.equal(request.url, base + "/operations/query");
    assert.deepEqual(await request.json(), { purpose: "operation-recovery", operation: entry.operation, key: entry.key });
    return Response.json({ state: confirmed ? "committed" : "not_confirmed", recordedAt: confirmed ? result.recordedAt : null, result: confirmed ? result : null });
  });
  const legacy = { async lookup() { assert.fail("access fallback forbidden"); }, async close() { assert.fail("close unsupported"); } };
  const adapters = { "person.review": { lookup: client.receipt } };
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), { state: "unconfirmed" });
  assert.equal(journal.list(actorId).length, 1);
  confirmed = true;
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), { state: "committed", result: { id: proposalId, version: "1" } });
  assert.deepEqual(journal.list(actorId), []);
});

test("read cancellation reaches the private business request", async () => {
  const controller = new AbortController();
  let observed: AbortSignal | undefined;
  const client = gateway(async request => {
    observed = request.signal; controller.abort(); assert.equal(request.signal.aborted, true);
    throw new DOMException("Cancelled", "AbortError");
  });
  await assert.rejects(client.list(actorId, "pending", 0, controller.signal));
  assert.ok(observed?.aborted);
});

for (const [operation, status] of Object.entries({ "person.review.propose": "pending", "person.review.approve": "approved", "person.review.reject": "rejected", "person.review.withdraw": "withdrawn" })) {
  test(operation + " permanent resolution sends original key with only operation and reason", async () => {
    const entry = { actorId, operation, key: "original-reloaded-key", createdAt: result.recordedAt };
    let calls = 0;
    const client = gateway(async request => {
      calls++; assert.equal(request.url, base + "/operations/resolve"); assert.equal(request.headers.get("Idempotency-Key"), entry.key);
      assert.equal(request.headers.has("If-Match"), false);
      assert.deepEqual(await request.json(), { operation, reason: "Selesaikan referensi lama" });
      return Response.json({ state: "committed", result: { ...result, status }, recordedAt: result.recordedAt });
    });
    assert.deepEqual(await client.resolve(entry, "  Selesaikan referensi lama  "), { state: "committed", result: { id: proposalId, version: "1" }, recordedAt: result.recordedAt });
    assert.equal(calls, 1);
  });
}

test("closed lookup and resolve return confirmed permanent closure without inventing a business result", async () => {
  const entry = { actorId, operation: "person.review.propose", key: "original-reloaded-key", createdAt: result.recordedAt };
  const response = { state: "closed_no_effect", result: null, recordedAt: result.recordedAt };
  const client = gateway(async () => Response.json(response));
  assert.deepEqual(await client.receipt(entry), { state: "closed", result: null, recordedAt: result.recordedAt });
  assert.deepEqual(await client.resolve(entry, "Reason"), { state: "closed", result: null, recordedAt: result.recordedAt });
});

test("resolution denial, unavailable, malformed and unknown results never clear the original reference", async () => {
  for (const response of [() => Response.json({ code: "permission_denied" }, { status: 403 }), () => Response.json({ code: "person_governance_unavailable" }, { status: 503 }),
    () => Response.json({ state: "closed_no_effect", result, recordedAt: result.recordedAt }), () => Response.json({ state: "not_confirmed", result: null, recordedAt: null }),
    () => { throw new TypeError("network lost"); }]) {
    const values = new Map<string, string>();
    const journal = new OperationJournal(() => ({ get length() { return values.size; }, key(index) { return [...values.keys()][index] ?? null; }, getItem(key) { return values.get(key) ?? null; }, setItem(key, value) { values.set(key, value); }, removeItem(key) { values.delete(key); } }));
    const entry = journal.remember(actorId, "person.review.propose", "original-key"); let attempts = 0;
    const client = gateway(async () => { attempts++; return response(); });
    const legacy = { async lookup() { assert.fail("person lookup must be private"); }, async close() { assert.fail("person close must be private"); } };
    try { const outcome = await reconcileOperationRecovery(entry, journal, legacy, { "person.review": { lookup: client.receipt, close: { allowed: () => true, resolve: client.resolve } } }, "Close request"); assert.deepEqual(outcome, { state: "unconfirmed" }); } catch (error) { assert.ok(error instanceof Error); }
    assert.equal(attempts, 1); assert.deepEqual(journal.list(actorId), [entry]);
  }
});


test("a late original submission retains the journal and exact retry when its owning actor view was replaced", async () => {
  const values=new Map<string,string>();const journal=new OperationJournal(()=>({get length(){return values.size;},key(i){return [...values.keys()][i]??null;},getItem(k){return values.get(k)??null;},setItem(k,v){values.set(k,v);},removeItem(k){values.delete(k);}}));
  const ledger=new MutationLedger(()=>"original-actor-key"),command=personReviewCommand({actorId,action:"propose",groupId,groupVersion:version,reason:"Original reason"},preview,now);
  let current=true;const client=gateway(async()=>{current=false;return Response.json(result);});
  await assert.rejects(submitTrackedRequest(ledger,journal,actorId,"person-review","person.review.propose",command,key=>client.submit(command,key,()=>current)),/commit_outcome_unknown/);
  assert.equal(ledger.isUncertain("person-review"),true);assert.equal(journal.list(actorId)[0].key,"original-actor-key");assert.equal(values.size,1);
});
