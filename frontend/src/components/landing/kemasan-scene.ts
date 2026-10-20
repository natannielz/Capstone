import {
  AgXToneMapping, Color, DirectionalLight, Fog, HemisphereLight, MeshPhysicalMaterial, Object3D, PCFSoftShadowMap, PMREMGenerator, PerspectiveCamera,
  SRGBColorSpace, Scene, Vector3, WebGLRenderer,
} from "three";
import { RoomEnvironment } from "three/examples/jsm/environments/RoomEnvironment.js";
import { BOX, COFFEE_MAXIMUM, COFFEE_MINIMUM, activeAct, storyFrame, storyItems } from "./journey";
import { createKit } from "./scene/kit";
import { buildBox } from "./scene/box";
import { buildItems } from "./scene/items";
import { buildBranch, buildRoute, buildTrolley } from "./scene/delivery";
import { buildContactShadows, buildStudio } from "./scene/studio";
import { buildBranchDressing, buildShopSet } from "./scene/set";
import type { Fonts } from "./scene/textures";
import { createPipeline, type Pipeline } from "./scene/pipeline";
import { focusForShot, qualityOverride, qualityTier, shouldDowngrade } from "./scene/quality";

/**
 * "Satu kardus": one corrugated box carries one branch request. The hero shows it open with the requested packages
 * floating above it; scrolling drives storyFrame, which drops the packages in, closes, tapes and labels the box,
 * wheels it to the branch counter and records the receipt and invoice. Rendering is on demand: frames run only while
 * the story is moving (or the hero's packages float), never while the tab is hidden or the stage is off screen.
 * Every surface is painted locally, which keeps the scene inside the site CSP.
 */
export type KemasanScene = { setCoffee(count: number): void; setJourney(progress: number): void; dispose(): void };
export type KemasanSceneOptions = { mode: "animated" | "still"; coffee: number; acts: number; fonts: Fonts; onLost(): void };

const STAGE = "#DCE4E1";
const smoothstep = (value: number, low: number, high: number) => { const t = Math.min(1, Math.max(0, (value - low) / (high - low))); return t * t * (3 - 2 * t); };
const clampCoffee = (value: number) => Math.min(COFFEE_MAXIMUM, Math.max(COFFEE_MINIMUM, Math.round(value)));

async function fontsReady(fonts: Fonts) {
  // Package print uses the self-hosted stencil face; draw with the fallback rather than wait long for it.
  try {
    await Promise.race([Promise.all([document.fonts.load(`66px ${fonts.stencil}`), document.fonts.load(`600 26px ${fonts.body}`)]),
      new Promise(resolve => setTimeout(resolve, 1500))]);
  } catch { /* fallback faces */ }
}

