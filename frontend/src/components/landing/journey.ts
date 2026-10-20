/**
 * "Satu kardus": the landing story as a pure function of scroll progress. One corrugated box carries one branch
 * request. Progress 0 is the hero; each whole number is a finished act (1 Diajukan, 2 Ditinjau toko, 3 Dikirim,
 * 4 Diterima). The pilot stops at 2, with the box packed, sealed and labelled.
 * World units: 1 unit is about 40 cm. The box stands at the origin; the route runs along +x to the branch counter.
 */
export type Vec3 = readonly [number, number, number];
export type CameraKey = { position: Vec3; target: Vec3 };
export type ItemKind = "coffee" | "tea" | "paper" | "pens" | "sanitizer";
export type ItemPose = { position: Vec3; rotation: Vec3; scale: number };
export type StoryFrame = {
  camera: CameraKey;
  /** One pose per entry of `storyItems(coffee)`, in the box's own frame. */
  items: ItemPose[];
  /** 0..1 pop of the check that marks each item as reviewed. */
  checks: number[];
  /** 0 open .. 1 closed, for the short (end) and long (side) flaps. */
  flaps: { minor: number; major: number };
  tape: number; label: number;
  box: { position: Vec3; tilt: number };
  trolley: { visible: boolean; x: number; tilt: number };
  route: number;
  receipt: number; stamp: { height: number; imprint: number }; invoice: number;
};

export const ACTS_FULL = 4, ACTS_PILOT = 2;
export const COFFEE_MINIMUM = 1, COFFEE_MAXIMUM = 3;

/** Inside dimensions of the box (x across, y up, z deep), in world units. */
export const BOX = { width: 1.5, height: 0.9, depth: 1.05, wall: 0.018 } as const;
/** Where the trolley stops at the branch and where the counter top is. */
export const COUNTER = { x: 11.2, z: -0.25, height: 1.05 } as const;
/** Hand trolley: its origin is on the floor under the axle; the box's back bottom edge rests on the toe plate. */
export const TROLLEY = { wheel: 0.17, toeX: 0.15, toeTop: 0.039 } as const;
const BACK_EDGE = BOX.width / 2 + BOX.wall;
/** Trolley stops: under the box at the packing spot, and beside the counter. */
export const TROLLEY_ENTRY = -4.2, TROLLEY_PICKUP = -BACK_EDGE - TROLLEY.toeX, TROLLEY_STOP = COUNTER.x - 3.3, TROLLEY_EXIT = 18;
export const ON_COUNTER: Vec3 = [COUNTER.x - 0.95, COUNTER.height, COUNTER.z];

type ItemSpec = { kind: ItemKind; size: Vec3; slot: Vec3; hover: Vec3; spin: Vec3 };
/** Package sizes are true to the catalog items, roughly: a coffee dus, a tea box, an A4 ream, a pen box, a 500 ml bottle. */
const SIZES: Record<ItemKind, Vec3> = {
  coffee: [0.42, 0.34, 0.28], tea: [0.4, 0.17, 0.18], paper: [0.53, 0.13, 0.75], pens: [0.36, 0.06, 0.12], sanitizer: [0.17, 0.48, 0.17],
};
const FLOOR = 0.03; // box floor thickness, items rest on it

