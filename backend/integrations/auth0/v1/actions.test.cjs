"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const request = require("./01-request-otp.cjs").onExecutePostLogin;
const attest = require("./02-assert-otp.cjs").onExecutePostLogin;

function fixture() {
  const events = [];
  const event = {
    secrets: { UNIT_TOKO_CLIENT_ID: "fixture-client", UNIT_TOKO_CONNECTION_ID: "con_fixture", UNIT_TOKO_CALLBACK_URI: "https://unit-toko.co.id/auth/callback",
      UNIT_TOKO_CLAIMS_NAMESPACE: "https://unit-toko.co.id/identity/", UNIT_TOKO_POLICY_VERSION: "unit-toko-otp-v1" },
    client: { client_id: "fixture-client" }, connection: { id: "con_fixture", strategy: "auth0" },
    user: { email_verified: true, enrolledFactors: [{ type: "otp" }] }, request: { query: { unit_toko_challenge: "A".repeat(43) } },
    transaction: { protocol: "oidc-basic-profile", response_type: ["code"], redirect_uri: "https://unit-toko.co.id/auth/callback", metadata: {} },
    authentication: { methods: [{ name: "pwd", timestamp: new Date(Date.now() - 60000).toISOString() }] },
  };
  const api = {
    access: { deny: reason => events.push({ kind: "deny", reason }) },
    transaction: { setMetadata: (key, value) => { event.transaction.metadata[key] = value; } },
    authentication: { challengeWith: factor => events.push({ kind: "challenge", factor }), enrollWith: factor => events.push({ kind: "enroll", factor }) },
    idToken: { setCustomClaim: (key, value) => events.push({ kind: "claim", key, value }) },
  };
  return { event, api, events };
}

test("normal login challenges OTP and attests only a new actual method timestamp", async () => {
  const { event, api, events } = fixture();
  await request(event, api); assert.deepEqual(events, [{ kind: "challenge", factor: { type: "otp" } }]);
  const timestamp = new Date().toISOString(); event.authentication.methods.push({ name: "mfa", type: "otp", timestamp });
  await attest(event, api);
  const proof = events.find(item => item.kind === "claim");
  assert.equal(proof.key, "https://unit-toko.co.id/identity/assurance");
  assert.equal(proof.value.factorCompletedAt, Math.floor(Date.parse(timestamp) / 1000));
  assert.equal(proof.value.challengeBinding, "A".repeat(43)); assert.equal(proof.value.method, "otp");
});
test("first enrollment must still produce completed OTP evidence in the following Action", async () => {
  const { event, api, events } = fixture(); event.user.enrolledFactors = [];
  await request(event, api); assert.deepEqual(events, [{ kind: "enroll", factor: { type: "otp" } }]);
  await attest(event, api); assert.equal(events.at(-1).kind, "deny"); assert.equal(events.some(item => item.kind === "claim"), false);
});
test("remembered session MFA, even just completed before the first Action, cannot be relabeled fresh", async () => {
  const { event, api, events } = fixture();
  event.authentication.methods.push({ name: "mfa", type: "otp", timestamp: new Date().toISOString() });
  await request(event, api); await attest(event, api);
  assert.equal(events.at(-1).kind, "deny"); assert.equal(events.some(item => item.kind === "claim"), false);
});
test("weaker fallback, future/old methods, missing first stage and changed transaction binding all deny", async () => {
  for (const variation of ["recovery-code", "email", "future", "old", "missing-stage", "binding"]) {
    const { event, api, events } = fixture(); await request(event, api);
    event.authentication.methods.push({ name: "mfa", type: ["recovery-code", "email"].includes(variation) ? variation : "otp",
      timestamp: new Date(Date.now() + (variation === "future" ? 60000 : variation === "old" ? -60000 : 0)).toISOString() });
    if (variation === "missing-stage") event.transaction.metadata = {};
    if (variation === "binding") event.request.query.unit_toko_challenge = "B".repeat(43);
    await attest(event, api); assert.equal(events.at(-1).kind, "deny", variation); assert.equal(events.some(item => item.kind === "claim"), false, variation);
  }
});
test("unverified email, wrong connection, unsupported flow and arbitrary callback never reach a challenge", async () => {
  for (const variation of ["email", "connection", "refresh", "callback", "namespace"]) {
    const { event, api, events } = fixture();
    if (variation === "email") event.user.email_verified = false;
    if (variation === "connection") event.connection.id = "con_other";
    if (variation === "refresh") event.transaction.protocol = "oauth2-refresh-token";
    if (variation === "callback") event.transaction.redirect_uri = "https://another.co.id/auth/callback";
    if (variation === "namespace") event.secrets.UNIT_TOKO_CLAIMS_NAMESPACE = "https://auth0.com/";
    await request(event, api); assert.equal(events.length, 1); assert.equal(events[0].kind, "deny", variation);
  }
});
