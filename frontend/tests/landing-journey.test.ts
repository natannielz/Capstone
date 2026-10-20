import { test } from "node:test";
import assert from "node:assert/strict";
import { ACTS_FULL, ACTS_PILOT, BRANCH_DOCK_X, CAMERA_KEYS, activeAct, journeyFrame, progressFromAnchors } from "../src/components/landing/journey";

// Task 5b: the landing story is a pure function of scroll progress (0 = hero, then one unit per act), so every act
// lands on an exact, testable frame and reduced motion can jump straight to it.
const distance = (a: readonly number[], b: readonly number[]) => Math.hypot(...a.map((value, index) => value - b[index]));

test("the hero frame is the dock with the open carton and no van", () => {
  const frame = journeyFrame(0, ACTS_FULL);
  assert.deepEqual(frame.camera, CAMERA_KEYS[0]);
  assert.equal(frame.flapClose, 0); assert.equal(frame.label, 0); assert.equal(frame.van.visible, false); assert.equal(frame.check, 0);
});

test("each act ends on its exact keyframe with its step complete", () => {
  const submitted = journeyFrame(1, ACTS_FULL);
  assert.deepEqual(submitted.camera, CAMERA_KEYS[1]);
  assert.equal(submitted.flapClose, 1); assert.equal(submitted.tape, 1); assert.equal(submitted.label, 1);
  const reviewed = journeyFrame(2, ACTS_FULL);
  assert.deepEqual(reviewed.camera, CAMERA_KEYS[2]);
  assert.equal(reviewed.stamp.imprint, 1); assert.equal(reviewed.stamp.shown, false, "the stamp has lifted away when the act settles");
  const received = journeyFrame(ACTS_FULL, ACTS_FULL);
  assert.deepEqual(received.camera, CAMERA_KEYS[4]);
  assert.equal(received.van.visible, false, "the van has driven on after unloading"); assert.equal(received.check, 1); assert.equal(received.invoice, 1);
  assert.equal(journeyFrame(3.1, ACTS_FULL).van.x, BRANCH_DOCK_X, "the van waits while the carton is unloaded");
});

test("the stamp presses the label in the middle of the review act", () => {
  const pressed = journeyFrame(1.42, ACTS_FULL);
  assert.equal(pressed.stamp.shown, true); assert.equal(pressed.stamp.height, 0); assert.equal(pressed.stamp.imprint, 1);
  assert.equal(journeyFrame(1.1, ACTS_FULL).stamp.imprint, 0, "no imprint before the stamp lands");
});

test("the pilot story stops at the review and never shows the van", () => {
  assert.deepEqual(journeyFrame(3.5, ACTS_PILOT), journeyFrame(ACTS_PILOT, ACTS_PILOT));
  for (let progress = 0; progress <= 4; progress += 0.05) assert.equal(journeyFrame(progress, ACTS_PILOT).van.visible, false);
});

test("the story is continuous: no jump in camera or carton between nearby scroll positions", () => {
  let previous = journeyFrame(0, ACTS_FULL);
  for (let progress = 0.005; progress <= ACTS_FULL; progress += 0.005) {
    const frame = journeyFrame(progress, ACTS_FULL);
    assert.ok(distance(frame.camera.position, previous.camera.position) < 0.6, `camera jumps at ${progress.toFixed(3)}`);
    assert.ok(distance(frame.carton, previous.carton) < 0.5, `carton jumps at ${progress.toFixed(3)}`);
    previous = frame;
  }
});

test("on the road the carton rides inside the van", () => {
  const offsets = [2.62, 2.75, 2.9].map(progress => { const frame = journeyFrame(progress, ACTS_FULL); assert.equal(frame.van.visible, true); return frame.carton[0] - frame.van.x; });
  assert.ok(offsets.every(offset => Math.abs(offset - offsets[0]) < 1e-9));
  assert.ok(journeyFrame(2.9, ACTS_FULL).van.x > journeyFrame(2.7, ACTS_FULL).van.x, "the van drives towards the branch");
});

// Anchors are viewport y positions: the bottom edge of the hero copy, then each act card centre. An act is reached when
// its anchor rises to the focus line, so the scene lands on act N exactly while card N is being read.
test("anchor positions map to story progress and the nearest act", () => {
  assert.equal(progressFromAnchors([900, 1500, 2300], 540), 0, "nothing has crossed the focus line yet");
  assert.ok(Math.abs(progressFromAnchors([500, 1100, 1900], 540) - 40 / 600) < 1e-12, "between the hero and act 1");
  assert.equal(progressFromAnchors([-100, 540, 1300], 540), 1, "act 1 card on the focus line");
  assert.equal(progressFromAnchors([-1000, -500, 100], 540), 2, "past the last card");
  assert.equal(activeAct(1.4), 1); assert.equal(activeAct(1.6), 2); assert.equal(activeAct(-1), 0);
});