/** The items in this request, in the order they go into the box. A second coffee dus stacks on the first; a third stands beside them. */
export function storyItems(coffee: number): ItemSpec[] {
  const count = Math.min(COFFEE_MAXIMUM, Math.max(COFFEE_MINIMUM, Math.round(coffee)));
  const [pw, ph, pd] = SIZES.paper, [cw, ch, cd] = SIZES.coffee, [tw, th, td] = SIZES.tea, [nw, nh, nd] = SIZES.pens, [sw, sh] = SIZES.sanitizer;
  const left = -BOX.width / 2, back = -BOX.depth / 2;
  const items: ItemSpec[] = [
    // The ream lies flat along the left wall; everything else stands beside it.
    { kind: "paper", size: SIZES.paper, slot: [left + 0.04 + pw / 2, FLOOR + ph / 2, back + 0.05 + pd / 2], hover: [-0.95, 1.55, 0.15], spin: [0.18, 0.5, -0.12] },
  ];
  // Two dus stack in the back corner; a third stands on the floor beside them (three would rise above the flaps).
  for (let index = 0; index < count; index++) {
    const beside = index === 2, x = left + pw + 0.1 + cw / 2 + (beside ? cw + 0.02 : 0), y = FLOOR + ch / 2 + (index === 1 ? ch : 0);
    items.push({ kind: "coffee", size: SIZES.coffee, slot: [x, y, back + 0.05 + cd / 2], hover: [-0.3 + index * 0.24, 1.98 + index * 0.3, -0.3], spin: [-0.2, -0.6 + index * 0.25, 0.15] });
  }
  items.push(
    { kind: "tea", size: SIZES.tea, slot: [left + pw + 0.1 + tw / 2, FLOOR + th / 2, back + 0.05 + cd + 0.06 + td / 2], hover: [0.48, 1.72, 0.35], spin: [0.25, 0.9, 0.2] },
    { kind: "pens", size: SIZES.pens, slot: [left + pw + 0.1 + nw / 2, FLOOR + th + nh / 2, back + 0.05 + cd + 0.06 + td / 2], hover: [0.1, 1.4, 0.62], spin: [-0.3, -0.4, 0.35] },
    { kind: "sanitizer", size: SIZES.sanitizer, slot: [BOX.width / 2 - 0.06 - sw / 2, FLOOR + sh / 2, back + 0.05 + cd + 0.06 + td / 2 + 0.05], hover: [1.0, 1.5, -0.05], spin: [0.15, 0.3, -0.25] },
  );
  void nh; void nd; void th;
  return items;
}

export const CAMERA_KEYS: readonly CameraKey[] = [
  { position: [5.8, 3.4, 8.8], target: [1.3, -0.02, 0] },     // hero: the open box high and left of the request card, packages floating above it
  { position: [3.0, 3.7, 6.2], target: [0.0, 1.05, 0] },     // Diajukan: the request lined up above the box
  { position: [2.9, 4.3, 4.4], target: [0.0, 0.5, 0] },      // Ditinjau toko: packed, closed, labelled
  { position: [COUNTER.x - 6.6, 3.5, 8.2], target: [COUNTER.x - 2.3, 0.85, 0] }, // Dikirim: trolley beside the counter
  { position: [COUNTER.x + 1.5, 3.5, 7.2], target: [COUNTER.x + 0.1, COUNTER.height + 0.3, COUNTER.z] }, // Diterima: box, stamped receipt and invoice on the counter
];

const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));
const smooth = (t: number) => t * t * (3 - 2 * t);
/** Eased 0..1 progress of `value` through [start, end]. */
const segment = (value: number, start: number, end: number) => smooth(clamp((value - start) / (end - start), 0, 1));
const linear = (value: number, start: number, end: number) => clamp((value - start) / (end - start), 0, 1);
const mix = (a: number, b: number, t: number) => t >= 1 ? b : t <= 0 ? a : a + (b - a) * t;
const mixVec = (a: Vec3, b: Vec3, t: number): Vec3 => [mix(a[0], b[0], t), mix(a[1], b[1], t), mix(a[2], b[2], t)];
/** Overshoot then settle: 0 at 0, 1 at 1, about 1.08 at its peak. */
export const easeBack = (t: number) => t <= 0 ? 0 : t >= 1 ? 1 : 1 + 2.2 * (t - 1) ** 3 + 1.2 * (t - 1) ** 2;
/** A dropped package: falls with gravity, lands at 1 and gives one small damped bounce. */
function drop(t: number): { fall: number; bounce: number } {
  if (t <= 0) return { fall: 0, bounce: 0 };
  if (t >= 1) return { fall: 1, bounce: 0 };
  const land = 0.72;
  if (t < land) return { fall: (t / land) ** 2, bounce: 0 };
  const s = (t - land) / (1 - land);
  return { fall: 1, bounce: Math.sin(s * Math.PI) * 0.06 * (1 - s) };
}

/** Item drop windows inside act 2 (1.04..1.5): one after another, each overlapping the next a little. */
export function dropWindow(index: number, count: number): [number, number] {
  const start = 1.04, span = 0.46, length = Math.min(0.2, span / Math.max(1, count) * 1.6);
  const step = count > 1 ? (span - length) / (count - 1) : 0;
  return [start + index * step, start + index * step + length];
}

function lineUp(index: number, count: number): Vec3 {
  const spread = 0.62, x = (index - (count - 1) / 2) * spread;
  return [x, 1.75 + Math.abs(x) * -0.06, 0.1];
}

