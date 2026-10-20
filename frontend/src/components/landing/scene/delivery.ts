import { BoxGeometry, CylinderGeometry, Group, Mesh, type Object3D, PlaneGeometry, TorusGeometry } from "three";
import { RoundedBoxGeometry } from "three/examples/jsm/geometries/RoundedBoxGeometry.js";
import { BOX, COUNTER } from "../journey";
import type { Kit } from "./kit";

const shaded = <T extends Mesh>(mesh: T) => { mesh.castShadow = true; mesh.receiveShadow = true; return mesh; };

/**
 * A two-wheel hand trolley: frame rails and cross bars, a toe plate the box stands on, wheels with orange hubs and a
 * looped handle. Its origin is on the floor under the axle; `tilt` rotates it back about the axle as it is pushed.
 */
export function buildTrolley(kit: Kit) {
  const m = kit.material, root = new Group(), frame = new Group(); root.add(frame);
  const wheelRadius = 0.17, frameX = 0.12, railHeight = 1.75, half = 0.36;
  frame.position.set(0, wheelRadius, 0);
  for (const z of [-half, half]) {
    const rail = shaded(new Mesh(kit.track(new CylinderGeometry(0.022, 0.022, railHeight, 12)), m.metal)); rail.position.set(frameX, railHeight / 2 - 0.05, z); frame.add(rail);
    const wheel = shaded(new Mesh(kit.track(new CylinderGeometry(wheelRadius, wheelRadius, 0.07, 28)), m.rubber)); wheel.rotation.x = Math.PI / 2; wheel.position.set(0, 0, z + Math.sign(z) * 0.06);
    const hub = shaded(new Mesh(kit.track(new CylinderGeometry(wheelRadius * 0.45, wheelRadius * 0.45, 0.075, 20)), m.hub)); hub.rotation.x = Math.PI / 2; hub.position.copy(wheel.position);
    frame.add(wheel, hub);
  }
  for (const y of [0.35, 0.85, 1.35]) { const bar = shaded(new Mesh(kit.track(new CylinderGeometry(0.016, 0.016, half * 2, 10)), m.metal)); bar.rotation.x = Math.PI / 2; bar.position.set(frameX, y, 0); frame.add(bar); }
  const axle = shaded(new Mesh(kit.track(new CylinderGeometry(0.018, 0.018, half * 2 + 0.14, 10)), m.metal)); axle.rotation.x = Math.PI / 2; frame.add(axle);
  const toe = shaded(new Mesh(kit.track(new BoxGeometry(0.42, 0.018, half * 2 + 0.04)), m.metal)); toe.position.set(frameX + 0.21, -wheelRadius + 0.03, 0); frame.add(toe);
  const handle = shaded(new Mesh(kit.track(new TorusGeometry(half, 0.022, 10, 24, Math.PI)), m.metal)); handle.rotation.y = Math.PI / 2; handle.position.set(frameX, railHeight - 0.05, 0); frame.add(handle);
  const grip = shaded(new Mesh(kit.track(new CylinderGeometry(0.03, 0.03, 0.2, 12)), m.rubber)); grip.rotation.x = Math.PI / 2; grip.position.set(frameX, railHeight - 0.05 + half, 0); frame.add(grip);
  return {
    root,
    /** World x of the toe plate's inner edge (where the box's back edge rests) and the axle height. */
    toeX: frameX, axleY: wheelRadius,
    place(x: number, tilt: number) { root.position.set(x, 0, 0); frame.rotation.z = tilt; },
  };
}

