import { test } from "node:test";
import assert from "node:assert/strict";
import {
  ACTS_FULL, ACTS_PILOT, BOX, CAMERA_KEYS, COFFEE_MAXIMUM, COFFEE_MINIMUM, FILM_SECONDS, ON_COUNTER, TROLLEY_PICKUP, activeAct, dropWindow, filmProgress,
  onTrolleyPose, progressFromAnchors, storyFrame, storyItems,
} from "../src/components/landing/journey";

// "Satu kardus": the landing story is a pure function of scroll progress (0 = hero, then one unit per act), so every
// act lands on an exact, testable frame and reduced motion can jump straight to it.
const distance = (a: readonly number[], b: readonly number[]) => Math.hypot(...a.map((value, index) => value - b[index]));
const slots = (coffee = COFFEE_MINIMUM) => storyItems(coffee).map(spec => spec.slot);

test("the hero is the open box with the requested packages floating above it", () => {
  const frame = storyFrame(0, ACTS_FULL);
  assert.deepEqual(frame.camera, CAMERA_KEYS[0]);
  assert.deepEqual(frame.flaps, { minor: 0, major: 0 }); assert.equal(frame.tape, 0); assert.equal(frame.label, 0);
  assert.equal(frame.trolley.visible, false); assert.ok(frame.checks.every(check => check === 0));
  frame.items.forEach(item => assert.ok(item.position[1] > BOX.height + 0.4, "every package hovers above the open box"));
});

test("Diajukan lines the request up above the box; nothing is packed yet", () => {
  const frame = storyFrame(1, ACTS_FULL);
  assert.deepEqual(frame.camera, CAMERA_KEYS[1]);
  const heights = frame.items.map(item => item.position[1]);
  assert.ok(Math.max(...heights) - Math.min(...heights) < 0.1, "one line");
  assert.ok(frame.items.every(item => item.rotation.every(angle => angle === 0)), "square to the camera");
  assert.ok(frame.checks.every(check => check === 0)); assert.equal(frame.flaps.major, 0);
});

test("Ditinjau toko packs every item into its slot, checks it, then closes, tapes and labels the box", () => {
  const frame = storyFrame(2, ACTS_FULL);
  assert.deepEqual(frame.camera, CAMERA_KEYS[2]);
  frame.items.forEach((item, index) => assert.ok(distance(item.position, slots()[index]) < 1e-9, `item ${index} rests in its slot`));
  assert.ok(storyFrame(1.58, ACTS_FULL).checks.every(check => check === 1), "every item is checked once it is in");
  assert.ok(frame.checks.every(check => check === 0), "the checks fade as the flaps close over them");
  assert.deepEqual(frame.flaps, { minor: 1, major: 1 }); assert.equal(frame.tape, 1); assert.equal(frame.label, 1);
  assert.equal(frame.trolley.visible, false); assert.deepEqual(frame.box.position, [0, 0, 0]);
});

test("items drop one after another and the box only closes once all are in", () => {
  const count = storyItems(2).length;
  for (let index = 1; index < count; index++) assert.ok(dropWindow(index, count)[0] > dropWindow(index - 1, count)[0]);
  const last = dropWindow(count - 1, count);
  assert.ok(last[1] <= 1.62, "the final item lands before the first flap moves");
  const [start, end] = dropWindow(1, count), before = storyFrame(start - 0.01, ACTS_FULL, 2), after = storyFrame(end + 0.01, ACTS_FULL, 2);
  assert.ok(before.items[1].position[1] > BOX.height, "still above the box before its window");
  assert.ok(distance(after.items[1].position, slots(2)[1]) < 1e-9, "in its slot after its window");
  assert.equal(storyFrame(1.6, ACTS_FULL).flaps.major, 0);
});

test("the pilot story stops packed and sealed and never shows the trolley", () => {
  assert.deepEqual(storyFrame(3.5, ACTS_PILOT), storyFrame(ACTS_PILOT, ACTS_PILOT));
  for (let progress = 0; progress <= 4; progress += 0.05) assert.equal(storyFrame(progress, ACTS_PILOT).trolley.visible, false);
});

