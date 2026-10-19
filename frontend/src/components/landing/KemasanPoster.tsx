import { CARTON, LONG_FLAP_OPEN, SHORT_FLAP_OPEN, bottleSpots, cartonSlots } from "./kemasan-timeline";

/**
 * Static isometric drawing of the scene's final frame: the server renders it, it shows while three.js loads, and it
 * stays when WebGL is missing. It follows the same carton count as the scene. Drawn back to front (painter's order).
 */
const C = Math.cos(Math.PI / 6), S = 0.5, SCALE = 100;
const W = CARTON.width, D = CARTON.depth, H = CARTON.height, T = CARTON.wall;
const BRAND_PATH = "M8 8h48v8H36v24h-8V16H8V8Zm0 18h8v12c0 8 6 14 16 14s16-6 16-14V26h8v12c0 13-10 22-24 22S8 51 8 38V26Z";
const TONE = { top: "#D7AF75", front: "#C69A62", side: "#A97E4B", inside: "#8F673B", insideLit: "#A97E4B", print: "#0F4C52",
  tape: "#D9C28B", woodTop: "#DDBE8F", woodFront: "#BF9C68", woodSide: "#A9875A", cap: "#E8622A", bottle: "#DCE9E7" };

type P3 = [number, number, number];
const project = ([x, y, z]: P3) => [(x + z) * C * SCALE, ((z - x) * S - y) * SCALE] as const;
const points = (corners: P3[]) => corners.map(corner => project(corner).map(value => value.toFixed(1)).join(",")).join(" ");
const face = (corners: P3[], fill: string, key: string) => <polygon key={key} points={points(corners)} fill={fill} />;
/** Maps 2D artwork (u right, v down, in world units) onto a face plane through `origin`. */
function onFace(origin: P3, across: P3, down: P3): string {
  const [ox, oy] = project(origin), [ax, ay] = project([origin[0] + across[0], origin[1] + across[1], origin[2] + across[2]]);
  const [dx, dy] = project([origin[0] + down[0], origin[1] + down[1], origin[2] + down[2]]);
  return `matrix(${(ax - ox).toFixed(3)} ${(ay - oy).toFixed(3)} ${(dx - ox).toFixed(3)} ${(dy - oy).toFixed(3)} ${ox.toFixed(1)} ${oy.toFixed(1)})`;
}

function pallet() {
  const x0 = -1.8, x1 = 1.8, z0 = -1.3, z1 = 1.3, y0 = -0.215, y1 = 0;
  const boards = Array.from({ length: 7 }, (_, i) => -1.15 + i * (2.3 / 6)).map((z, i) =>
    face([[x0, y1, z - 0.15], [x1, y1, z - 0.15], [x1, y1, z + 0.15], [x0, y1, z + 0.15]], TONE.woodTop, "board" + i));
  const blocks = [-1.6, 0, 1.6].flatMap(x => [face([[x - 0.14, y0, z1], [x + 0.14, y0, z1], [x + 0.14, -0.045, z1], [x - 0.14, -0.045, z1]], TONE.woodFront, "front" + x)]);
  const sides = [-1.15, 0, 1.15].map(z => face([[x0, y0, z - 0.14], [x0, y0, z + 0.14], [x0, -0.045, z + 0.14], [x0, -0.045, z - 0.14]], TONE.woodSide, "side" + z));
  return <g>
    <ellipse cx="0" cy={project([0, -0.22, 0])[1]} rx="330" ry="150" fill="url(#kemasan-shadow)" />
    {sides}{blocks}
    {face([[x0, -0.045, z1], [x1, -0.045, z1], [x1, 0, z1], [x0, 0, z1]], TONE.woodFront, "deck-front")}
    {face([[x0, -0.045, z0], [x0, -0.045, z1], [x0, 0, z1], [x0, 0, z0]], TONE.woodSide, "deck-side")}
    {boards}
  </g>;
}

function walls(x: number, z: number, key: string) {
  const x0 = x - W / 2, x1 = x + W / 2, z0 = z - D / 2, z1 = z + D / 2;
  return { x0, x1, z0, z1, outside: <g key={key + "-outside"}>
    {face([[x0, 0, z1], [x1, 0, z1], [x1, H, z1], [x0, H, z1]], TONE.front, "front")}
    {face([[x0, 0, z0], [x0, 0, z1], [x0, H, z1], [x0, H, z0]], TONE.side, "left")}
    <g fill={TONE.print} opacity="0.86">
      <path d={BRAND_PATH} transform={onFace([x0 + 0.07, H - 0.06, z1], [0.0022, 0, 0], [0, -0.0022, 0])} />
      <text transform={onFace([x0 + 0.24, H - 0.12, z1], [0.0012, 0, 0], [0, -0.0012, 0])} fontSize="80" fontFamily="var(--font-stencil), sans-serif">UNIT TOKO</text>
      <text transform={onFace([x1 - 0.07, H - 0.44, z1], [0.0012, 0, 0], [0, -0.0012, 0])} fontSize="140" textAnchor="end" fontFamily="var(--font-stencil), sans-serif">ISI 24</text>
      <text transform={onFace([x0, H - 0.36, z0 + D / 2], [0, 0, 0.0012], [0, -0.0012, 0])} fontSize="72" textAnchor="middle" fontFamily="var(--font-stencil), sans-serif">ISI 24 BOTOL</text>
    </g>
  </g> };
}

