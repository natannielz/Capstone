import { Group, Mesh, PlaneGeometry, ShadowMaterial, Sprite } from "three";
import { BRANCH_CENTER, BRANCH_DOOR, ROAD_Z } from "../journey";
import { H, LABEL_SIZE, T, type Kit } from "./kit";
import { part } from "./carton";

const FLOOR_Y = -0.215;

/** A flat decal on the floor (paint, road, paving), drawn without depth writes so shadows and objects sit on top. */
function decal(kit: Kit, material: Mesh["material"], width: number, depth: number, x: number, z: number, lift: number) {
  const mesh = new Mesh(kit.geometry.plane, material); mesh.rotation.x = -Math.PI / 2; mesh.scale.set(width, depth, 1);
  mesh.position.set(x, FLOOR_Y + lift, z); mesh.renderOrder = -1; return mesh;
}

/** Shadow-catching floor, the loading-bay paint, the road to the branch and the paving in front of it. */
export function buildGround(kit: Kit) {
  const ground = new Group(), shadowFloor = new Mesh(new PlaneGeometry(90, 50), new ShadowMaterial({ color: "#04191C", opacity: 0.32 }));
  shadowFloor.rotation.x = -Math.PI / 2; shadowFloor.position.set(9, FLOOR_Y, -2); shadowFloor.receiveShadow = true;
  ground.add(shadowFloor,
    decal(kit, kit.material.floorPaint, 4.8, 3.6, -0.1, 0.2, 0.004),
    decal(kit, kit.material.road, 18.6, 2.3, 11.2, ROAD_Z, 0.003),
    decal(kit, kit.material.pavement, 6.8, 4.4, BRANCH_CENTER[0], -1.0, 0.002));
  return { ground, dispose() { shadowFloor.geometry.dispose(); (shadowFloor.material as ShadowMaterial).dispose(); } };
}

/** A low branch office: render walls, a glass band, a glass door under a kraft awning and a generic sign. */
export function buildBranch(kit: Kit) {
  const { material: m, geometry: g } = kit, branch = new Group(), [cx, , cz] = BRANCH_CENTER, front = cz + 1.5;
  const box = (material: Mesh["material"], sx: number, sy: number, sz: number, x: number, y: number, z: number) => {
    const mesh = part(g.unit, material, x, y, z); mesh.scale.set(sx, sy, sz); return mesh;
  };
  branch.add(
    box(m.wall, 4.4, 2.2, 3.0, cx, FLOOR_Y + 1.1, cz),
    box(m.trim, 4.6, 0.14, 3.2, cx, FLOOR_Y + 2.27, cz),
    box(m.glass, 2.6, 0.72, 0.04, cx + 0.8, 1.05, front + 0.01),
    box(m.glass, 0.72, 1.18, 0.04, BRANCH_DOOR[0], FLOOR_Y + 0.6, front + 0.01),
    box(m.awning, 1.3, 0.07, 0.62, BRANCH_DOOR[0], 1.12, front + 0.31),
    box(m.trim, 4.4, 0.12, 0.05, cx, FLOOR_Y + 0.06, front + 0.02),
  );
  const sign = new Mesh(g.plane, m.sign); sign.scale.set(2.5, 0.39, 1); sign.position.set(cx + 0.8, 1.72, front + 0.012); branch.add(sign);
  for (const x of [cx + 1.3, cx + 2.6]) { const bush = part(g.bush, m.leaf, x, FLOOR_Y + 0.35, front + 0.5); branch.add(bush); }
  return branch;
}

/** The Unit Toko delivery van, facing +x: cargo box with printed sides, a teal cab, rolling wheels and a rear door. */
export function buildVan(kit: Kit) {
  const { material: m, geometry: g } = kit, van = new Group(), wheels: Group[] = [];
  const box = (material: Mesh["material"] | Mesh["material"][], sx: number, sy: number, sz: number, x: number, y: number, z: number) => {
    const mesh = part(g.unit, material as Mesh["material"], x, y, z); mesh.scale.set(sx, sy, sz); return mesh;
  };
  const b = m.vanBody, s = m.vanSide;
  van.add(
    box(m.trim, 3.5, 0.18, 1.3, 0, 0.36, 0),
    box([b, m.cargoInside, b, b, s, s], 2.3, 1.45, 1.36, -0.55, 1.175, 0),
    box(m.vanCab, 1.1, 1.05, 1.3, 1.2, 0.975, 0),
    box(m.glass, 0.05, 0.46, 1.16, 1.76, 1.22, 0),
    box(m.glass, 0.62, 0.38, 1.32, 1.24, 1.23, 0),
    box(m.trim, 0.12, 0.14, 1.34, 1.79, 0.44, 0),
  );
  for (const z of [-0.45, 0.45]) van.add(box(m.lamp, 0.03, 0.11, 0.2, 1.77, 0.64, z));
  for (const x of [-1.15, 1.15]) for (const z of [-0.62, 0.62]) {
    const wheel = new Group(), tyre = new Mesh(g.wheel, m.rubber), hub = new Mesh(g.hub, m.hub);
    tyre.rotation.x = hub.rotation.x = Math.PI / 2; tyre.castShadow = true; wheel.add(tyre, hub); wheel.position.set(x, 0.125, z); van.add(wheel); wheels.push(wheel);
  }
  // Rear door hinged on the cargo box's back edge; it swings open while the van waits at the dock.
  const hinge = new Group(); hinge.position.set(-1.73, 1.175, 0.68); const door = box(b, 0.04, 1.4, 1.34, 0, 0, -0.67); hinge.add(door); van.add(hinge);
  van.position.y = FLOOR_Y;
  return {
    van,
    update(x: number, doorOpen: number) {
      van.position.x = x; van.position.z = ROAD_Z;
      for (const wheel of wheels) wheel.rotation.z = -x / 0.34;
      hinge.rotation.y = -doorOpen * 1.75;
    },
  };
}

/** A rubber stamp, rubber face down, its origin at the face. */
export function buildStamp(kit: Kit) {
  const { material: m, geometry: g } = kit, stamp = new Group();
  const box = (material: Mesh["material"], sx: number, sy: number, sz: number, y: number) => { const mesh = part(g.unit, material, 0, y, 0); mesh.scale.set(sx, sy, sz); return mesh; };
  stamp.add(box(m.stampRubber, 0.32, 0.03, 0.15, 0.015), box(m.stampBody, 0.36, 0.12, 0.19, 0.09));
  const handle = part(g.handle, m.stampHandle, 0, 0.32, 0), knob = part(g.knob, m.stampHandle, 0, 0.52, 0);
  handle.scale.set(0.8, 0.8, 0.8); stamp.add(handle, knob);
  return stamp;
}

/** The request label on the front carton's lid, with the stamped version cross-fading over it. */
export function buildLabel(kit: Kit) {
  const label = new Group(), plain = new Mesh(kit.geometry.label, kit.material.label), stamped = new Mesh(kit.geometry.label, kit.material.labelStamped);
  plain.rotation.x = stamped.rotation.x = -Math.PI / 2; stamped.position.y = 0.0015; plain.receiveShadow = true;
  label.add(plain, stamped); label.visible = false;
  return { label, rest: { x: 0.3, y: H + 2 * T + 0.008, z: 0.28 }, size: LABEL_SIZE };
}

export function buildFinale(kit: Kit) {
  const check = new Sprite(kit.material.check); check.visible = false;
  const invoice = new Mesh(kit.geometry.plane, kit.material.invoice); invoice.scale.set(0.62, 0.83, 1); invoice.castShadow = true; invoice.visible = false;
  return { check, invoice };
}
