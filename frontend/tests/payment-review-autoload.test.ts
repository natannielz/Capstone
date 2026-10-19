import { test } from "node:test";
import assert from "node:assert/strict";
import { AccessFailure } from "../src/modules/administration/workflow";
import { autoLoadEndsSession } from "../src/modules/billing/payment-review-session";

// An automatic OCR read on open must not eject the reviewer for a single unreadable piece of evidence (403):
// only a lost session (401) or a changed account ends the desk; anything else leaves "Muat hasil OCR" to retry.
test("only a lost session or changed account ends the review desk after an automatic load", () => {
  assert.equal(autoLoadEndsSession(new AccessFailure(401, "unauthenticated")), true);
  assert.equal(autoLoadEndsSession(new AccessFailure(409, "actor_changed")), true);
  assert.equal(autoLoadEndsSession(new AccessFailure(403, "access_denied")), false);
  assert.equal(autoLoadEndsSession(new AccessFailure(503, "request_failed")), false);
  assert.equal(autoLoadEndsSession(new Error("offline")), false);
});
