import { test } from "node:test";
import assert from "node:assert/strict";
import { proofDimensionProblem, proofFileMessage, proofFileProblem } from "../src/modules/billing/payment-proof-file";

// The original screenshot is the evidence (no client compression); before uploading, the person learns exactly why a
// file cannot be used instead of one generic error.
test("each unusable proof file gets its own reason", () => {
  assert.equal(proofFileProblem({ name: "bukti.jpg", type: "image/jpeg", size: 250_000 }), null);
  assert.equal(proofFileProblem({ name: "bukti.png", type: "image/png", size: 1 }), null);
  assert.equal(proofFileProblem({ name: "kosong.jpg", type: "image/jpeg", size: 0 }), "empty");
  assert.equal(proofFileProblem({ name: "besar.jpg", type: "image/jpeg", size: 10 * 1024 * 1024 + 1 }), "too_large");
  assert.equal(proofFileProblem({ name: "IMG_0001.HEIC", type: "image/heic", size: 900_000 }), "heic");
  assert.equal(proofFileProblem({ name: "IMG_0002.heif", type: "", size: 900_000 }), "heic");
  assert.equal(proofFileProblem({ name: "mutasi.pdf", type: "application/pdf", size: 90_000 }), "type");
});

test("images too small to read are refused", () => {
  assert.equal(proofDimensionProblem(1080, 2340), null);
  assert.equal(proofDimensionProblem(500, 500), null);
  assert.equal(proofDimensionProblem(499, 1200), "too_small");
  assert.equal(proofDimensionProblem(1200, 320), "too_small");
});

test("reasons read as plain Bahasa Indonesia with the next step", () => {
  assert.match(proofFileMessage("heic"), /HEIC/);
  assert.match(proofFileMessage("heic"), /tangkapan layar/);
  assert.match(proofFileMessage("too_large"), /10 MB/);
  assert.match(proofFileMessage("too_small"), /500 piksel/);
  assert.match(proofFileMessage("empty"), /kosong/);
  assert.match(proofFileMessage("type"), /JPG atau PNG/);
});
