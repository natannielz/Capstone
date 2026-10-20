import { test } from "node:test";
import assert from "node:assert/strict";
import { sceneMode } from "../src/components/landing/scene-mode";
import { BOX, COFFEE_MAXIMUM, COFFEE_MINIMUM, storyItems } from "../src/components/landing/journey";

// The landing scene is decorative. It animates only when WebGL exists and nothing asks for less motion or data;
// otherwise it shows the act being read as a still, and without WebGL the page keeps its static poster.
test("the scene gate picks animated, still or none", () => {
  assert.equal(sceneMode({ webgl: false, reducedMotion: false, saveData: false }), "none");
  assert.equal(sceneMode({ webgl: false, reducedMotion: true, saveData: true }), "none");
  assert.equal(sceneMode({ webgl: true, reducedMotion: true, saveData: false }), "still");
  assert.equal(sceneMode({ webgl: true, reducedMotion: false, saveData: true }), "still");
  assert.equal(sceneMode({ webgl: true, reducedMotion: false, saveData: false }), "animated");
});

type Bounds = { min: number[]; max: number[] };
const bounds = (slot: readonly number[], size: readonly number[]): Bounds =>
  ({ min: slot.map((value, axis) => value - size[axis] / 2), max: slot.map((value, axis) => value + size[axis] / 2) });

// The packed box must be physically plausible: every package inside the walls, under the closed flaps, on the floor
// or on another package, and no two packages occupying the same space.
test("every package fits inside the box, rests on something and overlaps nothing", () => {
  for (let coffee = COFFEE_MINIMUM; coffee <= COFFEE_MAXIMUM; coffee++) {
    const items = storyItems(coffee).map(spec => ({ kind: spec.kind, box: bounds(spec.slot, spec.size) }));
    for (const { kind, box } of items) {
      assert.ok(box.min[0] >= -BOX.width / 2 && box.max[0] <= BOX.width / 2, `${kind} inside the end walls`);
      assert.ok(box.min[2] >= -BOX.depth / 2 && box.max[2] <= BOX.depth / 2, `${kind} inside the side walls`);
      assert.ok(box.max[1] <= BOX.height, `${kind} below the closed flaps`);
      const resting = Math.abs(box.min[1] - 0.03) < 1e-9 || items.some(other => other.box !== box && Math.abs(other.box.max[1] - box.min[1]) < 1e-9);
      assert.ok(resting, `${kind} rests on the floor or a package`);
    }
    for (let i = 0; i < items.length; i++) for (let j = i + 1; j < items.length; j++) {
      const a = items[i].box, b = items[j].box;
      const overlap = [0, 1, 2].every(axis => a.min[axis] < b.max[axis] - 1e-9 && b.min[axis] < a.max[axis] - 1e-9);
      assert.equal(overlap, false, `${items[i].kind} and ${items[j].kind} overlap with ${coffee} dus of coffee`);
    }
  }
});