test("Dikirim: the trolley slides under the box, tips it back about the axle and wheels it to the branch", () => {
  const arrived = storyFrame(2.18, ACTS_FULL);
  assert.ok(Math.abs(arrived.trolley.x - TROLLEY_PICKUP) < 1e-6); assert.deepEqual(arrived.box.position, [0, 0, 0]);
  const riding = storyFrame(2.6, ACTS_FULL);
  assert.ok(riding.trolley.tilt > 0.3); assert.equal(riding.box.tilt, riding.trolley.tilt);
  assert.deepEqual(riding.box.position, onTrolleyPose(riding.trolley.x, riding.trolley.tilt, 1).position);
  assert.ok(riding.route > 0 && riding.route < 1, "the route draws while they travel");
});

test("Diterima: the box sits on the counter, the receipt is stamped and the invoice appears", () => {
  const received = storyFrame(ACTS_FULL, ACTS_FULL);
  assert.deepEqual(received.camera, CAMERA_KEYS[4]); assert.deepEqual(received.box.position, ON_COUNTER); assert.equal(received.box.tilt, 0);
  assert.equal(received.trolley.visible, false, "the trolley has gone"); assert.equal(received.receipt, 1);
  assert.equal(received.stamp.imprint, 1); assert.equal(received.stamp.height, 1, "the stamp has lifted away"); assert.equal(received.invoice, 1);
  assert.equal(storyFrame(3.5, ACTS_FULL).stamp.imprint, 0, "no imprint before the stamp lands");
});

test("the story is continuous: no jump in camera, box or packages between nearby scroll positions", () => {
  for (const coffee of [COFFEE_MINIMUM, COFFEE_MAXIMUM]) {
    let previous = storyFrame(0, ACTS_FULL, coffee);
    for (let progress = 0.002; progress <= ACTS_FULL; progress += 0.002) {
      const frame = storyFrame(progress, ACTS_FULL, coffee);
      assert.ok(distance(frame.camera.position, previous.camera.position) < 0.2, `camera jumps at ${progress.toFixed(3)}`);
      assert.ok(distance(frame.box.position, previous.box.position) < 0.12, `box jumps at ${progress.toFixed(3)}`);
      frame.items.forEach((item, index) => assert.ok(distance(item.position, previous.items[index].position) < 0.2, `item ${index} jumps at ${progress.toFixed(3)}`));
      previous = frame;
    }
  }
});

test("the coffee stepper changes how many dus go in: a second stacks on the first, a third stands beside", () => {
  assert.equal(storyItems(1).length, 5); assert.equal(storyItems(3).length, 7);
  assert.equal(storyItems(0).length, 5, "clamped to the minimum"); assert.equal(storyItems(9).length, 7, "clamped to the maximum");
  const coffee = storyItems(3).filter(spec => spec.kind === "coffee").map(spec => spec.slot);
  assert.ok(coffee[1][1] > coffee[0][1] && coffee[1][0] === coffee[0][0], "the second stacks on the first");
  assert.ok(coffee[2][1] === coffee[0][1] && coffee[2][0] > coffee[0][0], "the third stands beside them");
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

// The film cut is a pure function of time too, so the renderer can ask for any frame in any order.
test("the film holds on every act and moves smoothly and only forward between them", () => {
  assert.equal(filmProgress(-1), 0); assert.equal(filmProgress(0), 0); assert.equal(filmProgress(FILM_SECONDS), ACTS_FULL); assert.equal(filmProgress(FILM_SECONDS + 5), ACTS_FULL);
  for (const [seconds, act] of [[2, 0], [6, 1], [11.8, 2], [17.6, 3], [23, 4]] as const) assert.equal(filmProgress(seconds), act, `holds act ${act} at ${seconds} s`);
  let previous = 0;
  for (let frame = 1; frame <= FILM_SECONDS * 30; frame++) {
    const progress = filmProgress(frame / 30);
    assert.ok(progress >= previous - 1e-12, `never rewinds at frame ${frame}`); assert.ok(progress - previous < 0.06, `no jump at frame ${frame}`);
    previous = progress;
  }
});
