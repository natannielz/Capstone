import {
  AdditiveBlending, BoxGeometry, BufferAttribute, BufferGeometry, CanvasTexture, Color, CylinderGeometry, DoubleSide, Group, Mesh, MeshBasicMaterial,
  MeshStandardMaterial, type Object3D, PlaneGeometry, Points, PointsMaterial, SRGBColorSpace, SphereGeometry, Vector3,
} from "three";
import { COUNTER } from "../journey";
import type { Kit } from "./kit";

const shaded = <T extends Mesh>(mesh: T, cast = true) => { mesh.castShadow = cast; mesh.receiveShadow = true; return mesh; };

/** Deterministic pseudo-random sequence, so the set is dressed the same way on every visit. */
function seeded(seed: number) { let value = seed >>> 0; return () => ((value = (value * 1664525 + 1013904223) >>> 0) / 4294967296); }

/** A soft vertical gradient for a light shaft: brightest near the window, fading to nothing at the floor and the edges. */
function shaftTexture() {
  const canvas = document.createElement("canvas"); canvas.width = 64; canvas.height = 256;
  const context = canvas.getContext("2d")!;
  const along = context.createLinearGradient(0, 0, 0, 256); along.addColorStop(0, "rgba(255,240,214,0.95)"); along.addColorStop(0.55, "rgba(255,236,206,0.35)"); along.addColorStop(1, "rgba(255,236,206,0)");
  context.fillStyle = along; context.fillRect(0, 0, 64, 256);
  context.globalCompositeOperation = "destination-in";
  const across = context.createLinearGradient(0, 0, 64, 0); across.addColorStop(0, "rgba(0,0,0,0)"); across.addColorStop(0.5, "rgba(0,0,0,1)"); across.addColorStop(1, "rgba(0,0,0,0)");
  context.fillStyle = across; context.fillRect(0, 0, 64, 256);
  const texture = new CanvasTexture(canvas); texture.colorSpace = SRGBColorSpace; return texture;
}

/** The window's light on the floor: a soft-edged pool crossed by the shadows of the mullions. */
function poolTexture() {
  const canvas = document.createElement("canvas"); canvas.width = 256; canvas.height = 384;
  const context = canvas.getContext("2d")!;
  context.filter = "blur(10px)"; context.fillStyle = "rgba(255,236,204,0.9)"; context.fillRect(28, 28, 200, 328);
  context.filter = "blur(4px)"; context.fillStyle = "rgba(0,0,0,1)"; context.globalCompositeOperation = "destination-out";
  context.fillRect(122, 20, 12, 344); context.fillRect(20, 128, 216, 10); context.fillRect(20, 232, 216, 10);
  const texture = new CanvasTexture(canvas); texture.colorSpace = SRGBColorSpace; return texture;
}

/** What a window shows: a bright sky over a band of greenery, warm for the morning shop, cooler for the office. */
function viewTexture(warm: boolean) {
  const canvas = document.createElement("canvas"); canvas.width = 256; canvas.height = 320;
  const context = canvas.getContext("2d")!, random = seeded(warm ? 3 : 9);
  const sky = context.createLinearGradient(0, 0, 0, 320);
  sky.addColorStop(0, warm ? "#FFF1D6" : "#D9EAF4"); sky.addColorStop(0.65, warm ? "#FFE6BE" : "#EEF6FA"); sky.addColorStop(1, warm ? "#F6E2C0" : "#F6FAFB");
  context.fillStyle = sky; context.fillRect(0, 0, 256, 320);
  if (warm) { const sun = context.createRadialGradient(70, 60, 4, 70, 60, 120); sun.addColorStop(0, "rgba(255,255,240,0.95)"); sun.addColorStop(1, "rgba(255,250,230,0)"); context.fillStyle = sun; context.fillRect(0, 0, 256, 320); }
  context.filter = "blur(6px)";
  for (let index = 0; index < 18; index++) {
    context.fillStyle = index % 3 ? (warm ? "#9BB08A" : "#7FA585") : (warm ? "#86A07A" : "#5E8C68");
    context.beginPath(); context.arc(random() * 256, 230 + random() * 40, 26 + random() * 30, 0, Math.PI * 2); context.fill();
  }
  context.fillStyle = warm ? "#B9A88A" : "#9CAFA9"; context.fillRect(0, 290, 256, 30);
  const texture = new CanvasTexture(canvas); texture.colorSpace = SRGBColorSpace; return texture;
}

