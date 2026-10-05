import {test} from "node:test";
import assert from "node:assert/strict";
import {createUnitTokoClient} from "./helpers/api-client";
import {commandOperations, createAccessGateway, identitySuspensionReason, identitySuspensionTarget, parseAccessSubject} from "../src/modules/administration/access-client";
import {OperationJournal, reconcileOperationRecovery, submitTrackedRequest} from "../src/modules/administration/operation-journal";
import {operationLabel, recoveryPage} from "../src/modules/administration/operation-recovery-navigation";
import {AccessFailure, MutationLedger} from "../src/modules/administration/workflow";

const actorId = "9007199254740993", subjectId = "9007199254740994", version = "9007199254740995";
const subject = {userId: subjectId, status: "active", grantVersion: version, memberships: [], grants: [], allowedActions: ["suspend"]};

test("account suspension requires explicit subject authority and freezes the exact account grant version", () => {
  const target = identitySuspensionTarget(parseAccessSubject(subject), actorId);
  assert.deepEqual(target, {action: "suspendIdentity", id: subjectId, version});
  assert.equal(Object.isFrozen(target), true);
  assert.equal(identitySuspensionTarget(parseAccessSubject({...subject, allowedActions: []}), actorId), null);
  assert.equal(identitySuspensionTarget(parseAccessSubject(subject), subjectId), null);
  for (const status of ["pending", "suspended", "revoked"])
    assert.equal(identitySuspensionTarget(parseAccessSubject({...subject, status, allowedActions: []}), actorId), null);
});

test("missing, nullable, duplicate, unknown or inactive account actions fail closed", () => {
  const withoutActions = Object.fromEntries(Object.entries(subject).filter(([key]) => key !== "allowedActions"));
  const bad = [withoutActions, {...subject, allowedActions: null}, {...subject, allowedActions: ["revoke"]},
    {...subject, allowedActions: ["suspend", "suspend"]}, {...subject, status: "suspended"},
    {...subject, userId: Number(subjectId)}, {...subject, grantVersion: "01"}];
  for (const input of bad) assert.throws(() => parseAccessSubject(input), error => error instanceof AccessFailure && error.code === "access_subject_invalid");
});

test("suspension reasons are readable, bounded and normalized before freezing the request", () => {
  assert.equal(identitySuspensionReason("  Perubahan tugas cafe\u0301\nAkses dihentikan.  "), "Perubahan tugas café\nAkses dihentikan.");
  for (const value of [" ", "x".repeat(1001), "Alasan\u0000", "Alasan\u202e", "Alasan\ud800"])
    assert.throws(() => identitySuspensionReason(value), error => error instanceof AccessFailure && error.code === "identity_suspension_reason_invalid");
});

test("subject projection is read with the original actor and its lossless account identifiers", async () => {
  const gateway = createAccessGateway(createUnitTokoClient({baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); assert.equal(input.method, "GET");
    assert.equal(new URL(input.url).pathname, `/api/v1/access/subjects/${subjectId}`);
    assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    return Response.json(subject);
  }}));
  const actual = await gateway.subject(subjectId, actorId);
  assert.equal(actual.grantVersion, version); assert.deepEqual(actual.allowedActions, ["suspend"]);
});

test("account suspension sends one actor-bound CSRF protected command with exact quoted grant version", async () => {
  let calls = 0;
  const gateway = createAccessGateway(createUnitTokoClient({baseUrl: "https://unit-toko.test", fetch: async input => {
    calls++; assert.ok(input instanceof Request);
    assert.equal(new URL(input.url).pathname, `/api/v1/access/subjects/${subjectId}/suspend`);
    assert.equal(input.method, "POST"); assert.equal(input.headers.get("If-Match"), `"${version}"`);
    assert.equal(input.headers.get("Idempotency-Key"), "suspend-once"); assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    assert.equal(input.headers.get("X-CSRF-Token"), "fixture-request-token");
    assert.equal(input.credentials, "same-origin"); assert.equal(input.cache, "no-store"); assert.equal(input.headers.has("Authorization"), false);
    assert.deepEqual(await input.json(), {reason: "Penugasan akun berakhir"});
    return Response.json({id: subjectId, subjectId, version: "9007199254740996", grantVersion: "9007199254740996", status: "suspended"});
  }}));
  await gateway.change("suspendIdentity", subjectId, version, "Penugasan akun berakhir", "suspend-once", actorId);
  assert.equal(calls, 1); assert.equal(commandOperations.suspendIdentity, "access.identity.suspend");
});

class MemoryStorage {
  values = new Map<string, string>();
  get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}

test("lost suspension replies retain only actor action key time and recover by lookup without retransmission", async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => "suspend-original-key");
  const paths: string[] = [];
  const gateway = createAccessGateway(createUnitTokoClient({baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); const path = new URL(input.url).pathname; paths.push(path);
    assert.equal(input.headers.get("X-Expected-Actor-Id"), actorId);
    if (path.endsWith("/suspend")) throw new TypeError("Lost committed response");
    assert.equal(path, "/api/v1/access/operations/lookup"); assert.equal(input.headers.get("If-Match"), null);
    assert.equal(input.headers.get("Idempotency-Key"), null);
    assert.deepEqual(await input.json(), {operation: "access.identity.suspend", key: "suspend-original-key", purpose: "operation-recovery"});
    return Response.json({operation: "access.identity.suspend", state: "committed", recordedAt: "2026-10-05T00:00:00Z",
      result: {id: subjectId, subjectId, version: "9007199254740996", grantVersion: "9007199254740996", status: "suspended", kind: null}});
  }}));
  const payload = {id: subjectId, version, reason: "Private staffing reason"};
  await assert.rejects(submitTrackedRequest(ledger, journal, actorId, `suspendIdentity:${subjectId}`, commandOperations.suspendIdentity, payload,
    key => gateway.change("suspendIdentity", subjectId, version, payload.reason, key, actorId)));
  const reloaded = new OperationJournal(() => storage), entry = reloaded.list(actorId)[0];
  assert.deepEqual(Object.keys(entry).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(reloaded.list(subjectId), []);
  assert.equal([...storage.values.values()].some(value => value.includes("Private staffing reason") || value.includes(subjectId) || value.includes(version)), false);
  const outcome = await reconcileOperationRecovery(entry, reloaded, {
    lookup: reference => gateway.receipt(reference.operation, reference.key, reference.actorId),
    close: () => {throw new Error("Recovery must not close or resend this test command");},
  });
  assert.equal(outcome.state, "committed"); assert.deepEqual(reloaded.list(actorId), []);
  assert.deepEqual(paths, [`/api/v1/access/subjects/${subjectId}/suspend`, "/api/v1/access/operations/lookup"]);
  assert.deepEqual(recoveryPage(entry.operation), {href: "/administration/access", label: "halaman pengelolaan akses"});
  assert.equal(operationLabel(entry.operation), "Penonaktifan akun");
});
