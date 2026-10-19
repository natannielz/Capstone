/**
 * Pure schedule for the landing scene, in scene units (1 unit is about 40 cm) and milliseconds. The three.js scene and
 * the static poster both read it, so the reduced-motion frame is exactly the frame the animation ends on.
 */
export const CARTON = { width: 1.6, depth: 1.1, height: 0.62, wall: 0.03, perCarton: 24, columns: 6, rows: 4 } as const;
export const MAX_CARTONS = 4;
export const OPENING_MS = 2600;
export const LEAVE_MS = 380;
export const LONG_FLAP_OPEN = 2.05;  // radians outward from closed (about 117°)
export const SHORT_FLAP_OPEN = 1.95;
const HIDDEN = -0.5;                 // bottle offset below its resting height while the carton is sealed
const ENTRY_HEIGHT = 2.2, ENTRY_MS = 520;

export type OpeningFrame = { tape: number; longFlap: number; shortFlap: number; items: number[] };
export type Slot = { x: number; z: number };

const clamp01 = (value: number) => Math.min(1, Math.max(0, value));
const progress = (ms: number, start: number, duration: number) => clamp01((ms - start) / duration);
const easeInOut = (t: number) => t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2;
const easeOut = (t: number) => 1 - (1 - t) ** 3;
const easeIn = (t: number) => t * t * t;
const easeOutBack = (t: number) => 1 + 2.70158 * (t - 1) ** 3 + 1.70158 * (t - 1) ** 2;

/** Rows run back to front, so the wave starts at the far row and sweeps towards the viewer. */
function itemDelay(index: number): number {
  const row = Math.floor(index / CARTON.columns), column = index % CARTON.columns;
  return (row + column * 0.6) * 120;
}

/** The front carton opening: a cut runs along the tape, the long flaps open, then the short flaps, then the bottles rise. */
export function openingFrame(ms: number): OpeningFrame {
  const items = Array.from({ length: CARTON.perCarton }, (_, index) => {
    const t = progress(ms, 1300 + itemDelay(index), 550);
    return t >= 1 ? 0 : HIDDEN * (1 - easeOutBack(t));
  });
  return {
    tape: easeInOut(progress(ms, 300, 400)),
    longFlap: LONG_FLAP_OPEN * easeOut(progress(ms, 500, 700)),
    shortFlap: SHORT_FLAP_OPEN * easeOut(progress(ms, 900, 600)),
    items,
  };
}

export function restingFrame(): OpeningFrame {
  return { tape: 1, longFlap: LONG_FLAP_OPEN, shortFlap: SHORT_FLAP_OPEN, items: Array.from({ length: CARTON.perCarton }, () => 0) };
}

/** Bottle centres inside the open carton, relative to its centre, in item order (back row first). */
export function bottleSpots(): Slot[] {
  const innerWidth = CARTON.width - 2 * CARTON.wall, innerDepth = CARTON.depth - 2 * CARTON.wall;
  return Array.from({ length: CARTON.perCarton }, (_, index) => {
    const row = Math.floor(index / CARTON.columns), column = index % CARTON.columns;
    return { x: -innerWidth / 2 + innerWidth / CARTON.columns * (column + 0.5), z: -innerDepth / 2 + innerDepth / CARTON.rows * (row + 0.5) };
  });
}

const SLOTS: Slot[] = [{ x: -0.85, z: 0.6 }, { x: 0.85, z: 0.6 }, { x: -0.85, z: -0.6 }, { x: 0.85, z: -0.6 }];

/** Fixed pallet slots: the open carton front-left (nearest the camera), then front-right, back-left, back-right. */
export function cartonSlots(count: number): Slot[] {
  return SLOTS.slice(0, Math.min(MAX_CARTONS, Math.max(1, Math.round(count))));
}

/** Height above the pallet of a carton that was just added. */
export function entryOffset(ms: number): number {
  return ENTRY_HEIGHT * (1 - easeOut(progress(ms, 0, ENTRY_MS)));
}

/** Height above the pallet of a carton being removed. */
export function leaveOffset(ms: number): number {
  return (ENTRY_HEIGHT + 0.6) * easeIn(progress(ms, 0, LEAVE_MS));
}
