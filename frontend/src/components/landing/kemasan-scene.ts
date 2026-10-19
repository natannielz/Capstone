import {
  ACESFilmicToneMapping, BoxGeometry, BufferGeometry, CanvasTexture, CylinderGeometry, DirectionalLight, Group, HemisphereLight,
  InstancedMesh, LatheGeometry, Material, Matrix4, Mesh, MeshBasicMaterial, MeshStandardMaterial, PCFSoftShadowMap,
  PerspectiveCamera, PlaneGeometry, Quaternion, SRGBColorSpace, Scene, Texture, Vector2, Vector3, WebGLRenderer,
} from "three";
import { CARTON, OPENING_MS, bottleSpots, cartonSlots, entryOffset, leaveOffset, LEAVE_MS, openingFrame, restingFrame, type OpeningFrame } from "./kemasan-timeline";

/**
 * The landing hero: cartons of bottled water on a pallet. The front carton is cut open and its 24 bottles rise once;
 * after that the scene only answers the visitor (cartons drop on or lift off, a slight pointer parallax). Rendering is
 * on demand: no frames while nothing moves, the tab is hidden or the stage is off screen. Everything is drawn locally
 * (canvas textures, no remote assets), which keeps it inside the site CSP.
 */
export type KemasanScene = { setCartons(count: number): void; dispose(): void };
export type KemasanSceneOptions = { mode: "animated" | "still"; cartons: number; stencil: string; onLost(): void };

const COLOR = { kraft: "#C69A62", kraftEdge: "#A97E4B", inside: "#9E7444", print: "#0F4C52", tape: "#D9C28B", cut: "#3B2A17",
  wood: "#D6B585", bottle: "#EEF6F5", cap: "#E8622A", shadow: "6,38,42" };
const W = CARTON.width, D = CARTON.depth, H = CARTON.height, T = CARTON.wall;
const BOTTLE_HEIGHT = 0.53, CAP_HEIGHT = 0.06;
// Unit Toko mark (a T held inside a U), the same path as BrandMark, printed on the cartons.
const BRAND_PATH = "M8 8h48v8H36v24h-8V16H8V8Zm0 18h8v12c0 8 6 14 16 14s16-6 16-14V26h8v12c0 13-10 22-24 22S8 51 8 38V26Z";

type Carton = { group: Group; slot: number; front: Group; back: Group; left: Group; right: Group; cut: Mesh; enteredAt: number; leavingAt: number | null };

function seeded(seed: number) { return () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 2 ** 32; }; }

/** Kraft liner: faint corrugation bands and recycled-fibre speckle. */
function board(width: number, height: number, base: string, seed: number) {
  const canvas = document.createElement("canvas"); canvas.width = width; canvas.height = height;
  const context = canvas.getContext("2d")!; const random = seeded(seed);
  context.fillStyle = base; context.fillRect(0, 0, width, height);
  for (let x = 0; x < width; x += 7) { context.fillStyle = `rgba(80,52,20,${0.02 + random() * 0.03})`; context.fillRect(x, 0, 3, height); }
  for (let i = 0; i < width * height / 700; i++) {
    context.fillStyle = random() > 0.5 ? "rgba(70,45,15,0.11)" : "rgba(255,240,210,0.10)";
    context.fillRect(random() * width, random() * height, 1 + random() * 3, 1);
  }
  return { canvas, context };
}

function brandMark(context: CanvasRenderingContext2D, x: number, y: number, size: number) {
  context.save(); context.translate(x, y); context.scale(size / 64, size / 64); context.fill(new Path2D(BRAND_PATH)); context.restore();
}

/** ISO 780 "this way up": two arrows over a bar. */
function upArrows(context: CanvasRenderingContext2D, x: number, y: number, size: number) {
  context.save(); context.translate(x, y); context.scale(size / 100, size / 100);
  context.fillRect(8, 90, 84, 9);
  for (const middle of [30, 70]) {
    context.beginPath(); context.moveTo(middle, 2); context.lineTo(middle + 18, 30); context.lineTo(middle + 6, 30); context.lineTo(middle + 6, 80);
    context.lineTo(middle - 6, 80); context.lineTo(middle - 6, 30); context.lineTo(middle - 18, 30); context.closePath(); context.fill();
  }
  context.restore();
}