function itemPose(spec: ItemSpec, index: number, count: number, progress: number, time: number | null): ItemPose {
  const [, h] = spec.size, lined = lineUp(index, count), above: Vec3 = [spec.slot[0], spec.slot[1] + 1.2 + index * 0.05, spec.slot[2]];
  // Hero to Diajukan: from a loose cloud into the request line.
  const gather = segment(progress, 0.05, 0.95);
  let position = mixVec(spec.hover, lined, gather);
  let rotation = mixVec(spec.spin, [0, 0, 0], gather);
  const bob = time === null ? 0 : Math.sin(time * 1.3 + index * 1.7) * 0.04 * (1 - gather);
  position = [position[0], position[1] + bob, position[2]];
  if (progress > 1) {
    const [start, end] = dropWindow(index, count), local = linear(progress, start, end);
    // First a short glide over the box, then the fall into the slot.
    const glide = segment(local, 0, 0.35), { fall, bounce } = drop((local - 0.35) / 0.65);
    position = mixVec(lined, above, glide);
    position = [mix(position[0], spec.slot[0], fall), mix(position[1], spec.slot[1], fall) + bounce * h * 3, mix(position[2], spec.slot[2], fall)];
    rotation = [0, 0, 0];
  }
  return { position, rotation, scale: 1 };
}

/** While the packages drop in, the camera rises to look down into the box: the walls are taller than the box is deep,
 * so from the act cameras' angle the front wall would hide everything on the box floor. */
export const PACKING_CAMERA: CameraKey = { position: [0.75, 5.5, 2.0], target: [0.0, 0.25, 0.02] };
const mixKey = (a: CameraKey, b: CameraKey, t: number): CameraKey => t <= 0 ? a : t >= 1 ? b : { position: mixVec(a.position, b.position, t), target: mixVec(a.target, b.target, t) };

function cameraAt(progress: number): CameraKey {
  if (progress > 1 && progress < 2) {
    const rise = segment(progress, 1.0, 1.28), settle = segment(progress, 1.66, 1.96);
    return settle > 0 ? mixKey(PACKING_CAMERA, CAMERA_KEYS[2], settle) : mixKey(CAMERA_KEYS[1], PACKING_CAMERA, rise);
  }
  const index = Math.min(CAMERA_KEYS.length - 2, Math.floor(progress)), t = smooth(clamp(progress - index, 0, 1));
  const from = CAMERA_KEYS[index], to = CAMERA_KEYS[index + 1];
  if (t <= 0) return from;
  if (t >= 1) return to;
  return { position: mixVec(from.position, to.position, t), target: mixVec(from.target, to.target, t) };
}

/** On the road the camera rides alongside the trolley, a little ahead of it. */
function travelCamera(progress: number, x: number): CameraKey {
  const follow: CameraKey = { position: [x - 3.4, 3.0, 6.2], target: [x + 1.2, 0.8, 0] };
  const enter = segment(progress, 2.0, 2.35), leave = segment(progress, 2.75, 3.0);
  const start = CAMERA_KEYS[2], end = CAMERA_KEYS[3];
  if (leave > 0) return { position: mixVec(follow.position, end.position, leave), target: mixVec(follow.target, end.target, leave) };
  return { position: mixVec(start.position, follow.position, enter), target: mixVec(start.target, follow.target, enter) };
}

/** The box riding the trolley: its back bottom edge on the toe plate, the pair rotated back by `tilt` about the axle. */
export function onTrolleyPose(trolleyX: number, tilt: number, seated: number): { position: Vec3; tilt: number } {
  const dx = TROLLEY.toeX, dy = TROLLEY.toeTop * seated - TROLLEY.wheel, cos = Math.cos(tilt), sin = Math.sin(tilt);
  const edgeX = trolleyX + dx * cos - dy * sin, edgeY = TROLLEY.wheel + dx * sin + dy * cos;
  return { position: [edgeX + BACK_EDGE, edgeY, 0], tilt };
}

