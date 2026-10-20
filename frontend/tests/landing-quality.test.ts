import { test } from "node:test";
import assert from "node:assert/strict";
import { focusForShot, qualityTier, shouldDowngrade } from "../src/components/landing/scene/quality";

// The cinematic pipeline (ambient occlusion, depth of field, bloom, glass bottles) runs only where it can hold a
// smooth frame rate; everywhere else the balanced renderer keeps the same story without the costly passes.
test("desktop machines with enough cores get the cinematic tier; phones and small machines the balanced one", () => {
  assert.equal(qualityTier({ phone: false, cores: 8, override: null }), "cinematic");
  assert.equal(qualityTier({ phone: true, cores: 8, override: null }), "balanced");
  assert.equal(qualityTier({ phone: false, cores: 4, override: null }), "balanced");
  assert.equal(qualityTier({ phone: true, cores: 2, override: "cinematic" }), "cinematic", "an explicit override wins");
  assert.equal(qualityTier({ phone: false, cores: 16, override: "balanced" }), "balanced");
});

test("the cinematic tier steps down when the measured frames are slow, never on a handful of samples", () => {
  assert.equal(shouldDowngrade(Array.from({ length: 10 }, () => 80)), false, "too few frames to judge");
  assert.equal(shouldDowngrade(Array.from({ length: 60 }, () => 14)), false);
  assert.equal(shouldDowngrade(Array.from({ length: 60 }, (_, index) => index % 4 === 0 ? 60 : 30)), true, "median above budget");
  assert.equal(shouldDowngrade(Array.from({ length: 60 }, (_, index) => index % 10 === 0 ? 120 : 15)), false, "a few spikes do not count");
});

test("depth of field is shallow in the label close-ups and deeper on the road", () => {
  const close = focusForShot(1.5), road = focusForShot(2.8), hero = focusForShot(0);
  assert.ok(close.aperture > road.aperture && close.aperture > hero.aperture);
  assert.ok(road.aperture > 0, "a little miniature blur stays on the road");
  assert.deepEqual(focusForShot(1), focusForShot(1), "deterministic");
});