function printedLong(stencil: string) {
  const { canvas, context } = board(1024, 400, COLOR.kraft, 7);
  context.fillStyle = COLOR.print; context.globalAlpha = 0.86;
  brandMark(context, 52, 44, 96);
  context.font = `66px ${stencil}`; context.fillText("UNIT TOKO", 166, 112);
  context.font = `30px ${stencil}`; context.fillText("AIR MINERAL 600 ML", 168, 160);
  upArrows(context, 56, 236, 104);
  context.font = `26px ${stencil}`; context.fillText("JANGAN DIBANTING", 176, 336);
  context.textAlign = "right"; context.font = `132px ${stencil}`; context.fillText("ISI 24", 972, 300);
  context.font = `36px ${stencil}`; context.fillText("BOTOL", 972, 350);
  return canvas;
}

function printedShort(stencil: string) {
  const { canvas, context } = board(704, 400, COLOR.kraft, 11);
  context.fillStyle = COLOR.print; context.globalAlpha = 0.86; context.textAlign = "center";
  upArrows(context, 302, 34, 100);
  context.font = `72px ${stencil}`; context.fillText("ISI 24 BOTOL", 352, 238);
  brandMark(context, 322, 286, 60);
  return canvas;
}

function bottleWrap() {
  const canvas = document.createElement("canvas"); canvas.width = 256; canvas.height = 128;
  const context = canvas.getContext("2d")!;
  context.fillStyle = COLOR.bottle; context.fillRect(0, 0, 256, 128);
  context.fillStyle = "rgba(255,255,255,0.55)"; context.fillRect(0, 18, 256, 6);
  context.fillStyle = COLOR.print; context.fillRect(0, 52, 256, 26);
  context.strokeStyle = "#DCEFEC"; context.lineWidth = 3; context.beginPath();
  for (let x = 0; x <= 256; x += 4) context.lineTo(x, 65 + Math.sin(x / 256 * Math.PI * 6) * 4);
  context.stroke();
  return canvas;
}

function contactShadow() {
  const canvas = document.createElement("canvas"); canvas.width = canvas.height = 256;
  const context = canvas.getContext("2d")!, gradient = context.createRadialGradient(128, 128, 10, 128, 128, 128);
  gradient.addColorStop(0, `rgba(${COLOR.shadow},0.55)`); gradient.addColorStop(0.6, `rgba(${COLOR.shadow},0.22)`); gradient.addColorStop(1, `rgba(${COLOR.shadow},0)`);
  context.fillStyle = gradient; context.fillRect(0, 0, 256, 256);
  return canvas;
}

async function stencilReady(stencil: string) {
  // The carton print uses the self-hosted stencil face; draw with the fallback rather than wait long for it.
  try { await Promise.race([document.fonts.load(`66px ${stencil}`), new Promise(resolve => setTimeout(resolve, 1500))]); } catch { /* fallback face */ }
}

