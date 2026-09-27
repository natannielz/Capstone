import { test } from "node:test";
import assert from "node:assert/strict";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";
import { AccessFailure, MutationLedger } from "../src/modules/administration/workflow";
import { identityDestinations } from "../src/modules/identity/identity-workflow";

class Storage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}

test("identity requests retain only actor-owned references after an uncertain write", async () => {
  const storage = new Storage();
  const journal = new OperationJournal(() => storage);
  const ledger = new MutationLedger(() => "invite-once");
  let sent = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, "9007199254740993", "invite", "identity.invitation.propose",
    { email: "private@example.test", reason: "private staffing context" }, async () => {
      sent++;
      throw new AccessFailure(503, "commit_outcome_unknown");
    }), error => error instanceof AccessFailure && error.code === "commit_outcome_unknown");
  assert.equal(sent, 1);
  assert.equal(journal.list("9007199254740993")[0].operation, "identity.invitation.propose");
  assert.deepEqual(journal.list("7"), []);
  assert.doesNotMatch([...storage.values.values()].join(""), /private|staffing|email|reason/);
});

test("all supported identity decisions recover without accepting arbitrary provider actions", () => {
  const journal = new OperationJournal(() => new Storage());
  for (const operation of ["identity.invitation.propose", "identity.invitation.approve", "identity.invitation.rejected",
    "identity.invitation.withdrawn", "identity.invitation.revoked", "identity.recovery.request"])
    assert.equal(journal.remember("1", operation, "retry").operation, operation);
  for (const operation of ["identity.password.change", "identity.invitation.delete", "identity.recovery.completed"])
    assert.throws(() => journal.remember("1", operation, "retry"), error => error instanceof AccessFailure);
});

test("account administration navigation follows identity management or review permissions", () => {
  for (const permission of ["invite.manage", "recovery.initiate", "membership.approve", "access.approve", "access.audit.read"])
    assert.ok(identityDestinations([permission]).some(item => item.href === "/administration/accounts"), permission);
  assert.equal(identityDestinations(["order.create"]).some(item => item.href === "/administration/accounts"), false);
});
import { createUnitTokoClient } from "../src/api/client";
import { createIdentityAdministrationGateway } from "../src/modules/identity/identity-administration-client";

function transport(handler: (request: Request) => Promise<Response>) {
  return createUnitTokoClient({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request);
    if (input.url.endsWith("/identity/csrf")) return Response.json({ requestToken: "memory-only-token" });
    assert.equal(input.headers.get("X-CSRF-Token"), "memory-only-token");
    assert.equal(input.credentials, "same-origin");
    assert.equal(input.cache, "no-store");
    return handler(input);
  } });
}

test("account search keeps private query text in scoped POST and retains string counts", async () => {
  const gateway = createIdentityAdministrationGateway(transport(async request => {
    assert.equal(request.url, "https://unit-toko.test/api/v1/identity/administration/directory/search");
    assert.equal(request.headers.get("X-Expected-Actor-Id"), "9007199254740993");
    assert.deepEqual(await request.json(), { purpose: "account-directory", term: "staff@example.test", offset: 25, limit: 25 });
    return Response.json({ total: "9007199254740995", items: [] });
  }));
  assert.equal((await gateway.profiles("9007199254740993", "staff@example.test", 25)).total, "9007199254740995");
});

test("invitation decisions bind reviewed version, actor and same request key", async () => {
  const gateway = createIdentityAdministrationGateway(transport(async request => {
    assert.match(request.url, /invitations\/9007199254740993\/approve$/);
    assert.equal(request.headers.get("If-Match"), '"9007199254740995"');
    assert.equal(request.headers.get("Idempotency-Key"), "same-review");
    assert.equal(request.headers.get("X-Expected-Actor-Id"), "52");
    assert.deepEqual(await request.json(), { reason: "Lingkup dan peran sudah diperiksa" });
    return Response.json({ id: "9007199254740993", userId: "9007199254740997", version: "9007199254740996", status: "approved" });
  }));
  const result = await gateway.decide("approve", "9007199254740993", "9007199254740995", "Lingkup dan peran sudah diperiksa", "same-review", "52");
  assert.equal(result.status, "approved");
});

test("recovery records blocked provider intent and the exact reviewed security revision", async () => {
  const gateway = createIdentityAdministrationGateway(transport(async request => {
    assert.match(request.url, /administration\/recovery$/);
    assert.equal(request.headers.get("If-Match"), '"9007199254740995"');
    assert.deepEqual(await request.json(), { userId: "42", kind: "factor", reason: "Pemilik meminta pemulihan" });
    return Response.json({ id: "91", userId: "42", securityVersion: "9007199254740996", status: "blocked" });
  }));
  assert.equal((await gateway.recover("42", "9007199254740995", "factor", "Pemilik meminta pemulihan", "recover-once", "52")).status, "blocked");
});

test("identity admin retains uncertain outcome without automatic retry", async () => {
  let requests = 0;
  const gateway = createIdentityAdministrationGateway(transport(async () => {
    requests++;
    return Response.json({ code: "commit_outcome_unknown", traceId: "unknown-case" }, { status: 503 });
  }));
  await assert.rejects(gateway.decide("revoke", "11", "2", "Dibatalkan", "once", "52"),
    error => error instanceof AccessFailure && error.code === "commit_outcome_unknown" && error.traceId === "unknown-case");
  assert.equal(requests, 1);
});