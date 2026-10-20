import { BoxGeometry, Group, type Material, Mesh, Object3D, PlaneGeometry } from "three";
import { BOX } from "../journey";
import type { Kit } from "./kit";

/**
 * The regular slotted carton: four walls of real thickness (printed outside, plain liner inside, flute on every cut
 * edge), a floor, four hinged flaps, the tape strip and the request label. The group's origin is the centre of the
 * box floor; `tilt` swings it about the bottom back edge, the way a box tips onto a hand trolley.
 */
const W = BOX.width, D = BOX.depth, H = BOX.height, T = BOX.wall, FLAP = D / 2;
const OPEN_MAJOR = 2.05, OPEN_MINOR = 1.95;

export function buildBox(kit: Kit) {
  const m = kit.material;
  const root = new Group(), pivot = new Group(), body = new Group();
  // Tilting happens about the bottom edge on the -x side (where the trolley's frame is).
  pivot.position.set(-W / 2 - T, 0, 0); body.position.set(W / 2 + T, 0, 0); root.add(pivot); pivot.add(body);
  const shaded = (mesh: Mesh) => { mesh.castShadow = true; mesh.receiveShadow = true; return mesh; };
  const slab = (size: [number, number, number], materials: Material[]) => shaded(new Mesh(kit.track(new BoxGeometry(...size)), materials));

  // Face order for BoxGeometry: +x, -x, +y, -y, +z, -z. Outer print faces out of the box; liner faces in.
  const front = slab([W + 2 * T, H, T], [m.edge, m.edge, m.edge, m.edge, m.long, m.inside]); front.position.set(0, H / 2, D / 2 + T / 2);
  const back = slab([W + 2 * T, H, T], [m.edge, m.edge, m.edge, m.edge, m.inside, m.long]); back.position.set(0, H / 2, -D / 2 - T / 2);
  const right = slab([T, H, D], [m.short, m.inside, m.edge, m.edge, m.edge, m.edge]); right.position.set(W / 2 + T / 2, H / 2, 0);
  const left = slab([T, H, D], [m.inside, m.short, m.edge, m.edge, m.edge, m.edge]); left.position.set(-W / 2 - T / 2, H / 2, 0);
  const floor = slab([W, T, D], [m.edge, m.edge, m.inside, m.inside, m.edge, m.edge]); floor.position.set(0, T / 2, 0);
  body.add(front, back, right, left, floor);

  // Flaps hinge on the top edge of their wall. Minor (end) flaps fold first and lie under the major (side) flaps.
  const hinge = (x: number, y: number, z: number) => { const node = new Object3D(); node.position.set(x, y, z); body.add(node); return node; };
  const majorFront = hinge(0, H + T, D / 2 + T), majorBack = hinge(0, H + T, -D / 2 - T);
  const minorRight = hinge(W / 2 + T, H, 0), minorLeft = hinge(-W / 2 - T, H, 0);
  const major = (node: Object3D, sign: number) => {
    // Upright, the flap continues the wall upward; its outer face is the plain liner of the same board.
    const flap = slab([W + 2 * T, FLAP, T], [m.edge, m.edge, m.edge, m.edge, sign > 0 ? m.plain : m.inside, sign > 0 ? m.inside : m.plain]);
    flap.position.set(0, FLAP / 2, -sign * T / 2); node.add(flap); return flap;
  };
  const minor = (node: Object3D, sign: number) => {
    const flap = slab([T, FLAP, D], [sign > 0 ? m.plain : m.inside, sign > 0 ? m.inside : m.plain, m.edge, m.edge, m.edge, m.edge]);
    flap.position.set(-sign * T / 2, FLAP / 2, 0); node.add(flap); return flap;
  };
  major(majorFront, 1); major(majorBack, -1); minor(minorRight, 1); minor(minorLeft, -1);

  // Tape runs along the seam where the major flaps meet, then down both ends.
  const tapeTop = shaded(new Mesh(kit.track(new BoxGeometry(1, 0.004, 0.16)), m.tape));
  const tapeEnds = [-1, 1].map(sign => { const end = shaded(new Mesh(kit.track(new BoxGeometry(0.004, 0.22, 0.16)), m.tape)); end.position.set(sign * (W / 2 + T + 0.003), H - 0.08, 0); body.add(end); return end; });
  body.add(tapeTop);
  const label = shaded(new Mesh(kit.track(new PlaneGeometry(0.62, 0.41)), m.label)); label.rotation.x = -Math.PI / 2;
  const labelRest = { x: 0.32, y: H + T + 0.006, z: 0.24 };
  body.add(label);
  const content = new Group(); body.add(content);

  return {
    root, content,
    /** Flap closure 0 (open) .. 1 (closed), tape 0..1 and label 0..1 from the story frame. */
    set(flaps: { minor: number; major: number }, tape: number, labelProgress: number) {
      minorRight.rotation.z = -OPEN_MINOR + (OPEN_MINOR + Math.PI / 2) * flaps.minor;
      minorLeft.rotation.z = OPEN_MINOR - (OPEN_MINOR + Math.PI / 2) * flaps.minor;
      majorFront.rotation.x = OPEN_MAJOR - (OPEN_MAJOR + Math.PI / 2) * flaps.major;
      majorBack.rotation.x = -OPEN_MAJOR + (OPEN_MAJOR + Math.PI / 2) * flaps.major;
      const length = (W + 2 * T + 0.02) * tape;
      tapeTop.visible = tape > 0; tapeTop.scale.x = Math.max(0.001, length);
      tapeTop.position.set(-(W / 2 + T + 0.01) + length / 2, H + T + 0.003, 0);
      tapeEnds.forEach((end, index) => { end.visible = index === 0 ? tape > 0.02 : tape > 0.98; });
      label.visible = labelProgress > 0;
      const lift = 1 - Math.min(1, labelProgress), tilt = 1 - Math.min(1, labelProgress);
      label.position.set(labelRest.x + 0.15 * lift, labelRest.y + 0.9 * lift, labelRest.z + 0.4 * lift);
      label.rotation.set(-Math.PI / 2 + 0.8 * tilt, 0, 0.35 * tilt);
      label.scale.setScalar(labelProgress > 1 ? 1 + (labelProgress - 1) * 0.6 : 1);
    },
    /** Places the box (its floor centre) and swings it about the bottom back edge by `tilt` radians. */
    place(x: number, y: number, z: number, tilt: number) { root.position.set(x, y, z); pivot.rotation.z = tilt; },
  };
}
export type Box = ReturnType<typeof buildBox>;