export async function createKemasanScene(canvas: HTMLCanvasElement, options: KemasanSceneOptions): Promise<KemasanScene> {
  await stencilReady(options.stencil);
  const renderer = new WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: "low-power" });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.outputColorSpace = SRGBColorSpace; renderer.toneMapping = ACESFilmicToneMapping; renderer.toneMappingExposure = 1.05;
  renderer.shadowMap.enabled = true; renderer.shadowMap.type = PCFSoftShadowMap;
  const anisotropy = Math.min(8, renderer.capabilities.getMaxAnisotropy());
  const texture = (source: HTMLCanvasElement) => { const value = new CanvasTexture(source); value.colorSpace = SRGBColorSpace; value.anisotropy = anisotropy; return value; };

  const scene = new Scene();
  const camera = new PerspectiveCamera(26, 1, 0.1, 60);
  const target = new Vector3(0.15, -0.15, 0), base = new Vector3(-4.6, 5.6, 6.0), up = new Vector3(0, 1, 0);
  scene.add(new HemisphereLight("#E3F1EE", "#3B2B1A", 1.1));
  const key = new DirectionalLight("#FFF0DA", 2.4); key.position.set(-3.5, 6.5, 4.5); key.castShadow = true;
  key.shadow.mapSize.set(1024, 1024); key.shadow.bias = -0.0004; key.shadow.normalBias = 0.02;
  Object.assign(key.shadow.camera, { left: -3.2, right: 3.2, top: 3.2, bottom: -3.2, near: 1, far: 20 });
  const rim = new DirectionalLight("#9FD6D2", 0.9); rim.position.set(4.5, 3, -4.5);
  scene.add(key, rim);

  // Materials and geometries are shared by every carton.
  const kraftTexture = texture(board(512, 512, COLOR.kraft, 3).canvas), insideTexture = texture(board(512, 512, COLOR.inside, 5).canvas);
  const longPrint = texture(printedLong(options.stencil)), shortPrint = texture(printedShort(options.stencil));
  const woodTexture = texture(board(512, 128, COLOR.wood, 13).canvas);
  const material = {
    edge: new MeshStandardMaterial({ color: COLOR.kraftEdge, roughness: 0.95 }),
    plain: new MeshStandardMaterial({ map: kraftTexture, roughness: 0.92 }),
    inside: new MeshStandardMaterial({ map: insideTexture, roughness: 0.96 }),
    long: new MeshStandardMaterial({ map: longPrint, roughness: 0.9 }),
    short: new MeshStandardMaterial({ map: shortPrint, roughness: 0.9 }),
    tape: new MeshStandardMaterial({ color: COLOR.tape, roughness: 0.35 }),
    cut: new MeshBasicMaterial({ color: COLOR.cut }),
    wood: new MeshStandardMaterial({ map: woodTexture, roughness: 0.88 }),
    bottle: new MeshStandardMaterial({ map: texture(bottleWrap()), roughness: 0.22, metalness: 0.02 }),
    cap: new MeshStandardMaterial({ color: COLOR.cap, roughness: 0.45 }),
    shadow: new MeshBasicMaterial({ map: texture(contactShadow()), transparent: true, depthWrite: false }),
  };
  const geometry = {
    longWall: new BoxGeometry(W, H, T), shortWall: new BoxGeometry(T, H, D - 2 * T), floor: new BoxGeometry(W - 2 * T, T, D - 2 * T),
    longFlap: new BoxGeometry(W, T, D / 2), shortFlap: new BoxGeometry(D / 2, T, D - 2 * T),
    tape: new BoxGeometry(W + 0.004, 0.004, 0.11), tapeTail: new BoxGeometry(0.004, 0.16, 0.22), cut: new BoxGeometry(W, 0.006, 0.01),
    board: new BoxGeometry(3.6, 0.045, 0.3), block: new BoxGeometry(0.28, 0.13, 0.28), runner: new BoxGeometry(0.3, 0.04, 2.6),
    shadow: new PlaneGeometry(5.6, 4.4),
    bottle: new LatheGeometry([[0, 0], [0.092, 0], [0.1, 0.02], [0.1, 0.34], [0.088, 0.41], [0.052, 0.46], [0.041, 0.49], [0.041, BOTTLE_HEIGHT]]
      .map(([radius, y]) => new Vector2(radius, y)), 20),
    cap: new CylinderGeometry(0.046, 0.046, CAP_HEIGHT, 16),
  };
  const box = (shape: BufferGeometry, faces: Material | Material[], x: number, y: number, z: number) => {
    const mesh = new Mesh(shape, faces); mesh.position.set(x, y, z); mesh.castShadow = mesh.receiveShadow = true; return mesh;
  };

  // Pallet: seven deck boards on nine blocks and three runners; its top is y = 0.
  const pallet = new Group();
  for (let i = 0; i < 7; i++) pallet.add(box(geometry.board, material.wood, 0, -0.0225, -1.15 + i * (2.3 / 6)));
  for (const x of [-1.6, 0, 1.6]) {
    for (const z of [-1.15, 0, 1.15]) pallet.add(box(geometry.block, material.wood, x, -0.11, z));
    pallet.add(box(geometry.runner, material.wood, x, -0.195, 0));
  }
  const shadow = new Mesh(geometry.shadow, material.shadow); shadow.rotation.x = -Math.PI / 2; shadow.position.y = -0.22;
  scene.add(pallet, shadow);

  // Box faces run +x, -x, +y, -y, +z, -z: print outside, liner inside, plain edges.
  const e = material.edge;
  function buildCarton(slot: number, enteredAt: number): Carton {
    const group = new Group();
    group.add(box(geometry.longWall, [e, e, e, e, material.long, material.inside], 0, H / 2, D / 2 - T / 2));
    group.add(box(geometry.longWall, [e, e, e, e, material.inside, material.long], 0, H / 2, -D / 2 + T / 2));
    group.add(box(geometry.shortWall, [material.inside, material.short, e, e, e, e], -W / 2 + T / 2, H / 2, 0));
    group.add(box(geometry.shortWall, [material.short, material.inside, e, e, e, e], W / 2 - T / 2, H / 2, 0));
    group.add(box(geometry.floor, material.inside, 0, T / 2, 0));
    const flapFaces = [e, e, material.plain, material.inside, e, e];
    const pivot = (x: number, y: number, z: number, flap: Mesh, tape?: Mesh) => {
      const hinge = new Group(); hinge.position.set(x, y, z); hinge.add(flap); if (tape) hinge.add(tape); group.add(hinge); return hinge;
    };
    // Long flaps sit on the short ones, so they hinge one board higher. Each long flap carries its half of the tape.
    const front = pivot(0, H + T, D / 2 - T / 2, box(geometry.longFlap, flapFaces, 0, T / 2, -D / 4), box(geometry.tape, material.tape, 0, T + 0.002, -D / 2 + 0.055));
    const back = pivot(0, H + T, -D / 2 + T / 2, box(geometry.longFlap, flapFaces, 0, T / 2, D / 4), box(geometry.tape, material.tape, 0, T + 0.002, D / 2 - 0.055));
    const left = pivot(-W / 2 + T / 2, H, 0, box(geometry.shortFlap, flapFaces, D / 4, T / 2, 0));
    const right = pivot(W / 2 - T / 2, H, 0, box(geometry.shortFlap, flapFaces, -D / 4, T / 2, 0));
    for (const side of [-1, 1]) group.add(box(geometry.tapeTail, material.tape, side * (W / 2 + 0.002), H - 0.08, 0));
    const cut = new Mesh(geometry.cut, material.cut); cut.position.y = H + 2 * T + 0.005; cut.visible = false; group.add(cut);
    const position = cartonSlots(slot + 1)[slot]; group.position.set(position.x, 0, position.z);
    scene.add(group);
    return { group, slot, front, back, left, right, cut, enteredAt, leavingAt: null };
  }

  // The 24 bottles belong to the front carton; two instanced meshes draw all bodies and all caps.
  const spots = bottleSpots();
  const bottles = new InstancedMesh(geometry.bottle, material.bottle, spots.length), caps = new InstancedMesh(geometry.cap, material.cap, spots.length);
  bottles.castShadow = caps.castShadow = true; bottles.receiveShadow = true;
  const placed = new Matrix4(), turn = new Quaternion(), one = new Vector3(1, 1, 1), none = new Vector3(0, 0, 0), at = new Vector3();

  function applyOpening(carton: Carton, frame: OpeningFrame) {
    carton.front.rotation.x = frame.longFlap; carton.back.rotation.x = -frame.longFlap;
    carton.left.rotation.z = frame.shortFlap; carton.right.rotation.z = -frame.shortFlap;
    carton.cut.visible = frame.tape > 0 && frame.longFlap < 0.02;
    carton.cut.scale.x = Math.max(0.001, frame.tape); carton.cut.position.x = -W / 2 + W * frame.tape / 2;
    frame.items.forEach((offset, index) => {
      const scale = offset <= -0.499 ? none : one;
      bottles.setMatrixAt(index, placed.compose(at.set(spots[index].x, T + offset, spots[index].z), turn, scale));
      caps.setMatrixAt(index, placed.compose(at.set(spots[index].x, T + BOTTLE_HEIGHT + CAP_HEIGHT / 2 + offset, spots[index].z), turn, scale));
    });
    bottles.instanceMatrix.needsUpdate = caps.instanceMatrix.needsUpdate = true;
  }

  let clock = 0, openingStart: number | null = options.mode === "still" ? -OPENING_MS : null;
  const cartons: Carton[] = [];
  const front = buildCarton(0, -Infinity); front.group.add(bottles, caps); cartons.push(front);
  applyOpening(front, options.mode === "still" ? restingFrame() : openingFrame(0));

  // Cartons on the pallet at load stand still; later changes drop in or lift off (instantly in still mode).
  function setCartons(count: number, immediate = options.mode === "still") {
    const at = immediate ? -Infinity : clock;
    const wanted = cartonSlots(count).length, present = cartons.filter(carton => carton.leavingAt === null);
    for (let slot = present.length; slot < wanted; slot++) {
      const leaving = cartons.find(carton => carton.slot === slot);
      if (leaving) { leaving.leavingAt = null; leaving.enteredAt = at; continue; }
      cartons.push(buildCarton(slot, at));
      applyOpening(cartons[cartons.length - 1], openingFrame(0));
    }
    for (const carton of present) if (carton.slot >= wanted && carton.slot > 0) carton.leavingAt = at;
    invalidate();
  }

  let yaw = 0, targetYaw = 0, aspect = 1;
  function update(): boolean {
    let busy = false;
    if (openingStart === null) openingStart = clock;
    const elapsed = clock - openingStart;
    applyOpening(front, elapsed >= OPENING_MS ? restingFrame() : openingFrame(elapsed)); busy ||= elapsed < OPENING_MS;
    for (const carton of [...cartons]) {
      if (carton.leavingAt !== null) {
        const lifted = clock - carton.leavingAt;
        if (lifted >= LEAVE_MS) { scene.remove(carton.group); cartons.splice(cartons.indexOf(carton), 1); continue; }
        carton.group.position.y = leaveOffset(lifted); busy = true;
      } else { carton.group.position.y = entryOffset(clock - carton.enteredAt); busy ||= carton.group.position.y > 0; }
    }
    yaw += (targetYaw - yaw) * 0.08; busy ||= Math.abs(targetYaw - yaw) > 0.0005;
    // Narrow stages pull the camera back so the whole pallet stays in frame.
    const distance = Math.min(1.9, Math.max(1, 1.05 / aspect));
    camera.position.copy(base).sub(target).multiplyScalar(distance).applyAxisAngle(up, yaw).add(target);
    camera.lookAt(target);
    return busy;
  }

  let frameRequest = 0, last = 0, onScreen = true, pageVisible = document.visibilityState !== "hidden", alive = true;
  function frame(now: number) {
    frameRequest = 0; clock += last ? Math.min(now - last, 50) : 16; last = now;
    const busy = update(); renderer.render(scene, camera);
    if (busy && onScreen && pageVisible) frameRequest = requestAnimationFrame(frame); else last = 0;
  }
  function invalidate() { if (alive && !frameRequest && onScreen && pageVisible) frameRequest = requestAnimationFrame(frame); }
  function pause() { cancelAnimationFrame(frameRequest); frameRequest = 0; last = 0; }

  function resize() {
    const host = canvas.parentElement; if (!host) return;
    const width = Math.max(1, host.clientWidth), height = Math.max(1, host.clientHeight);
    renderer.setSize(width, height, false); aspect = width / height; camera.aspect = aspect; camera.updateProjectionMatrix(); invalidate();
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
    dispose() {
      alive = false; pause(); sized.disconnect(); seen.disconnect();
      document.removeEventListener("visibilitychange", visibility); window.removeEventListener("pointermove", pointer);
      canvas.removeEventListener("webglcontextlost", lost);
      const textures = new Set<Texture>();
      for (const value of Object.values(material)) {
        for (const slot of ["map"] as const) { const map = (value as MeshStandardMaterial)[slot]; if (map) textures.add(map); }
        value.dispose();
      }
      textures.forEach(map => map.dispose());
      Object.values(geometry).forEach(shape => shape.dispose());
      bottles.dispose(); caps.dispose();
      renderer.dispose(); renderer.forceContextLoss();
    },
  };
}
