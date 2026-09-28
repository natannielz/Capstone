import { test } from "node:test";
import assert from "node:assert/strict";
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from "../src/modules/identity/identity-workflow";
import { permissionLabel } from "../src/modules/administration/permission-labels";

const path = "/administration/person-reviews";

test("person review navigation requires an explicit person-review permission", () => {
  for (const action of ["person.review.read", "person.review.propose", "person.review.approve"])
    assert.ok(identityDestinations([action]).some(item => item.href === path), action);
  for (const action of ["access.approve", "access.audit.read", "technical.configure", "invite.manage", "person.review.unknown"])
    assert.equal(identityDestinations([action]).some(item => item.href === path), false, action);
});

test("person review sign-in and step-up return only to the exact permitted page", () => {
  assert.equal(identityReturnPath(path), path);
  assert.deepEqual(loginFields(path, "", ""), { returnPath: path });
  const session = parseIdentitySession({ authenticated: true, account: { userId: "9007199254740993", displayName: "Petugas", verifiedEmail: "petugas@example.test" },
    factorCompletedAt: "2026-09-28T01:00:00Z", freshFactorUntil: "2026-09-28T01:05:00Z", idleExpiresAt: "2026-09-28T01:30:00Z", absoluteExpiresAt: "2026-09-28T10:00:00Z",
    serverTime: "2026-09-28T01:01:00Z", stepUpOperations: ["person.review.approve"] });
  assert.deepEqual(stepUpFields(session, "person.review.approve", path), { expectedActorId: "9007199254740993", operation: "person.review.approve", returnPath: path });
  assert.throws(() => stepUpFields(session, "person.review.propose", path));
  for (const unsafe of [path + "?id=123", path + "/other", "//example.test" + path]) {
    assert.equal(identityReturnPath(unsafe), "/identity/account");
    assert.throws(() => loginFields(unsafe, "", ""));
  }
});

test("person review permissions have understandable Indonesian labels", () => {
  assert.equal(permissionLabel("person.review.read"), "Lihat peninjauan keterkaitan akun");
  assert.equal(permissionLabel("person.review.propose"), "Ajukan penangguhan keterkaitan akun");
  assert.equal(permissionLabel("person.review.approve"), "Putuskan penangguhan keterkaitan akun");
});
