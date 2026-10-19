import { test } from "node:test";
import assert from "node:assert/strict";
import { sceneMode } from "../src/components/landing/scene-mode";
import { CARTON, LEAVE_MS, MAX_CARTONS, OPENING_MS, cartonSlots, entryOffset, leaveOffset, openingFrame, restingFrame } from "../src/components/landing/kemasan-timeline";

// Phase G task 5: the landing scene is decorative. It animates only when WebGL exists and nothing asks for less motion
// or data; otherwise it shows the final frame, and without WebGL the page keeps its static poster.
test("the scene gate picks animated, still or none", () => {
  assert.equal(sceneMode({ webgl: false, reducedMotion: false, saveData: false }), "none");
  assert.equal(sceneMode({ webgl: false, reducedMotion: true, saveData: true }), "none");
  assert.equal(sceneMode({ webgl: true, reducedMotion: true, saveData: false }), "still");
  assert.equal(sceneMode({ webgl: true, reducedMotion: false, saveData: true }), "still");
  assert.equal(sceneMode({ webgl: true, reducedMotion: false, saveData: false }), "animated");
});

test("the opening starts sealed and ends exactly on the still frame", () => {
  const start = openingFrame(0), end = openingFrame(OPENING_MS), rest = restingFrame();
  assert.equal(start.longFlap, 0); assert.equal(start.shortFlap, 0); assert.equal(start.tape, 0);
  assert.ok(start.items.every(offset => offset < 0), "bottles start hidden below the rim");
  assert.deepEqual(end, rest, "reduced motion shows the frame the animation ends on");
  assert.deepEqual(openingFrame(OPENING_MS * 3), rest, "time past the end stays on the final frame");
  assert.equal(rest.items.length, CARTON.perCarton);
  assert.ok(rest.items.every(offset => offset === 0));
});

test("flaps only open and bottles rise in a wave from the back row", () => {
  let previous = openingFrame(0);
  for (let ms = 50; ms <= OPENING_MS; ms += 50) {
    const frame = openingFrame(ms);
    assert.ok(frame.longFlap >= previous.longFlap - 1e-9 && frame.shortFlap >= previous.shortFlap - 1e-9, `flaps never close at ${ms} ms`);
    previous = frame;
  }
  const middle = openingFrame(OPENING_MS * 0.62);
  assert.ok(middle.items[0] > middle.items[CARTON.perCarton - 1], "the first bottle leads the last one");
  assert.ok(openingFrame(OPENING_MS * 0.3).longFlap > openingFrame(OPENING_MS * 0.3).shortFlap, "long flaps open before short flaps");
});

test("cartons take fixed pallet slots, front-left first, clamped to the demo range", () => {
  assert.equal(cartonSlots(0).length, 1);
  assert.equal(cartonSlots(9).length, MAX_CARTONS);
  const four = cartonSlots(4);
  assert.equal(new Set(four.map(slot => slot.x + ":" + slot.z)).size, 4, "no two cartons share a slot");
  assert.ok(four[0].x < 0 && four[0].z > 0, "the open carton stands front-left, nearest the camera");
  assert.deepEqual(cartonSlots(2), four.slice(0, 2), "adding a carton never moves the others");
  for (const slot of four) assert.ok(Math.abs(slot.x) + CARTON.width / 2 <= 1.8 && Math.abs(slot.z) + CARTON.depth / 2 <= 1.3, "inside the pallet");
});

test("a carton drops onto the pallet and settles, and a removed one lifts off", () => {
  assert.ok(entryOffset(0) > 1, "starts above the pallet");
  assert.equal(entryOffset(10_000), 0);
  assert.ok(entryOffset(200) < entryOffset(0));
  assert.equal(leaveOffset(0), 0);
  assert.ok(leaveOffset(LEAVE_MS) > 2, "gone from view when the lift ends");
});
