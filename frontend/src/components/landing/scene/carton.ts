import { BufferGeometry, Group, InstancedMesh, Material, Matrix4, Mesh, Quaternion, Vector3 } from "three";
import { bottleSpots, type OpeningFrame } from "../kemasan-timeline";
import { BAND_Y, BOTTLE_HEIGHT, CAP_HEIGHT, D, H, T, W, type Kit } from "./kit";

export type Carton = {
  group: Group; front: Group; back: Group; left: Group; right: Group; cut: Mesh; reseal: Mesh;
  slot: number; enteredAt: number; leavingAt: number | null;
};

export function part(shape: BufferGeometry, faces: Material | Material[], x: number, y: number, z: number) {
  const mesh = new Mesh(shape, faces); mesh.position.set(x, y, z); mesh.castShadow = mesh.receiveShadow = true; return mesh;
}

/** A corrugated carton: printed walls, liner inside, flute edges, four hinged flaps carrying the cut tape halves. */
export function buildCarton(kit: Kit, slot: number, enteredAt: number): Carton {
  const { material: m, geometry: g } = kit, e = m.edge, group = new Group();
  // Box faces run +x, -x, +y, -y, +z, -z: print outside, liner inside, flute on the cut edges.
  group.add(part(g.longWall, [e, e, e, e, m.long, m.inside], 0, H / 2, D / 2 - T / 2));
  group.add(part(g.longWall, [e, e, e, e, m.inside, m.long], 0, H / 2, -D / 2 + T / 2));
  group.add(part(g.shortWall, [m.inside, m.short, e, e, e, e], -W / 2 + T / 2, H / 2, 0));
  group.add(part(g.shortWall, [m.short, m.inside, e, e, e, e], W / 2 - T / 2, H / 2, 0));
  group.add(part(g.floor, m.inside, 0, T / 2, 0));
  const flapFaces = [e, e, m.plain, m.inside, e, e];
  const pivot = (x: number, y: number, z: number, flap: Mesh, tape?: Mesh) => {
    const hinge = new Group(); hinge.position.set(x, y, z); hinge.add(flap); if (tape) hinge.add(tape); group.add(hinge); return hinge;
  };
  // Long flaps sit on the short ones, so they hinge one board higher. Each long flap carries its half of the tape.
  const front = pivot(0, H + T, D / 2 - T / 2, part(g.longFlap, flapFaces, 0, T / 2, -D / 4), part(g.tape, m.tape, 0, T + 0.002, -D / 2 + 0.055));
  const back = pivot(0, H + T, -D / 2 + T / 2, part(g.longFlap, flapFaces, 0, T / 2, D / 4), part(g.tape, m.tape, 0, T + 0.002, D / 2 - 0.055));
  const left = pivot(-W / 2 + T / 2, H, 0, part(g.shortFlap, flapFaces, D / 4, T / 2, 0));
  const right = pivot(W / 2 - T / 2, H, 0, part(g.shortFlap, flapFaces, -D / 4, T / 2, 0));
  for (const side of [-1, 1]) group.add(part(g.tapeTail, m.tape, side * (W / 2 + 0.002), H - 0.08, 0));
  const cut = new Mesh(g.cut, m.cut); cut.position.y = H + 2 * T + 0.005; cut.visible = false; group.add(cut);
  // Fresh tape laid over the seam when the carton is closed again for its journey.
  const reseal = part(g.reseal, m.tape, 0, H + 2 * T + 0.004, 0); reseal.visible = false; group.add(reseal);
  return { group, front, back, left, right, cut, reseal, slot, enteredAt, leavingAt: null };
}

/** Flap angles from the opening (time) scaled down as the journey closes the carton; tape cut and reseal follow. */
export function applyFlaps(carton: Carton, opening: OpeningFrame, close: number, tape: number) {
  const open = 1 - close;
  carton.front.rotation.x = opening.longFlap * open; carton.back.rotation.x = -opening.longFlap * open;
  carton.left.rotation.z = opening.shortFlap * open; carton.right.rotation.z = -opening.shortFlap * open;
  carton.cut.visible = opening.tape > 0 && opening.longFlap < 0.02 && close === 0;
  carton.cut.scale.x = Math.max(0.001, opening.tape); carton.cut.position.x = -W / 2 + W * opening.tape / 2;
  carton.reseal.visible = tape > 0; carton.reseal.scale.x = Math.max(0.001, tape); carton.reseal.position.x = -W / 2 + W * tape / 2;
}

/** Seven deck boards on nine blocks and three runners; the deck top is y = 0. */
export function buildPallet(kit: Kit) {
  const { material: m, geometry: g } = kit, pallet = new Group();
  for (let i = 0; i < 7; i++) pallet.add(part(g.board, m.wood, 0, -0.0225, -1.15 + i * (2.3 / 6)));
  for (const x of [-1.6, 0, 1.6]) {
    for (const z of [-1.15, 0, 1.15]) pallet.add(part(g.block, m.wood, x, -0.11, z));
    pallet.add(part(g.runner, m.wood, x, -0.195, 0));
  }
  return pallet;
}

/** The 24 bottles of the front carton as instanced meshes: bodies, caps and (glass only) printed label bands. */
export function buildBottles(kit: Kit, glass: boolean) {
  const spots = bottleSpots();
  const bodies = new InstancedMesh(kit.geometry.bottle, glass ? kit.material.bottleGlass : kit.material.bottle, spots.length);
  const caps = new InstancedMesh(kit.geometry.cap, kit.material.cap, spots.length);
  const bands = new InstancedMesh(kit.geometry.band, kit.material.bottleBand, spots.length);
  bodies.castShadow = caps.castShadow = bands.castShadow = true; bodies.receiveShadow = bands.receiveShadow = true; bands.visible = glass;
  const matrix = new Matrix4(), turn = new Quaternion(), one = new Vector3(1, 1, 1), none = new Vector3(0, 0, 0), at = new Vector3();
  function place(offsets: readonly number[]) {
    offsets.forEach((offset, index) => {
      const scale = offset <= -0.499 ? none : one, { x, z } = spots[index];
      bodies.setMatrixAt(index, matrix.compose(at.set(x, T + offset, z), turn, scale));
      caps.setMatrixAt(index, matrix.compose(at.set(x, T + BOTTLE_HEIGHT + CAP_HEIGHT / 2 + offset, z), turn, scale));
      bands.setMatrixAt(index, matrix.compose(at.set(x, T + BAND_Y + offset, z), turn, scale));
    });
    bodies.instanceMatrix.needsUpdate = caps.instanceMatrix.needsUpdate = bands.instanceMatrix.needsUpdate = true;
  }
  /** Back to the opaque printed bottle when the renderer steps down from the cinematic tier. */
  function useOpaque() { bodies.material = kit.material.bottle; bands.visible = false; }
  return { bodies, caps, bands, place, useOpaque };
}
