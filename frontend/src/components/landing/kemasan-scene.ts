import {
  DirectionalLight, Fog, HemisphereLight, Mesh, NeutralToneMapping, Object3D, PCFSoftShadowMap, PMREMGenerator, PerspectiveCamera,
  SRGBColorSpace, Scene, Vector3, WebGLRenderer,
} from "three";
import { RoomEnvironment } from "three/examples/jsm/environments/RoomEnvironment.js";
import { OPENING_MS, cartonSlots, entryOffset, leaveOffset, LEAVE_MS, openingFrame, restingFrame } from "./kemasan-timeline";
import { BRANCH_DOOR, VAN_DOCK_X, activeAct, journeyFrame } from "./journey";
import { createKit } from "./scene/kit";
import { applyFlaps, buildBottles, buildCarton, buildPallet, type Carton } from "./scene/carton";
import { buildBranch, buildFinale, buildGround, buildLabel, buildStamp, buildVan } from "./scene/diorama";
import type { Fonts } from "./scene/textures";

/**
 * "Satu karton, dari dok ke cabang". The hero shows cartons of bottled water on a pallet; the front carton is cut
 * open and its bottles rise once. Scrolling then drives journeyFrame: the carton is closed, labelled, stamped, carried
 * by van and delivered to a branch office. Rendering is on demand: frames run only while the scroll target, the
 * opening or a dropped carton is still moving, and never while the tab is hidden or the stage is off screen. Every
 * surface is painted locally, which keeps the scene inside the site CSP.
 */
export type KemasanScene = { setCartons(count: number): void; setJourney(progress: number): void; dispose(): void };
export type KemasanSceneOptions = { mode: "animated" | "still"; cartons: number; acts: number; fonts: Fonts; onLost(): void };

const DOCK = "#0F4C52";
const clamp01 = (value: number) => Math.min(1, Math.max(0, value));
const easeBack = (t: number) => 1 + 2.70158 * (t - 1) ** 3 + 1.70158 * (t - 1) ** 2;

async function fontsReady(fonts: Fonts) {
  // The print uses the self-hosted stencil face; draw with the fallback rather than wait long for it.
  try {
    await Promise.race([Promise.all([document.fonts.load(`66px ${fonts.stencil}`), document.fonts.load(`600 26px ${fonts.body}`)]),
      new Promise(resolve => setTimeout(resolve, 1500))]);
  } catch { /* fallback faces */ }
}

