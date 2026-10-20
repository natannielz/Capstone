import {
  BoxGeometry, BufferGeometry, CanvasTexture, DoubleSide, CylinderGeometry, LatheGeometry, Material, MeshBasicMaterial, MeshPhysicalMaterial,
  MeshStandardMaterial, PlaneGeometry, RepeatWrapping, SRGBColorSpace, SpriteMaterial, Texture, Vector2, type WebGLRenderer,
} from "three";
import { CARTON } from "../kemasan-timeline";
import {
  INK, KRAFT, KRAFT_INSIDE, KRAFT_TEXT, MARKER, board, bayMarking, bottleWrap, branchSign, checkBadge, contactShadow, corrugationBump,
  fluteEdge, invoiceSheet, pavement, printedLong, printedShort, road, shippingLabel, vanSide, woodGrain, type Fonts, type LabelText,
} from "./textures";

export const W = CARTON.width, D = CARTON.depth, H = CARTON.height, T = CARTON.wall;
export const BOTTLE_HEIGHT = 0.53, CAP_HEIGHT = 0.06, LABEL_SIZE: [number, number] = [0.62, 0.4];

/** Every material, texture and geometry the scene shares, created once and disposed together. */
export function createKit(renderer: WebGLRenderer, fonts: Fonts) {
  const anisotropy = Math.min(8, renderer.capabilities.getMaxAnisotropy());
  const textures: Texture[] = [];
  const texture = (source: HTMLCanvasElement, color = true) => {
    const value = new CanvasTexture(source); if (color) value.colorSpace = SRGBColorSpace; value.anisotropy = anisotropy; textures.push(value); return value;
  };
  const bump = texture(corrugationBump(), false); bump.wrapS = bump.wrapT = RepeatWrapping; bump.repeat.set(3, 1);
  const kraft = (map: Texture, roughness = 0.9) => new MeshStandardMaterial({ map, roughness, bumpMap: bump, bumpScale: 0.35 });
  const labelText: LabelText = { number: "PT-00000142", from: "Kantor Cabang Uji Selatan", to: "Unit Toko Pusat", summary: "2 KARTON  48 BOTOL" };
  const labelMap = texture(shippingLabel(fonts, labelText, false)), stampedMap = texture(shippingLabel(fonts, labelText, true));
  const invoiceMap = texture(invoiceSheet(fonts, "PT-00000142  2 karton"));

  const material = {
    edge: new MeshStandardMaterial({ map: texture(fluteEdge()), roughness: 0.95 }),
    plain: kraft(texture(board(512, 512, KRAFT, 3).canvas)),
    inside: kraft(texture(board(512, 512, KRAFT_INSIDE, 5).canvas), 0.96),
    long: kraft(texture(printedLong(fonts))),
    short: kraft(texture(printedShort(fonts))),
    tape: new MeshPhysicalMaterial({ color: "#D9C28B", roughness: 0.28, clearcoat: 0.6, clearcoatRoughness: 0.2 }),
    cut: new MeshBasicMaterial({ color: "#3B2A17" }),
    wood: new MeshStandardMaterial({ map: texture(woodGrain(13)), roughness: 0.85 }),
    bottle: new MeshPhysicalMaterial({ map: texture(bottleWrap()), roughness: 0.12, clearcoat: 1, clearcoatRoughness: 0.05 }),
    cap: new MeshPhysicalMaterial({ color: MARKER, roughness: 0.35, clearcoat: 0.4 }),
    shadow: new MeshBasicMaterial({ map: texture(contactShadow()), transparent: true, depthWrite: false }),
    label: new MeshStandardMaterial({ map: labelMap, roughness: 0.6, polygonOffset: true, polygonOffsetFactor: -2 }),
    labelStamped: new MeshStandardMaterial({ map: stampedMap, roughness: 0.6, transparent: true, opacity: 0, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -4 }),
    floorPaint: new MeshBasicMaterial({ map: texture(bayMarking(fonts)), transparent: true, depthWrite: false, toneMapped: false }),
    road: new MeshBasicMaterial({ map: texture(road()), transparent: true, depthWrite: false, toneMapped: false }),
    pavement: new MeshBasicMaterial({ map: texture(pavement()), transparent: true, depthWrite: false, toneMapped: false }),
    wall: new MeshStandardMaterial({ color: "#E9EFEA", roughness: 0.75 }),
    trim: new MeshStandardMaterial({ color: "#0B3D42", roughness: 0.6 }),
    glass: new MeshPhysicalMaterial({ color: "#14383D", roughness: 0.06, metalness: 0.3, clearcoat: 1 }),
    awning: new MeshStandardMaterial({ color: KRAFT_TEXT, roughness: 0.7 }),
    sign: new MeshStandardMaterial({ map: texture(branchSign(fonts)), roughness: 0.5 }),
    leaf: new MeshStandardMaterial({ color: "#2F6E62", roughness: 0.85 }),
    vanBody: new MeshStandardMaterial({ color: "#F4F6F2", roughness: 0.45 }),
    cargoInside: new MeshStandardMaterial({ color: "#1B2A2C", roughness: 0.9 }),
    vanSide: new MeshStandardMaterial({ map: texture(vanSide(fonts)), roughness: 0.45 }),
    vanCab: new MeshPhysicalMaterial({ color: "#1B6A72", roughness: 0.35, clearcoat: 0.8 }),
    rubber: new MeshStandardMaterial({ color: "#1C2729", roughness: 0.9 }),
    hub: new MeshStandardMaterial({ color: "#B9C3C2", roughness: 0.3, metalness: 0.7 }),
    lamp: new MeshBasicMaterial({ color: "#FFF4D6" }),
    stampBody: new MeshPhysicalMaterial({ color: INK, roughness: 0.3, clearcoat: 0.7 }),
    stampHandle: new MeshStandardMaterial({ color: "#B88A5A", roughness: 0.55 }),
    stampRubber: new MeshStandardMaterial({ color: MARKER, roughness: 0.8 }),
    invoice: new MeshStandardMaterial({ map: invoiceMap, roughness: 0.7, side: DoubleSide }),
    check: new SpriteMaterial({ map: texture(checkBadge()), transparent: true, depthTest: false }),
  } as const;

  const geometry = {
    longWall: new BoxGeometry(W, H, T), shortWall: new BoxGeometry(T, H, D - 2 * T), floor: new BoxGeometry(W - 2 * T, T, D - 2 * T),
    longFlap: new BoxGeometry(W, T, D / 2), shortFlap: new BoxGeometry(D / 2, T, D - 2 * T),
    tape: new BoxGeometry(W + 0.004, 0.004, 0.11), tapeTail: new BoxGeometry(0.004, 0.16, 0.22), cut: new BoxGeometry(W, 0.006, 0.01),
    reseal: new BoxGeometry(W + 0.006, 0.005, 0.22),
    board: new BoxGeometry(3.6, 0.045, 0.3), block: new BoxGeometry(0.28, 0.13, 0.28), runner: new BoxGeometry(0.3, 0.04, 2.6),
    shadow: new PlaneGeometry(5.6, 4.4), label: new PlaneGeometry(LABEL_SIZE[0], LABEL_SIZE[1]),
    bottle: new LatheGeometry([[0, 0], [0.092, 0], [0.1, 0.02], [0.1, 0.34], [0.088, 0.41], [0.052, 0.46], [0.041, 0.49], [0.041, BOTTLE_HEIGHT]]
      .map(([radius, y]) => new Vector2(radius, y)), 24),
    cap: new CylinderGeometry(0.046, 0.046, CAP_HEIGHT, 18),
    unit: new BoxGeometry(1, 1, 1), plane: new PlaneGeometry(1, 1),
    wheel: new CylinderGeometry(0.34, 0.34, 0.26, 28), hub: new CylinderGeometry(0.17, 0.17, 0.27, 18),
    handle: new CylinderGeometry(0.06, 0.08, 0.34, 18), knob: new CylinderGeometry(0.1, 0.1, 0.08, 20),
    bush: new CylinderGeometry(0.0, 0.32, 0.7, 7),
  } as const;

  return {
    material, geometry,
    /** Repaint the label and invoice for the carton count chosen on the landing page stepper. */
    setCartonCount(cartons: number) {
      labelText.summary = `${cartons} KARTON  ${cartons * CARTON.perCarton} BOTOL`;
      labelMap.image = shippingLabel(fonts, labelText, false); stampedMap.image = shippingLabel(fonts, labelText, true);
      invoiceMap.image = invoiceSheet(fonts, `PT-00000142  ${cartons} karton`);
      labelMap.needsUpdate = stampedMap.needsUpdate = invoiceMap.needsUpdate = true;
    },
    dispose() {
      textures.forEach(value => value.dispose());
      Object.values(material).forEach((value: Material) => value.dispose());
      Object.values(geometry).forEach((value: BufferGeometry) => value.dispose());
    },
  };
}
export type Kit = ReturnType<typeof createKit>;