/** The story frame at `progress` (clamped to the acts this release shows). `time` (seconds) adds the hero's idle float. */
export function storyFrame(progress: number, acts: number, coffee: number = COFFEE_MINIMUM, time: number | null = null): StoryFrame {
  const p = clamp(progress, 0, acts), specs = storyItems(coffee), count = specs.length;
  const items = specs.map((spec, index) => itemPose(spec, index, count, p, time));
  // Each check pops as its item lands and fades as the flaps begin to close over it.
  const checks = specs.map((_, index) => { const [, end] = dropWindow(index, count); return easeBack(linear(p, end - 0.02, end + 0.07)) * (1 - segment(p, 1.6, 1.68)); });
  const flaps = { minor: easeBack(linear(p, 1.62, 1.72)), major: easeBack(linear(p, 1.7, 1.82)) };
  const tape = segment(p, 1.82, 1.9), label = easeBack(linear(p, 1.9, 1.98));
  // Act 3: the trolley rolls in under the box, tips it back and wheels it to the branch counter.
  const trolleyIn = segment(p, 2.0, 2.18), travel = segment(p, 2.3, 3.0), away = segment(p, 3.22, 3.5);
  const onTrolley = segment(p, 2.18, 2.3);
  const trolleyX = p < 2.3 ? mix(TROLLEY_ENTRY, TROLLEY_PICKUP, trolleyIn) : p <= 3.22 ? mix(TROLLEY_PICKUP, TROLLEY_STOP, travel) : mix(TROLLEY_STOP, TROLLEY_EXIT, away);
  const trolley = { visible: p > 2 && trolleyX < TROLLEY_EXIT, x: trolleyX, tilt: onTrolley * (1 - segment(p, 2.96, 3.02)) * 0.32 };
  // Act 4: the box is lifted from the trolley onto the counter top, in a short arc.
  const lift = segment(p, 3.04, 3.22);
  let boxPosition: Vec3 = [0, 0, 0], tilt = 0;
  if (p > 2.18 && p <= 3.04) ({ position: boxPosition, tilt } = onTrolleyPose(trolleyX, trolley.tilt, onTrolley));
  else if (p > 3.04) {
    const from = onTrolleyPose(TROLLEY_STOP, 0, 1).position, arc = Math.sin(lift * Math.PI) * 0.35;
    boxPosition = mixVec(from, ON_COUNTER, lift); boxPosition = [boxPosition[0], boxPosition[1] + arc, boxPosition[2]];
  }
  const route = segment(p, 2.25, 2.95);
  const receipt = segment(p, 3.28, 3.45), press = linear(p, 3.48, 3.66);
  const stamp = { height: press <= 0 ? 1 : press < 0.45 ? 1 - press / 0.45 : press < 0.6 ? 0 : (press - 0.6) / 0.4, imprint: press >= 0.45 ? 1 : 0 };
  const invoice = segment(p, 3.7, 3.95);
  const camera = p > 2 && p < 3 ? travelCamera(p, trolleyX) : cameraAt(p);
  return { camera, items, checks, flaps, tape, label, box: { position: boxPosition, tilt }, trolley, route, receipt, stamp, invoice };
}

/** Story progress from viewport anchors (bottom of the hero copy, then each act card centre): act i is reached when
 * anchor i rises to the focus line; in between, progress moves linearly toward the next anchor. */
export function progressFromAnchors(anchors: readonly number[], focus: number): number {
  if (anchors.length === 0 || focus < anchors[0]) return 0;
  for (let index = 0; index < anchors.length - 1; index++)
    if (focus < anchors[index + 1]) return index + (focus - anchors[index]) / (anchors[index + 1] - anchors[index]);
  return anchors.length - 1;
}

export function activeAct(progress: number): number {
  return Math.max(0, Math.round(progress));
}

/** The film cut of the story: holds on each act and eased moves between them, 24 seconds in all. */
export const FILM_SECONDS = 24;
const FILM_KEYS: readonly (readonly [seconds: number, progress: number])[] = [
  [0, 0], [3.2, 0], [5.6, 1], [6.4, 1], [11.2, 2], [12.4, 2], [17.2, 3], [18, 3], [21.6, 4], [24, 4],
];
/** Story progress at `seconds` into the film: piecewise, eased in and out between keys, clamped at both ends. */
export function filmProgress(seconds: number): number {
  if (seconds <= 0) return 0;
  for (let index = 1; index < FILM_KEYS.length; index++) {
    const [t1, p1] = FILM_KEYS[index], [t0, p0] = FILM_KEYS[index - 1];
    if (seconds <= t1) return p0 + (p1 - p0) * smooth((seconds - t0) / (t1 - t0));
  }
  return FILM_KEYS[FILM_KEYS.length - 1][1];
}