export async function createKemasanScene(canvas: HTMLCanvasElement, options: KemasanSceneOptions): Promise<KemasanScene> {
  await fontsReady(options.fonts);
  const phone = window.matchMedia("(max-width: 900px)").matches;
  const renderer = new WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: "high-performance" });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, phone ? 1.5 : 2));
  renderer.outputColorSpace = SRGBColorSpace; renderer.toneMapping = NeutralToneMapping; renderer.toneMappingExposure = 1.0;
  renderer.shadowMap.enabled = true; renderer.shadowMap.type = PCFSoftShadowMap;

  // Image-based light from three's procedural room (no files), a warm key that casts shadows and a cool rim.
  const scene = new Scene(), pmrem = new PMREMGenerator(renderer), room = new RoomEnvironment();
  scene.environment = pmrem.fromScene(room, 0.04).texture; scene.environmentIntensity = 0.6;
  scene.fog = new Fog(DOCK, 34, 80);
  const camera = new PerspectiveCamera(26, 1, 0.1, 140), up = new Vector3(0, 1, 0), keyOffset = new Vector3(-3.5, 6.5, 4.5);
  const key = new DirectionalLight("#FFF0DA", 2.3), keyTarget = new Object3D(); key.target = keyTarget; key.castShadow = true;
  key.shadow.mapSize.set(phone ? 1024 : 2048, phone ? 1024 : 2048); key.shadow.bias = -0.0004; key.shadow.normalBias = 0.02; key.shadow.radius = 3;
  const rim = new DirectionalLight("#9FD6D2", 0.8); rim.position.set(4.5, 3, -4.5);
  scene.add(new HemisphereLight("#E3F1EE", "#3B2B1A", 0.55), key, keyTarget, rim);

  const kit = createKit(renderer, options.fonts);
  const { ground, dispose: disposeGround } = buildGround(kit);
  const branch = buildBranch(kit), van = buildVan(kit), stamp = buildStamp(kit), finale = buildFinale(kit), label = buildLabel(kit);
  const pallet = buildPallet(kit), bottles = buildBottles(kit);
  const contact = new Mesh(kit.geometry.shadow, kit.material.shadow); contact.rotation.x = -Math.PI / 2; contact.position.y = -0.21;
  scene.add(ground, branch, van.van, stamp, finale.check, finale.invoice, pallet, contact);
  stamp.visible = false; van.van.visible = false;

  const cartons: Carton[] = [];
  const addCarton = (slot: number, enteredAt: number) => {
    const carton = buildCarton(kit, slot, enteredAt), position = cartonSlots(slot + 1)[slot];
    carton.group.position.set(position.x, 0, position.z); scene.add(carton.group); cartons.push(carton);
    applyFlaps(carton, openingFrame(0), 1, 0); return carton;
  };
  const front = addCarton(0, -Infinity);
  front.group.add(bottles.bodies, bottles.caps, label.label);
  let opening = options.mode === "still" ? restingFrame() : openingFrame(0);
  bottles.place(opening.items); applyFlaps(front, opening, 0, 0);

  let clock = 0, openingStart: number | null = options.mode === "still" ? -OPENING_MS : null;
  let targetProgress = 0, shownProgress = 0, yaw = 0, targetYaw = 0, aspect = 1, cartonCount = 0;

  // Cartons on the pallet at load stand still; later changes drop in or lift off (instantly in still mode).
  function setCartons(count: number, immediate = options.mode === "still") {
    const at = immediate ? -Infinity : clock, wanted = cartonSlots(count).length;
    if (wanted !== cartonCount) { cartonCount = wanted; kit.setCartonCount(wanted); }
    const present = cartons.filter(carton => carton.leavingAt === null);
    for (let slot = present.length; slot < wanted; slot++) {
      const leaving = cartons.find(carton => carton.slot === slot);
      if (leaving) { leaving.leavingAt = null; leaving.enteredAt = at; continue; }
      addCarton(slot, at);
    }
    for (const carton of present) if (carton.slot >= wanted && carton.slot > 0) carton.leavingAt = at;
    invalidate();
  }

  // Reduced motion jumps between finished acts; otherwise the story follows the scroll position.
  function setJourney(progress: number) {
    targetProgress = options.mode === "still" ? activeAct(progress) : progress;
    if (options.mode === "still") shownProgress = targetProgress;
    invalidate();
  }

  const position = new Vector3(), target = new Vector3();
  function update(dt: number): boolean {
    let busy = false;
    if (openingStart === null) openingStart = clock;
    const elapsed = clock - openingStart;
    opening = elapsed >= OPENING_MS ? restingFrame() : openingFrame(elapsed); busy ||= elapsed < OPENING_MS;
    // The visible story eases toward the scroll position, so a wheel notch glides instead of jumping.
    shownProgress += (targetProgress - shownProgress) * (1 - Math.exp(-dt / 140));
    if (Math.abs(targetProgress - shownProgress) < 0.0005) shownProgress = targetProgress; else busy = true;
    const frame = journeyFrame(shownProgress, options.acts), [cx, cy, cz] = frame.carton;

    bottles.place(opening.items); applyFlaps(front, opening, frame.flapClose, frame.tape);
    front.group.position.set(cx, cy, cz);
    // The label flies in from above with a small overshoot and lands flat on the lid; the stamped print fades over it.
    label.label.visible = frame.label > 0;
    if (label.label.visible) {
      const lift = 1 - easeBack(frame.label), tilt = 1 - frame.label;
      label.label.position.set(label.rest.x + 0.1 * lift, label.rest.y + 1.1 * lift, label.rest.z + 0.5 * lift);
      label.label.rotation.set(0.9 * tilt, 0, 0.4 * tilt);
    }
    kit.material.labelStamped.opacity = frame.stamp.imprint;
    stamp.visible = frame.stamp.shown;
    if (stamp.visible) stamp.position.set(cx + label.rest.x + 0.108, cy + label.rest.y + 0.003 + frame.stamp.height, cz + label.rest.z + 0.01);

    van.van.visible = frame.van.visible;
    const docked = shownProgress - 2, atDock = frame.van.visible && Math.abs(frame.van.x - VAN_DOCK_X) < 0.01;
    van.update(frame.van.x, atDock ? clamp01((docked - 0.26) / 0.04) * clamp01((0.6 - docked) / 0.05) : 0);
    finale.check.visible = frame.check > 0; finale.check.scale.setScalar(0.55 * easeBack(frame.check));
    finale.check.position.set(BRANCH_DOOR[0], 1.5, BRANCH_DOOR[2]);
    finale.invoice.visible = frame.invoice > 0; finale.invoice.rotation.y = -0.35;
    finale.invoice.position.set(BRANCH_DOOR[0] + 0.95, -0.2 + 0.85 * frame.invoice, BRANCH_DOOR[2] + 0.15);

    for (const carton of [...cartons]) {
      if (carton === front) continue;
      if (carton.leavingAt !== null) {
        const lifted = clock - carton.leavingAt;
        if (lifted >= LEAVE_MS) { scene.remove(carton.group); cartons.splice(cartons.indexOf(carton), 1); continue; }
        carton.group.position.y = leaveOffset(lifted); busy = true;
      } else { carton.group.position.y = entryOffset(clock - carton.enteredAt); busy ||= carton.group.position.y > 0; }
    }

    // Pointer parallax belongs to the hero; it fades out once the story starts. Narrow stages pull the camera back.
    yaw += (targetYaw - yaw) * 0.08; busy ||= Math.abs(targetYaw - yaw) > 0.0005;
    const hero = Math.max(0, 1 - shownProgress), distance = Math.min(1.9, Math.max(1, 1.05 / (camera.view?.enabled ? aspect * 0.62 : aspect)));
    target.set(...frame.camera.target);
    camera.position.copy(position.set(...frame.camera.position)).sub(target).multiplyScalar(distance).applyAxisAngle(up, yaw * hero).add(target);
    camera.lookAt(target);
    // The shadow-casting light follows what the camera looks at; its frustum widens with the shot.
    const reach = Math.min(16, Math.max(4.5, camera.position.distanceTo(target) * 0.42));
    keyTarget.position.copy(target); key.position.copy(target).add(keyOffset);
    if (key.shadow.camera.right !== reach) {
      Object.assign(key.shadow.camera, { left: -reach, right: reach, top: reach, bottom: -reach, near: 0.5, far: 40 });
      key.shadow.camera.updateProjectionMatrix();
    }
    return busy;
  }

  let frameRequest = 0, last = 0, onScreen = true, pageVisible = document.visibilityState !== "hidden", alive = true;
  function frame(now: number) {
    frameRequest = 0; const dt = last ? Math.min(now - last, 50) : 16; clock += dt; last = now;
    const busy = update(dt); renderer.render(scene, camera);
    if (busy && onScreen && pageVisible) frameRequest = requestAnimationFrame(frame); else last = 0;
  }
  function invalidate() { if (alive && !frameRequest && onScreen && pageVisible) frameRequest = requestAnimationFrame(frame); }
  function pause() { cancelAnimationFrame(frameRequest); frameRequest = 0; last = 0; }

  function resize() {
    const host = canvas.parentElement; if (!host) return;
    const width = Math.max(1, host.clientWidth), height = Math.max(1, host.clientHeight);
    renderer.setSize(width, height, false); aspect = width / height; camera.aspect = aspect;
    // On wide screens the stage runs behind the copy, so the subject is drawn right of centre, in the open space.
    const focus = window.matchMedia("(min-width: 901px)").matches ? 0.68 : 0.5;
    if (focus === 0.5) camera.clearViewOffset(); else camera.setViewOffset(width, height, -(focus - 0.5) * width, 0, width, height);
    camera.updateProjectionMatrix(); invalidate();
  }
  const sized = new ResizeObserver(resize); if (canvas.parentElement) sized.observe(canvas.parentElement);
  const seen = new IntersectionObserver(([entry]) => { onScreen = entry?.isIntersecting ?? true; if (onScreen) invalidate(); else pause(); });
  seen.observe(canvas);
  const visibility = () => { pageVisible = document.visibilityState !== "hidden"; if (pageVisible) invalidate(); else pause(); };
  document.addEventListener("visibilitychange", visibility);
  const fine = options.mode === "animated" && window.matchMedia("(pointer: fine)").matches;
  const pointer = (event: PointerEvent) => { targetYaw = (event.clientX / window.innerWidth - 0.5) * 0.14; invalidate(); };
  if (fine) window.addEventListener("pointermove", pointer, { passive: true });
  const lost = (event: Event) => { event.preventDefault(); pause(); options.onLost(); };
  canvas.addEventListener("webglcontextlost", lost);

  setCartons(options.cartons, true); resize();

  return {
    setCartons: count => setCartons(count),
    setJourney,
    dispose() {
      alive = false; pause(); sized.disconnect(); seen.disconnect();
      document.removeEventListener("visibilitychange", visibility); window.removeEventListener("pointermove", pointer);
      canvas.removeEventListener("webglcontextlost", lost);
      kit.dispose(); disposeGround(); bottles.bodies.dispose(); bottles.caps.dispose();
      scene.environment?.dispose(); pmrem.dispose(); room.dispose();
      renderer.dispose(); renderer.forceContextLoss();
    },
  };
}