export async function createKemasanScene(canvas: HTMLCanvasElement, options: KemasanSceneOptions): Promise<KemasanScene> {
  await fontsReady(options.fonts);
  const phone = window.matchMedia("(max-width: 900px)").matches;
  const forced = qualityOverride(window.location.search);
  let tier = qualityTier({ phone, cores: navigator.hardwareConcurrency || 4, override: forced });
  const renderer = new WebGLRenderer({ canvas, antialias: tier === "balanced", powerPreference: "high-performance" });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, phone ? 1.5 : tier === "cinematic" ? 1.75 : 2));
  // AgX keeps highlights on paper and plastic soft and the kraft's warmth intact, like a product photograph.
  renderer.outputColorSpace = SRGBColorSpace; renderer.toneMapping = AgXToneMapping; renderer.toneMappingExposure = 1.05;
  renderer.shadowMap.enabled = true; renderer.shadowMap.type = PCFSoftShadowMap;

  // Studio light: a procedural room for reflections, a large warm key that casts soft shadows, a cool fill and a rim.
  const scene = new Scene(), pmrem = new PMREMGenerator(renderer), room = new RoomEnvironment();
  scene.environment = pmrem.fromScene(room, 0.03).texture; scene.environmentIntensity = 0.58;
  scene.background = new Color(STAGE); scene.fog = new Fog(STAGE, 16, 46);
  const camera = new PerspectiveCamera(28, 1, 0.1, 120), up = new Vector3(0, 1, 0), keyOffset = new Vector3(-3.2, 7.5, 5.2);
  const key = new DirectionalLight("#FFF3E2", 2.6), keyTarget = new Object3D(); key.target = keyTarget; key.castShadow = true;
  key.shadow.mapSize.set(phone ? 1024 : 2048, phone ? 1024 : 2048); key.shadow.bias = -0.0003; key.shadow.normalBias = 0.015;
  key.shadow.radius = tier === "cinematic" ? 6 : 4; key.shadow.blurSamples = 16;
  const rim = new DirectionalLight("#CFE9EE", 1.1); rim.position.set(5, 4, -6);
  scene.add(new HemisphereLight("#F2F7F6", "#8C9A97", 0.38), key, keyTarget, rim);

  const kit = createKit(renderer, options.fonts, tier === "cinematic");
  const studio = buildStudio(kit), box = buildBox(kit), items = buildItems(kit), trolley = buildTrolley(kit), branch = buildBranch(kit), route = buildRoute(kit);
  const shop = buildShopSet(kit), dressing = buildBranchDressing(kit);
  items.all.forEach(item => box.content.add(item));
  const shadows = buildContactShadows(renderer, kit, { size: 5, resolution: phone ? 256 : 512, far: 1.4, blur: 2.6, opacity: 0.62 });
  scene.add(studio.group, shop.group, dressing.group, box.root, trolley.root, branch.group, route.route, shadows.plane);
  const overlay = new Scene(); items.checks.forEach(check => overlay.add(check));
  const shadowExcluded = [...studio.excluded, ...shop.excluded, dressing.group, route.route];
  // Time of day in the light: warm morning through the shop window, neutral daylight on the road, a cooler office at the branch.
  const morningKey = new Color("#FFE4C2"), dayKey = new Color("#FFF5EA"), officeKey = new Color("#EAF1FF");

  let pipeline: Pipeline = createPipeline(renderer, scene, overlay, camera, tier);
  const frameTimes: number[] = [];
  let clock = 0, targetProgress = 0, shownProgress = 0, yaw = 0, targetYaw = 0, aspect = 1, coffee = clampCoffee(options.coffee);
  let shadowsDirty = true;

  function setCoffee(count: number) { const next = clampCoffee(count); if (next !== coffee) { coffee = next; shadowsDirty = true; invalidate(); } }
  // Reduced motion jumps between finished acts; otherwise the story follows the scroll position.
  function setJourney(progress: number) {
    targetProgress = options.mode === "still" ? activeAct(progress) : progress;
    if (options.mode === "still") shownProgress = targetProgress;
    invalidate();
  }

  const position = new Vector3(), target = new Vector3(), focusPoint = new Vector3(), world = new Vector3(), shift = new Vector3();
  let lastBox = "";
  function update(dt: number): boolean {
    let busy = false;
    // The visible story eases toward the scroll position, so a wheel notch glides instead of jumping.
    shownProgress += (targetProgress - shownProgress) * (1 - Math.exp(-dt / 160));
    if (Math.abs(targetProgress - shownProgress) < 0.0005) shownProgress = targetProgress; else busy = true;
    const floating = options.mode === "animated" && shownProgress < 0.95;
    const frame = storyFrame(shownProgress, options.acts, coffee, floating ? clock / 1000 : null);
    busy ||= floating;

    box.set(frame.flaps, frame.tape, frame.label);
    box.place(...frame.box.position, frame.box.tilt);
    trolley.root.visible = frame.trolley.visible; trolley.place(frame.trolley.x, frame.trolley.tilt);
    route.reveal(frame.route);
    branch.set(frame.receipt, frame.stamp.height, frame.stamp.imprint, frame.invoice);
    const placed = items.apply(frame, coffee);
    box.root.updateMatrixWorld(true);
    // A package stacked on another gets its check beside the lower one's, so the two never sit on top of each other.
    const specs = storyItems(coffee);
    items.checks.forEach((check, index) => {
      // Below a fifth of its size a fading check reads as a stray dot, so it simply goes.
      const pop = frame.checks[index] ?? 0, item = placed[index]; check.visible = pop > 0.2 && !!item && shownProgress < 2.05;
      if (!check.visible || !item) return;
      const spec = specs[index], stacked = !!spec && spec.slot[1] - spec.size[1] / 2 > 0.05;
      item.getWorldPosition(world); check.position.set(world.x + (stacked ? 0.18 : 0), world.y + 0.32, world.z); check.scale.setScalar(0.16 * pop);
    });

    const morning = 1 - smoothstep(shownProgress, 2.0, 2.45), office = smoothstep(shownProgress, 3.0, 3.4);
    key.color.copy(dayKey).lerp(morningKey, morning).lerp(officeKey, office); key.intensity = 2.8 + 0.5 * morning - 0.2 * office;
    shop.update(morning, 1 - smoothstep(shownProgress, 0.9, 1.2), clock / 1000); pipeline.setTime(clock / 1000);
    // Each place appears only in its own acts: the shop until the trolley has left it, the branch office once it is near.
    shop.group.visible = shownProgress < 2.7; dressing.group.visible = shownProgress > 2.2;

    // Pointer parallax belongs to the hero; it fades out once the story starts. Narrow stages pull the camera back.
    yaw += (targetYaw - yaw) * 0.08; busy ||= Math.abs(targetYaw - yaw) > 0.0005;
    const hero = Math.max(0, 1 - shownProgress), distance = Math.min(1.9, Math.max(1, 1.05 / (camera.view?.enabled ? aspect * 0.62 : aspect)));
    target.set(...frame.camera.target);
    // The hero frames the box left of the request card; a narrow stage has no card beside it, so centre the box.
    const recentre = camera.view?.enabled ? 0 : 1.1 * hero;
    target.x -= recentre;
    camera.position.copy(position.set(...frame.camera.position)).add(shift.set(-recentre, 0, 0)).sub(target).multiplyScalar(distance).applyAxisAngle(up, yaw * hero).add(target);
    // While the hero rests, the camera breathes a little, like a hand-held shot; it settles as soon as the story moves.
    if (floating) { const t = clock / 1000; camera.position.x += Math.sin(t * 0.37) * 0.03 * hero; camera.position.y += Math.sin(t * 0.29 + 1.3) * 0.02 * hero; }
    camera.lookAt(target); camera.updateMatrixWorld();
    // Focus follows the box; the aperture opens for the packing close-ups and narrows for the wide shots.
    focusPoint.set(frame.box.position[0], frame.box.position[1] + BOX.height * 0.6, frame.box.position[2]);
    const focus = focusForShot(shownProgress); pipeline.setFocus(-focusPoint.applyMatrix4(camera.matrixWorldInverse).z, focus.aperture, focus.maxBlur);
    // The shadow-casting light follows the box; its frustum stays tight for crisp, soft contact edges.
    const reach = Math.min(9, Math.max(3.2, camera.position.distanceTo(target) * 0.32));
    keyTarget.position.copy(target); key.position.copy(target).add(keyOffset);
    if (key.shadow.camera.right !== reach) {
      Object.assign(key.shadow.camera, { left: -reach, right: reach, top: reach, bottom: -reach, near: 0.5, far: 30 });
      key.shadow.camera.updateProjectionMatrix();
    }
    // Contact shadows ground the box on the floor; they are redrawn only when the box or what it holds has moved.
    // (Floating packages hover above the shadow camera's reach, so the hero's idle motion never redraws them.)
    const grounded = frame.box.position[1] < 0.5, signature = shownProgress.toFixed(4) + ":" + coffee;
    if (grounded && (shadowsDirty || signature !== lastBox)) {
      lastBox = signature; shadowsDirty = false;
      shadows.render(scene, shadowExcluded, frame.box.position[0] + 0.1, frame.box.position[2]);
    }
    shadows.plane.visible = grounded;
    return busy;
  }

  let frameRequest = 0, last = 0, onScreen = true, pageVisible = document.visibilityState !== "hidden", alive = true;
  function frame(now: number) {
    frameRequest = 0; const dt = last ? Math.min(now - last, 50) : 16; clock += dt; last = now;
    const busy = update(dt); pipeline.render();
    // Measure the cinematic tier while it animates; step down once if it cannot hold about 40 fps.
    if (tier === "cinematic" && !forced && busy && last && frameTimes.length < 90) {
      frameTimes.push(dt);
      if (shouldDowngrade(frameTimes)) {
        tier = "balanced"; pipeline.dispose(); pipeline = createPipeline(renderer, scene, overlay, camera, tier); switchToOpaqueBottle(); frameTimes.length = 90;
      }
    }
    if (busy && onScreen && pageVisible) frameRequest = requestAnimationFrame(frame); else last = 0;
  }
  function switchToOpaqueBottle() {
    const bottle = kit.material.bottle as MeshPhysicalMaterial;
    bottle.transmission = 0; bottle.color.set("#CFE7EC"); bottle.roughness = 0.18; bottle.needsUpdate = true;
  }
  function invalidate() { if (alive && !frameRequest && onScreen && pageVisible) frameRequest = requestAnimationFrame(frame); }
  function pause() { cancelAnimationFrame(frameRequest); frameRequest = 0; last = 0; }

  function resize() {
    const host = canvas.parentElement; if (!host) return;
    const width = Math.max(1, host.clientWidth), height = Math.max(1, host.clientHeight);
    renderer.setSize(width, height, false); pipeline.setSize(width, height); aspect = width / height; camera.aspect = aspect;
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

  resize();

  return {
    setCoffee,
    setJourney,
    dispose() {
      alive = false; pause(); sized.disconnect(); seen.disconnect();
      document.removeEventListener("visibilitychange", visibility); window.removeEventListener("pointermove", pointer);
      canvas.removeEventListener("webglcontextlost", lost);
      pipeline.dispose(); shadows.dispose(); shop.dispose(); dressing.dispose(); kit.dispose();
      scene.environment?.dispose(); pmrem.dispose(); room.dispose();
      renderer.dispose(); renderer.forceContextLoss();
    },
  };
}
