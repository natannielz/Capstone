import { test } from "node:test";
import assert from "node:assert/strict";
import { parseIdentitySession, loginFields, stepUpFields, identityMessage, IdentityFailure, sessionTiming, sessionNow, identityDestinations } from "../src/modules/identity/identity-workflow";
import { createIdentityGateway } from "../src/modules/identity/identity-client";

const session = { authenticated: true, account: { userId: "9007199254740993", displayName: "Operator Tebet", verifiedEmail: "operator@example.test" }, factorCompletedAt: "2026-09-28T02:00:00Z", freshFactorUntil: "2026-09-28T02:05:00Z", idleExpiresAt: "2026-09-28T02:30:00Z", absoluteExpiresAt: "2026-09-28T10:00:00Z", serverTime: "2026-09-28T02:01:00Z", stepUpOperations: ["access.approve"] };

test("session parsing preserves exact user identifiers and rejects malformed authenticated evidence", () => {
  assert.equal(parseIdentitySession(session).account?.userId, "9007199254740993");
  assert.deepEqual(parseIdentitySession({ ...session, stepUpOperations: ["receiving.account.verify", "receiving.account.approve"] }).stepUpOperations, ["receiving.account.verify", "receiving.account.approve"]);
  for (const invalid of [{ ...session, account: { ...session.account, userId: 9007199254740993 } }, { ...session, account: null }, { ...session, idleExpiresAt: null }, { ...session, stepUpOperations: ["//elsewhere"] }, { ...session, absoluteExpiresAt: "invalid" }]) {
    assert.throws(() => parseIdentitySession(invalid), IdentityFailure);
  }
  const anonymous = parseIdentitySession({ authenticated: false, account: null, factorCompletedAt: null, freshFactorUntil: null, idleExpiresAt: null, absoluteExpiresAt: null, serverTime: session.serverTime, stepUpOperations: [] });
  assert.equal(anonymous.authenticated, false);
});

test("native form fields restrict redirects and require paired explicit invitation credentials", () => {
  assert.deepEqual(loginFields("/identity/account", "", ""), { returnPath: "/identity/account" });
  assert.deepEqual(loginFields("/administration/access", "9007199254740993", "a".repeat(64)), { returnPath: "/administration/access", invitationId: "9007199254740993", invitationSecret: "a".repeat(64) });
  for (const returnPath of ["//example.test", "/%2fexample.test", "/identity/account?token=https://outside.test", "/identity/account?token=a%2Fb", "/auth/callback", "/legacy-login"]) assert.throws(() => loginFields(returnPath, "", ""));
  assert.throws(() => loginFields("/identity/account", "12", ""));
  assert.throws(() => loginFields("/identity/account", "", "", true), "invitation mode must never silently become ordinary login");
  assert.throws(() => loginFields("/identity/account", "9223372036854775808", "a".repeat(64)));
});

test("step-up binds the displayed actor and only offers actual server-permitted operations", () => {
  const parsed = parseIdentitySession(session);
  assert.deepEqual(stepUpFields(parsed, "access.approve", "/identity/account"), { expectedActorId: "9007199254740993", operation: "access.approve", returnPath: "/identity/account" });
  assert.throws(() => stepUpFields(parsed, "payment.verify", "/identity/account"));
  assert.throws(() => stepUpFields({ ...parsed, account: null }, "access.approve", "/identity/account"));
  assert.equal(identityDestinations(["access.approve"]).some(item => item.href === "/administration/access"), true);
  assert.deepEqual(identityDestinations(["order.submit"]).map(item => item.href), [], "buying is reached through the store door, not listed in the workspace (R2)");
  assert.deepEqual(identityDestinations(["report.read"]).map(item => item.href), ["/administration/delegations", "/reporting/accounts"]);
  assert.equal(identityDestinations(["accounting.book.read"]).some(item => item.href === "/reporting/accounts"), false);
  assert.deepEqual(loginFields("/reporting/accounts", "", ""), { returnPath: "/reporting/accounts" });
});

test("session timeout display uses server-relative elapsed time without background renewal", () => {
  const parsed = parseIdentitySession(session);
  assert.deepEqual(sessionTiming(parsed, 60_000), { expired: false, fresh: true });
  assert.deepEqual(sessionTiming(parsed, 4 * 60_000), { expired: false, fresh: false });
  assert.deepEqual(sessionTiming(parsed, 29 * 60_000), { expired: true, fresh: false });
});

test("identity reads are same-origin, uncacheable and never follow a provider redirect", async () => {
  const gateway = createIdentityGateway({ baseUrl: "https://unit-toko.test", fetch: async input => {
    assert.ok(input instanceof Request); assert.equal(input.url, "https://unit-toko.test/api/v1/identity/session");
    assert.equal(input.credentials, "same-origin"); assert.equal(input.cache, "no-store"); assert.equal(input.redirect, "error");
    assert.equal(input.headers.has("Authorization"), false); return Response.json(session);
  } });
  assert.equal((await gateway.session()).account?.userId, "9007199254740993");
});

test("identity failures distinguish unavailable service from missing approval and never expose server text", async () => {
  const gateway = createIdentityGateway({ baseUrl: "https://unit-toko.test", fetch: async () => Response.json({ code: "identity_unavailable", title: "Secret internal detail", traceId: "trace" }, { status: 503 }) });
  await assert.rejects(gateway.session(), error => error instanceof IdentityFailure && error.status === 503 && error.traceId === "trace");
  assert.match(identityMessage(new IdentityFailure(503, "identity_unavailable")), /belum tersedia/i);
  assert.match(identityMessage(new IdentityFailure(403, "membership_required")), /disetujui/i);
});

test("personal delegation discovery does not expose administrative write workflows", () => {
  assert.deepEqual(identityDestinations([]).map(item => item.href), [], "delegation is listed for staff only (G-028)");
  assert.ok(identityDestinations(["report.read"]).some(item => item.href === "/administration/delegations"));
  assert.deepEqual(loginFields("/administration/delegations", "", ""), { returnPath: "/administration/delegations" });
});

test("staff sign in straight into their back office and guests return to the public catalog", async () => {
  const { identityReturnPath } = await import("../src/modules/identity/identity-workflow");
  assert.equal(identityReturnPath("/workspace"), "/workspace");
  assert.equal(identityReturnPath("/katalog"), "/katalog");
  assert.deepEqual(loginFields("/workspace", "", ""), { returnPath: "/workspace" });
});

test("signing in from a public product returns to that product's priced page, and only a canonical product address qualifies", async () => {
  const { identityReturnPath } = await import("../src/modules/identity/identity-workflow");
  assert.equal(identityReturnPath("/shop/00000065-0000-4000-8000-000000000101"), "/shop/00000065-0000-4000-8000-000000000101");
  for (const forged of ["/shop/00000065-0000-4000-8000-000000000101/x", "/shop/../identity/account", "/shop/ABCDEF65-0000-4000-8000-000000000101", "/shop/1"])
    assert.equal(identityReturnPath(forged), "/workspace");
});

test("sessionNow follows the elapsed time exactly, so an action sees the clock of that instant, not a ticked one", () => {
  const parsed = parseIdentitySession(session), base = Date.parse(parsed.serverTime);
  assert.equal(sessionNow(parsed, 1_250), base + 1_250);
  assert.equal(sessionNow(parsed, -5), base);
});