function moteTexture() {
  const canvas = document.createElement("canvas"); canvas.width = canvas.height = 32;
  const context = canvas.getContext("2d")!, glow = context.createRadialGradient(16, 16, 0, 16, 16, 16);
  glow.addColorStop(0, "rgba(255,248,232,1)"); glow.addColorStop(0.4, "rgba(255,244,224,0.5)"); glow.addColorStop(1, "rgba(255,244,224,0)");
  context.fillStyle = glow; context.fillRect(0, 0, 32, 32);
  const texture = new CanvasTexture(canvas); texture.colorSpace = SRGBColorSpace; return texture;
}

/**
 * The shop's packing corner behind the box: steel shelving stocked with the catalog's cartons and shipping boxes, a tall
 * window high on the left whose morning light falls across the box in soft shafts, and dust drifting in that light.
 * Everything sits well behind the subject, so the shallow focus turns it into an out-of-focus, believable room.
 */
export function buildShopSet(kit: Kit) {
  const m = kit.material, group = new Group(), stock = new Group(), random = seeded(17), owned: { dispose(): void }[] = [];
  group.add(stock);
  const steel = new MeshStandardMaterial({ color: "#6F7C7C", metalness: 0.6, roughness: 0.45 });
  const board = new MeshStandardMaterial({ color: "#B9A27E", roughness: 0.8 });
  owned.push(steel, board);
  // Stock on the shelves is a step darker than the packages in the request, so the request always reads in front.
  const dim = <T extends { clone(): T; color: Color }>(material: T) => { const copy = material.clone(); copy.color.multiplyScalar(0.78); owned.push(copy as unknown as { dispose(): void }); return copy; };
  const printed = (set: typeof m.coffee) => set.map(material => dim(material as MeshStandardMaterial)) as unknown as typeof m.coffee;
  const cartons = [printed(m.coffee), printed(m.tea), printed(m.paper), printed(m.pens), m.plain, m.plain, m.plain];
  // Two bays of shelving along the back of the packing corner.
  for (const [x0, x1] of [[-3.3, -0.1], [0.3, 3.5]]) {
    const width = x1 - x0, depth = 0.62, z = -5.3;
    for (const x of [x0, x1]) for (const dz of [-depth / 2, depth / 2]) {
      const upright = shaded(new Mesh(kit.track(new BoxGeometry(0.05, 2.5, 0.05)), steel)); upright.position.set(x, 1.25, z + dz); stock.add(upright);
    }
    for (const y of [0.14, 0.86, 1.58, 2.3]) {
      const shelf = shaded(new Mesh(kit.track(new BoxGeometry(width, 0.035, depth)), board)); shelf.position.set((x0 + x1) / 2, y, z); stock.add(shelf);
      // Stock on each shelf: printed cartons and plain shipping boxes of catalog sizes, packed with small gaps.
      let x = x0 + 0.08;
      while (y < 2.2) {
        const kind = Math.floor(random() * cartons.length), plain = cartons[kind] === m.plain;
        const w = plain ? 0.38 + random() * 0.22 : 0.18 + random() * 0.24, h = plain ? 0.3 + random() * 0.24 : 0.16 + random() * 0.3, d = 0.3 + random() * 0.22;
        if (x + w > x1 - 0.06) break;
        const carton = shaded(new Mesh(kit.track(new BoxGeometry(w, Math.min(h, 0.62), d)), cartons[kind]));
        carton.position.set(x + w / 2, y + 0.018 + Math.min(h, 0.62) / 2, z + (random() - 0.5) * 0.08); carton.rotation.y = (random() - 0.5) * 0.08;
        stock.add(carton); x += w + 0.03 + random() * 0.05;
      }
    }
  }
  // A few shipping boxes waiting on the floor beside the shelves.
  for (const [x, z, w, h, d, ry] of [[-4.3, -4.9, 0.7, 0.5, 0.55, 0.18], [-4.1, -4.85, 0.5, 0.36, 0.42, -0.1], [-5.2, -4.6, 0.62, 0.44, 0.5, 0.32]] as const) {
    const stack = shaded(new Mesh(kit.track(new BoxGeometry(w, h, d)), m.plain));
    stack.position.set(x, h / 2 + (x === -4.1 ? 0.5 : 0), z); stack.rotation.y = ry; stock.add(stack);
  }
  // The window: a bright pane in a teal frame, high on the back wall to the left.
  const view = viewTexture(true);
  const frame = new MeshStandardMaterial({ color: "#0E4448", roughness: 0.6 }), pane = new MeshBasicMaterial({ map: view, color: new Color("#FFFFFF").multiplyScalar(1.45) });
  owned.push(view, frame, pane);
  // A tall window left of the shelving, so its light comes from the same side as the key light.
  const window = new Group(); window.position.set(-5.4, 2.05, -5.94);
  const glass = new Mesh(kit.track(new PlaneGeometry(1.6, 2.3)), pane); window.add(glass);
  for (const [w, h, x, y] of [[1.76, 0.08, 0, 1.19], [1.76, 0.08, 0, -1.19], [0.08, 2.46, 0.84, 0], [0.08, 2.46, -0.84, 0], [0.05, 2.3, 0, 0], [1.6, 0.05, 0, 0.35], [1.6, 0.05, 0, -0.45]] as const) {
    const bar = new Mesh(kit.track(new BoxGeometry(w, h, 0.06)), frame); bar.position.set(x, y, 0.02); window.add(bar);
  }
  const wall = shaded(new Mesh(kit.track(new PlaneGeometry(18, 6)), m.wall), false); wall.position.set(-1, 3, -6);
  // Atmosphere: a faint warm haze between the box and the shelving gives the room depth and keeps the stock behind.
  const hazeMaterial = new MeshBasicMaterial({ color: "#EDE7DC", transparent: true, opacity: 0.34, depthWrite: false, fog: false });
  owned.push(hazeMaterial);
  const haze = new Mesh(kit.track(new PlaneGeometry(22, 7)), hazeMaterial); haze.position.set(0, 3.2, -4.55); stock.add(haze);
  group.add(wall, window);

  // Light shafts from the window towards the box, as crossed additive ribbons (no geometry casts or receives).
  const shaftMap = shaftTexture(); owned.push(shaftMap);
  const shaftMaterial = new MeshBasicMaterial({ map: shaftMap, transparent: true, opacity: 0.22, blending: AdditiveBlending, depthWrite: false, side: DoubleSide, fog: true });
  owned.push(shaftMaterial);
  const from = new Vector3(-5.4, 2.6, -5.85), to = new Vector3(0.3, 0, 0.4), direction = to.clone().sub(from), length = direction.length();
  const beams = new Group(); beams.position.copy(from).add(to).multiplyScalar(0.5);
  beams.quaternion.setFromUnitVectors(new Vector3(0, 1, 0), direction.clone().normalize().negate());
  for (const [offset, width, turn] of [[0, 1.5, 0], [0.35, 0.9, Math.PI / 2], [-0.5, 0.7, Math.PI / 3], [0.7, 0.55, -Math.PI / 4]] as const) {
    const ribbon = new Mesh(kit.track(new PlaneGeometry(width, length)), shaftMaterial);
    ribbon.rotation.y = turn; ribbon.position.x = offset; beams.add(ribbon);
  }
  group.add(beams);
  const poolMap = poolTexture(); owned.push(poolMap);
  const poolMaterial = new MeshBasicMaterial({ map: poolMap, transparent: true, opacity: 0.45, blending: AdditiveBlending, depthWrite: false, fog: true });
  owned.push(poolMaterial);
  // Stretched along the light's direction and laid just above the floor, beside and under the box.
  const pool = new Mesh(kit.track(new PlaneGeometry(1.7, 3.1)), poolMaterial);
  pool.rotation.set(-Math.PI / 2, 0, Math.atan2(direction.x, direction.z)); pool.position.set(-0.55, 0.004, -0.35); pool.renderOrder = 2;
  group.add(pool);

  // Dust motes inside the light, drifting slowly; positions are a pure function of time so every frame is reproducible.
  const count = 220, base = new Float32Array(count * 3), positions = new Float32Array(count * 3), phase = new Float32Array(count);
  for (let index = 0; index < count; index++) {
    const t = random(), spread = 0.15 + t * 0.7;
    base[index * 3] = from.x + direction.x * t + (random() - 0.5) * spread * 2;
    base[index * 3 + 1] = Math.max(0.1, from.y + direction.y * t + (random() - 0.5) * spread);
    base[index * 3 + 2] = from.z + direction.z * t + (random() - 0.5) * spread * 2;
    phase[index] = random() * Math.PI * 2;
  }
  positions.set(base);
  const dustGeometry = kit.track(new BufferGeometry()); dustGeometry.setAttribute("position", new BufferAttribute(positions, 3));
  const moteMap = moteTexture(); owned.push(moteMap);
  const dustMaterial = new PointsMaterial({ map: moteMap, size: 0.035, sizeAttenuation: true, transparent: true, opacity: 0.55, blending: AdditiveBlending, depthWrite: false });
  owned.push(dustMaterial);
  const dust = new Points(dustGeometry, dustMaterial); group.add(dust);

  return {
    group,
    /** The set never takes part in the contact shadows under the box. */
    excluded: [group] as Object3D[],
    /** Shelving and stock, hidden once the story has left the shop (the room's wall and window stay). */
    stock,
    /** `light` 0..1 fades the window light (full in the shop, gone on the road); `air` 0..1 shows the shafts and dust,
     * which only read from the low hero angles; `time` in seconds moves the dust. */
    update(light: number, air: number, time: number) {
      poolMaterial.opacity = 0.45 * light; pool.visible = light > 0.01;
      shaftMaterial.opacity = 0.22 * light * air; dustMaterial.opacity = 0.55 * light * air; beams.visible = dust.visible = light * air > 0.01;
      if (!dust.visible) return;
      for (let index = 0; index < count; index++) {
        const p = phase[index], i = index * 3;
        positions[i] = base[i] + Math.sin(time * 0.11 + p) * 0.12; positions[i + 1] = base[i + 1] + Math.sin(time * 0.07 + p * 1.7) * 0.18;
        positions[i + 2] = base[i + 2] + Math.cos(time * 0.09 + p * 0.6) * 0.12;
      }
      dustGeometry.attributes.position.needsUpdate = true;
    },
    dispose() { owned.forEach(item => item.dispose()); },
  };
}

