/**
 * "Satu karton, dari dok ke cabang": the landing story as a pure function of scroll progress. Progress 0 is the hero;
 * each whole number is a finished act (1 Diajukan, 2 Ditinjau toko, 3 Dikirim, 4 Diterima). The pilot stops at 2.
 * World units match kemasan-timeline (1 unit is about 40 cm): the pallet sits at the origin, the road runs along +x
 * at z = ROAD_Z and the branch office stands at its far end.
 */
export type Vec3 = readonly [number, number, number];
export type CameraKey = { position: Vec3; target: Vec3 };
export type JourneyFrame = {
  camera: CameraKey;
  flapClose: number; tape: number; label: number;
  stamp: { shown: boolean; height: number; imprint: number };
  van: { visible: boolean; x: number };
  carton: Vec3;
  check: number; invoice: number;
};

export const ACTS_FULL = 4, ACTS_PILOT = 2;
export const ROAD_Z = 0.4, VAN_DOCK_X = 4.0, BRANCH_DOCK_X = 16.6, VAN_ENTRY_X = 13, VAN_EXIT_X = 34;
export const CARTON_HOME: Vec3 = [-0.85, 0, 0.6];
export const CARGO_OFFSET: Vec3 = [-0.6, 0.42, 0];
export const BRANCH_DOOR: Vec3 = [19.6, 0, 1.0];
/** Where the carton is set down beside the parked van before it is wheeled to the door. */
export const UNLOAD_POINT: Vec3 = [BRANCH_DOCK_X + CARGO_OFFSET[0], 0, 1.7];
/** Branch office footprint: x 18.8..23.2, z -2.9..0.1, door on the front face at x = BRANCH_DOOR[0]. */
export const BRANCH_CENTER: Vec3 = [21.0, 0, -1.4];
/** The request label lies on the closed lid of the front carton (world position at home), so the stamp comes straight down. */
export const LABEL_SPOT: Vec3 = [-0.55, 0.69, 0.88];
export const STAMP_HEIGHT = 1.6;

/** Act 3 is a tracking shot: a fixed offset from the travelling carton, looking a little ahead of it. */
export function followCamera(subjectX: number): CameraKey {
  return { position: [subjectX - 7, 5.5, 11], target: [subjectX + 2.5, 0.4, 0] };
}

export const CAMERA_KEYS: readonly CameraKey[] = [
  { position: [-4.6, 5.6, 6.0], target: [0.15, -0.15, 0] },
  { position: [-3.4, 3.9, 5.4], target: [-0.55, 0.6, 0.85] },
  { position: [-2.7, 4.2, 4.8], target: [-0.5, 0.66, 0.85] },
  followCamera(BRANCH_DOCK_X + CARGO_OFFSET[0]),
  { position: [16.6, 3.4, 9.0], target: [19.8, 0.6, 0.6] },
];

const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));
const smooth = (t: number) => t * t * (3 - 2 * t);
/** Eased 0..1 progress of `value` through [start, end]. */
const segment = (value: number, start: number, end: number) => smooth(clamp((value - start) / (end - start), 0, 1));
const mix = (a: number, b: number, t: number) => t >= 1 ? b : t <= 0 ? a : a + (b - a) * t;
const mixVec = (a: Vec3, b: Vec3, t: number): Vec3 => [mix(a[0], b[0], t), mix(a[1], b[1], t), mix(a[2], b[2], t)];

function cameraAt(progress: number): CameraKey {
  const index = Math.min(CAMERA_KEYS.length - 2, Math.floor(progress)), t = smooth(clamp(progress - index, 0, 1));
  const from = CAMERA_KEYS[index], to = CAMERA_KEYS[index + 1];
  if (t <= 0) return from;
  if (t >= 1) return to;
  return { position: mixVec(from.position, to.position, t), target: mixVec(from.target, to.target, t) };
}

/** From the stamped label close-up into the tracking shot that follows the carton onto the road. */
function trackingShot(s: number, subjectX: number): CameraKey {
  const from = CAMERA_KEYS[2], to = followCamera(subjectX), t = segment(s, 0, 0.3);
  return { position: mixVec(from.position, to.position, t), target: mixVec(from.target, to.target, t) };
}

/** Where the van is and whether it is on screen (full release, acts 3 and 4 only). */
function vanAt(progress: number): { visible: boolean; x: number } {
  if (progress <= 2) return { visible: false, x: VAN_ENTRY_X };
  const s = progress - 2;
  if (s < 0.55) return { visible: true, x: mix(VAN_ENTRY_X, VAN_DOCK_X, segment(s, 0, 0.3)) };
  if (progress <= 3) return { visible: true, x: mix(VAN_DOCK_X, BRANCH_DOCK_X, segment(s, 0.55, 1)) };
  // Once the carton is set down the van drives on past the branch and out of the shot.
  const x = mix(BRANCH_DOCK_X, VAN_EXIT_X, segment(progress - 3, 0.22, 0.62));
  return { visible: x < VAN_EXIT_X, x };
}

function cartonAt(progress: number, vanX: number): Vec3 {
  if (progress <= 2) return CARTON_HOME;
  const s = progress - 2, cargo = (x: number): Vec3 => [x + CARGO_OFFSET[0], CARGO_OFFSET[1], ROAD_Z + CARGO_OFFSET[2]];
  if (progress <= 3) {
    if (s < 0.3) return CARTON_HOME;
    if (s < 0.55) {
      // Lifted from the pallet in an arc and set into the van's cargo box.
      const t = segment(s, 0.3, 0.55), flat = mixVec(CARTON_HOME, cargo(VAN_DOCK_X), t);
      return [flat[0], flat[1] + Math.sin(Math.PI * t) * 0.9, flat[2]];
    }
    return cargo(vanX);
  }
  // Unloaded beside the van, then wheeled along the pavement to the branch door.
  const u = progress - 3;
  if (u < 0.2) return mixVec(cargo(BRANCH_DOCK_X), UNLOAD_POINT, segment(u, 0, 0.2));
  return mixVec(UNLOAD_POINT, BRANCH_DOOR, segment(u, 0.2, 0.45));
}

export function journeyFrame(progress: number, acts: number): JourneyFrame {
  const p = clamp(progress, 0, acts), review = p - 1;
  const stampHeight = review <= 0 || review >= 1 ? STAMP_HEIGHT
    : review < 0.35 ? mix(STAMP_HEIGHT, 0, segment(review, 0, 0.35)) : review < 0.5 ? 0 : mix(0, STAMP_HEIGHT, segment(review, 0.5, 0.85));
  const van = acts > ACTS_PILOT ? vanAt(p) : { visible: false, x: VAN_ENTRY_X };
  return {
    camera: acts > ACTS_PILOT && p > 2 && p < 3 ? trackingShot(p - 2, cartonAt(p, van.x)[0]) : cameraAt(p),
    flapClose: segment(p, 0.1, 0.55), tape: segment(p, 0.5, 0.7), label: segment(p, 0.6, 0.95),
    stamp: { shown: review > 0 && review < 1 && stampHeight < STAMP_HEIGHT, height: stampHeight, imprint: segment(review, 0.35, 0.4) },
    van, carton: acts > ACTS_PILOT ? cartonAt(p, van.x) : CARTON_HOME,
    check: segment(p - 3, 0.45, 0.7), invoice: segment(p - 3, 0.6, 1),
  };
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