function flap(hinge: [P3, P3], out: P3, fill: string, key: string) {
  const [a, b] = hinge, reach = D / 2;
  return face([a, b, [b[0] + out[0] * reach, b[1] + out[1] * reach, b[2] + out[2] * reach], [a[0] + out[0] * reach, a[1] + out[1] * reach, a[2] + out[2] * reach]], fill, key);
}

function openCarton(x: number, z: number) {
  const { x0, x1, z0, z1, outside } = walls(x, z, "open");
  const sinL = Math.sin(LONG_FLAP_OPEN), cosL = Math.cos(LONG_FLAP_OPEN), sinS = Math.sin(SHORT_FLAP_OPEN), cosS = Math.cos(SHORT_FLAP_OPEN);
  const rx = 0.046 * C * Math.SQRT2 * SCALE, ry = 0.046 * S * Math.SQRT2 * SCALE;
  const caps = bottleSpots().map((spot, i) => {
    const [cx, cy] = project([x + spot.x, H, z + spot.z]), [sx, sy] = project([x + spot.x, H - 0.13, z + spot.z]);
    return <g key={"bottle" + i}>
      <ellipse cx={sx.toFixed(1)} cy={sy.toFixed(1)} rx={(rx * 2.1).toFixed(1)} ry={(ry * 2.1).toFixed(1)} fill={TONE.bottle} />
      <ellipse cx={cx.toFixed(1)} cy={(cy + 4).toFixed(1)} rx={rx.toFixed(1)} ry={ry.toFixed(1)} fill="#B9461C" />
      <ellipse cx={cx.toFixed(1)} cy={cy.toFixed(1)} rx={rx.toFixed(1)} ry={ry.toFixed(1)} fill={TONE.cap} />
    </g>;
  });
  return <g key="open">
    {flap([[x0, H, z0], [x1, H, z0]], [0, sinL, cosL], TONE.insideLit, "flap-back")}
    {flap([[x1, H, z0 + T], [x1, H, z1 - T]], [-cosS, sinS, 0], TONE.insideLit, "flap-right")}
    {face([[x0 + T, T, z0 + T], [x1 - T, T, z0 + T], [x1 - T, T, z1 - T], [x0 + T, T, z1 - T]], TONE.inside, "floor")}
    {face([[x0 + T, T, z0 + T], [x1 - T, T, z0 + T], [x1 - T, H, z0 + T], [x0 + T, H, z0 + T]], TONE.insideLit, "inner-back")}
    {face([[x1 - T, T, z0 + T], [x1 - T, T, z1 - T], [x1 - T, H, z1 - T], [x1 - T, H, z0 + T]], TONE.inside, "inner-right")}
    {caps}
    {outside}
    {flap([[x0, H, z1], [x1, H, z1]], [0, sinL, -cosL], TONE.top, "flap-front")}
    {flap([[x0, H, z0 + T], [x0, H, z1 - T]], [cosS, sinS, 0], TONE.top, "flap-left")}
  </g>;
}

function sealedCarton(x: number, z: number, key: string) {
  const { x0, x1, z0, z1, outside } = walls(x, z, key);
  return <g key={key}>
    {outside}
    {face([[x0, H, z0], [x1, H, z0], [x1, H, z1], [x0, H, z1]], TONE.top, "top")}
    {face([[x0, H, z - 0.11], [x1, H, z - 0.11], [x1, H, z + 0.11], [x0, H, z + 0.11]], TONE.tape, "tape")}
    {face([[x0, H - 0.16, z - 0.11], [x0, H - 0.16, z + 0.11], [x0, H, z + 0.11], [x0, H, z - 0.11]], TONE.tape, "tail")}
  </g>;
}

export function KemasanPoster({ cartons, className }: { cartons: number; className?: string }) {
  const slots = cartonSlots(cartons).map((slot, index) => ({ ...slot, index }));
  // Painter's order for a viewer at front-left: far (small z - x) first.
  const ordered = [...slots].sort((a, b) => (a.z - a.x) - (b.z - b.x));
  return <svg className={className} viewBox="-310 -300 620 520" preserveAspectRatio="xMidYMid meet" focusable="false">
    <defs>
      <radialGradient id="kemasan-shadow"><stop offset="0" stopColor="#06262A" stopOpacity="0.5" /><stop offset="1" stopColor="#06262A" stopOpacity="0" /></radialGradient>
    </defs>
    {pallet()}
    {ordered.map(slot => slot.index === 0 ? openCarton(slot.x, slot.z) : sealedCarton(slot.x, slot.z, "sealed" + slot.index))}
  </svg>;
}
