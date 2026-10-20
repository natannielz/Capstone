import {
  type BufferGeometry, CanvasTexture, Color, type Material, MeshBasicMaterial, MeshPhysicalMaterial, MeshStandardMaterial, RepeatWrapping, SRGBColorSpace,
  SpriteMaterial, type Texture, Vector2, type WebGLRenderer,
} from "three";
import {
  INK, KRAFT, ORANGE, PAPER, STAGE, boxInside, kraft, boxPrint, branchSign, checkBadge, coffeeArt, fluteEdge, invoiceSheet, normalFromHeight, paperArt, pensArt,
  receiptSlip, routeLine, sanitizerLabel, shippingLabel, studioFloor, tape, teaArt, type Fonts, type PackageArt,
} from "./textures";

/** Every material, texture and geometry the scene shares, created once and disposed together. */
export function createKit(renderer: WebGLRenderer, fonts: Fonts, cinematic: boolean) {
  const anisotropy = Math.min(12, renderer.capabilities.getMaxAnisotropy());
  const textures: Texture[] = [], geometries: BufferGeometry[] = [];
  const texture = (source: HTMLCanvasElement, color = true) => {
    const value = new CanvasTexture(source); if (color) value.colorSpace = SRGBColorSpace; value.anisotropy = anisotropy; textures.push(value); return value;
  };
  // Corrugated board: printed liner, fibre normals and a faint sheen from the paper surface.
  const board = (sheet: { color: HTMLCanvasElement; height: HTMLCanvasElement }, roughness: number) => new MeshPhysicalMaterial({
    map: texture(sheet.color), normalMap: texture(normalFromHeight(sheet.height, 2.2), false), normalScale: new Vector2(0.55, 0.55),
    roughness, sheen: 0.35, sheenRoughness: 0.8, sheenColor: new Color("#F2DDB8"),
  });
  const printed = (art: PackageArt) => {
    // A coated carton: the art on the front and back, plain colour elsewhere, a light satin sheen.
    const front = new MeshPhysicalMaterial({ map: texture(art.front), roughness: 0.42, clearcoat: 0.35, clearcoatRoughness: 0.35 });
    const side = new MeshPhysicalMaterial({ color: art.side, roughness: 0.5, clearcoat: 0.25, clearcoatRoughness: 0.4 });
    const top = new MeshPhysicalMaterial({ color: art.top, roughness: 0.5, clearcoat: 0.25, clearcoatRoughness: 0.4 });
    // BoxGeometry face order: +x, -x, +y, -y, +z (front), -z (back).
    return [side, side, top, top, front, front];
  };
  const floorMap = texture(studioFloor()); floorMap.wrapS = floorMap.wrapT = RepeatWrapping; floorMap.repeat.set(6, 6);
  const routeMap = texture(routeLine()); routeMap.wrapS = RepeatWrapping;
  const labelMap = texture(shippingLabel(fonts, { number: "PT-00000142", from: "Kantor Cabang A", to: "Unit Toko Pusat", summary: "5 KEMASAN" }));
  const receiptMap = texture(receiptSlip(fonts, false)), stampedMap = texture(receiptSlip(fonts, true));

  const material = {
    long: board(boxPrint(fonts, "long", 3), 0.9),
    short: board(boxPrint(fonts, "short", 5), 0.9),
    inside: board(boxInside(9), 0.95),
    plain: board(kraft(1024, 1024, KRAFT, 11), 0.9),
    edge: new MeshStandardMaterial({ map: texture(fluteEdge()), roughness: 0.96 }),
    tape: new MeshPhysicalMaterial({ map: texture(tape(fonts)), roughness: 0.2, clearcoat: 1, clearcoatRoughness: 0.12, transparent: true, opacity: 0.9 }),
    label: new MeshStandardMaterial({ map: labelMap, roughness: 0.72 }),
    coffee: printed(coffeeArt(fonts)), tea: printed(teaArt(fonts)), paper: printed(paperArt(fonts)), pens: printed(pensArt(fonts)),
    // Clear PET with a faintly blue gel on the cinematic tier; a satin opaque bottle on the balanced one.
    bottle: cinematic
      ? new MeshPhysicalMaterial({ color: "#F6FBFC", transmission: 1, thickness: 0.18, roughness: 0.04, ior: 1.46, clearcoat: 1, clearcoatRoughness: 0.03,
        attenuationColor: "#9FD3DE", attenuationDistance: 0.5, specularIntensity: 1 })
      : new MeshPhysicalMaterial({ color: "#CFE7EC", roughness: 0.18, clearcoat: 1, clearcoatRoughness: 0.08 }),
    bottleLabel: new MeshStandardMaterial({ map: texture(sanitizerLabel(fonts)), roughness: 0.55 }),
    pump: new MeshPhysicalMaterial({ color: "#F4F6F6", roughness: 0.35, clearcoat: 0.6 }),
    floor: new MeshStandardMaterial({ map: floorMap, roughness: 0.88, metalness: 0 }),
    backdrop: new MeshStandardMaterial({ color: STAGE, roughness: 1 }),
    route: new MeshBasicMaterial({ map: routeMap, transparent: true, depthWrite: false }),
    metal: new MeshStandardMaterial({ color: "#2B353A", metalness: 1, roughness: 0.32 }),
    rubber: new MeshStandardMaterial({ color: "#16191B", roughness: 0.85 }),
    hub: new MeshStandardMaterial({ color: ORANGE, roughness: 0.45, metalness: 0.1 }),
    oak: new MeshStandardMaterial({ color: "#C9A57A", roughness: 0.55 }),
    panel: new MeshStandardMaterial({ color: INK, roughness: 0.6 }),
    wall: new MeshStandardMaterial({ color: "#EEF2F0", roughness: 0.9 }),
    sign: new MeshStandardMaterial({ map: texture(branchSign(fonts)), roughness: 0.6 }),
    paperSheet: new MeshStandardMaterial({ color: PAPER, roughness: 0.75 }),
    receipt: new MeshStandardMaterial({ map: receiptMap, roughness: 0.75 }),
    receiptStamped: new MeshStandardMaterial({ map: stampedMap, roughness: 0.75 }),
    invoice: new MeshStandardMaterial({ map: texture(invoiceSheet(fonts)), roughness: 0.75 }),
    stampWood: new MeshStandardMaterial({ color: "#8A5A34", roughness: 0.5 }),
    stampFoot: new MeshStandardMaterial({ color: ORANGE, roughness: 0.7 }),
    check: new SpriteMaterial({ map: texture(checkBadge()), depthTest: false, transparent: true }),
  };

  return {
    material, texture,
    /** Registers a geometry built by a scene module so the kit disposes it with everything else. */
    track<T extends BufferGeometry>(geometry: T): T { geometries.push(geometry); return geometry; },
    dispose() {
      textures.forEach(value => value.dispose()); geometries.forEach(value => value.dispose());
      for (const value of Object.values(material)) (Array.isArray(value) ? value : [value]).forEach((entry: Material) => entry.dispose());
    },
  };
}
export type Kit = ReturnType<typeof createKit>;