/** The branch counter: oak top on a teal body, a back panel with the branch sign, receipt, stamp and invoice. */
export function buildBranch(kit: Kit) {
  const m = kit.material, group = new Group(); group.position.set(COUNTER.x, 0, COUNTER.z);
  const length = 3.4, depth = 1.5, top = COUNTER.height;
  const body = shaded(new Mesh(kit.track(new RoundedBoxGeometry(length, top - 0.06, depth - 0.1, 2, 0.02)), m.panel)); body.position.y = (top - 0.06) / 2;
  const slab = shaded(new Mesh(kit.track(new RoundedBoxGeometry(length + 0.12, 0.06, depth, 2, 0.015)), m.oak)); slab.position.y = top - 0.03;
  // A low, light partition behind the counter with a teal sign band, so the box stays the brightest subject.
  const back = shaded(new Mesh(kit.track(new BoxGeometry(length + 1.2, 2.2, 0.08)), m.wall)); back.position.set(0, 1.1, -depth / 2 - 0.7);
  const sign = new Mesh(kit.track(new PlaneGeometry(2.2, 0.41)), m.sign); sign.position.set(0, 1.8, -depth / 2 - 0.655);
  group.add(body, slab, back, sign);
  const surface = top + 0.002;
  const receipt = shaded(new Mesh(kit.track(new PlaneGeometry(0.42, 0.58)), m.receipt)); receipt.rotation.x = -Math.PI / 2;
  const stamp = new Group();
  const handle = shaded(new Mesh(kit.track(new CylinderGeometry(0.045, 0.06, 0.2, 20)), m.stampWood)); handle.position.y = 0.21;
  const knob = shaded(new Mesh(kit.track(new CylinderGeometry(0.075, 0.075, 0.06, 20)), m.stampWood)); knob.position.y = 0.33;
  const block = shaded(new Mesh(kit.track(new RoundedBoxGeometry(0.32, 0.07, 0.17, 2, 0.01)), m.stampWood)); block.position.y = 0.075;
  const foot = shaded(new Mesh(kit.track(new BoxGeometry(0.3, 0.02, 0.15)), m.stampFoot)); foot.position.y = 0.03;
  stamp.add(handle, knob, block, foot);
  const invoice = shaded(new Mesh(kit.track(new PlaneGeometry(0.48, 0.64)), m.invoice));
  group.add(receipt, stamp, invoice);
  return {
    group, surface,
    /** Receipt slides out 0..1, the stamp height 1 (up) .. 0 (pressed), imprint once it has pressed, invoice rises 0..1. */
    set(receiptProgress: number, stampHeight: number, imprint: number, invoiceProgress: number) {
      receipt.visible = receiptProgress > 0; receipt.position.set(0.05 + 0.32 * receiptProgress, surface, 0.4);
      receipt.material = imprint > 0 ? m.receiptStamped : m.receipt;
      stamp.visible = receiptProgress >= 1 && (stampHeight < 1 || imprint === 0);
      stamp.position.set(0.37 + 0.04, surface + 0.02 + stampHeight * 0.75, 0.4 + 0.11); stamp.rotation.y = 0.3;
      invoice.visible = invoiceProgress > 0;
      invoice.position.set(0.95, surface + 0.02 + 0.45 * invoiceProgress, -0.2); invoice.rotation.set(-0.3, -0.38, 0);
      invoice.scale.setScalar(0.6 + 0.4 * invoiceProgress);
    },
  };
}

/** The dashed route on the floor from the packing spot to the counter; `reveal` 0..1 draws it from the start. */
export function buildRoute(kit: Kit) {
  const start = BOX.width / 2 + 0.6, end = COUNTER.x - 1.6, length = end - start;
  const geometry = kit.track(new PlaneGeometry(length, 0.12).rotateX(-Math.PI / 2).translate(length / 2, 0, 0));
  const route = new Mesh(geometry, kit.material.route); route.position.set(start, 0.004, 0.9); route.renderOrder = 2;
  const map = kit.material.route.map!; map.repeat.set(length / 1.2, 1);
  return {
    route: route as Object3D,
    reveal(amount: number) { route.visible = amount > 0; route.scale.x = Math.max(0.001, amount); map.repeat.x = Math.max(0.01, length * amount / 1.2); },
  };
}