/** The branch office around the counter: a bright window, a potted plant and a wall clock, so the counter stands in a room. */
export function buildBranchDressing(kit: Kit) {
  const group = new Group(), owned: { dispose(): void }[] = [];
  const clay = new MeshStandardMaterial({ color: "#B4643E", roughness: 0.85 }), leaf = new MeshStandardMaterial({ color: "#3F7A52", roughness: 0.55 });
  const view = viewTexture(false); owned.push(view);
  const frame = new MeshStandardMaterial({ color: "#0E4448", roughness: 0.6 }), pane = new MeshBasicMaterial({ map: view, color: new Color("#FFFFFF").multiplyScalar(1.3) });
  const face = new MeshStandardMaterial({ color: "#FBFBF8", roughness: 0.5 }), hand = new MeshStandardMaterial({ color: "#10343A", roughness: 0.5 });
  owned.push(clay, leaf, frame, pane, face, hand);
  const plant = new Group(); plant.position.set(COUNTER.x + 2.45, 0, COUNTER.z - 0.55);
  const pot = shaded(new Mesh(kit.track(new CylinderGeometry(0.24, 0.18, 0.5, 24)), clay)); pot.position.y = 0.25; plant.add(pot);
  const random = seeded(5);
  for (let index = 0; index < 14; index++) {
    const blade = shaded(new Mesh(kit.track(new SphereGeometry(0.1, 12, 8)), leaf));
    const angle = index / 14 * Math.PI * 2 + random() * 0.4, lean = 0.25 + random() * 0.45, height = 0.55 + random() * 0.55;
    blade.scale.set(0.55, height * 3.2, 0.25); blade.position.set(Math.cos(angle) * lean * 0.35, 0.5 + height * 0.45, Math.sin(angle) * lean * 0.35);
    blade.rotation.set(Math.sin(angle) * lean, 0, -Math.cos(angle) * lean); plant.add(blade);
  }
  const window = new Group(); window.position.set(COUNTER.x - 1.62, 1.5, COUNTER.z - 1.4);
  window.add(new Mesh(kit.track(new PlaneGeometry(1.1, 0.9)), pane));
  for (const [w, h, x, y] of [[1.22, 0.06, 0, 0.48], [1.22, 0.06, 0, -0.48], [0.06, 1.02, 0.58, 0], [0.06, 1.02, -0.58, 0], [0.04, 0.9, 0, 0]] as const) {
    const bar = new Mesh(kit.track(new BoxGeometry(w, h, 0.05)), frame); bar.position.set(x, y, 0.03); window.add(bar);
  }
  const clock = new Group(); clock.position.set(COUNTER.x + 1.55, 1.75, COUNTER.z - 1.38);
  clock.add(new Mesh(kit.track(new CylinderGeometry(0.17, 0.17, 0.03, 32).rotateX(Math.PI / 2)), face));
  const rim = new Mesh(kit.track(new CylinderGeometry(0.18, 0.18, 0.025, 32, 1, true).rotateX(Math.PI / 2)), frame); clock.add(rim);
  for (const [length, angle] of [[0.09, -0.6], [0.13, 2.1]] as const) {
    const arm = new Mesh(kit.track(new BoxGeometry(0.012, length, 0.01)), hand); arm.position.set(Math.sin(angle) * length / 2, Math.cos(angle) * length / 2, 0.02); arm.rotation.z = -angle; clock.add(arm);
  }
  group.add(plant, window, clock);
  return { group, dispose() { owned.forEach(item => item.dispose()); } };
}
