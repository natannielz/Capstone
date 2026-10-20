import { BOX } from "./journey";

/**
 * Static isometric drawing of the packed box: the server renders it, it shows while three.js loads, and it stays when
 * WebGL is missing or motion is reduced and the scene cannot start. Drawn back to front (painter's order).
 */
const C = Math.cos(Math.PI / 6), S = 0.5, SCALE = 150;
const W = BOX.width + 2 * BOX.wall, D = BOX.depth + 2 * BOX.wall, H = BOX.height + BOX.wall;
const BRAND_PATH = "M8 8h48v8H36v24h-8V16H8V8Zm0 18h8v12c0 8 6 14 16 14s16-6 16-14V26h8v12c0 13-10 22-24 22S8 51 8 38V26Z";
const TONE = { top: "#D3A86E", front: "#C2955B", side: "#A87D49", seam: "#8F6A3C", print: "#0E4448", tape: "#D9B57A", label: "#FBFBF8", labelBar: "#10343A" };

type P3 = [number, number, number];
const project = ([x, y, z]: P3) => [(x + z) * C * SCALE, ((z - x) * S - y) * SCALE] as const;
const points = (corners: P3[]) => corners.map(corner => project(corner).map(value => value.toFixed(1)).join(",")).join(" ");
/** Maps 2D artwork (u right, v down, in world units) onto a face plane through `origin`. */
function onFace(origin: P3, across: P3, down: P3): string {
  const [ox, oy] = project(origin), [ax, ay] = project([origin[0] + across[0], origin[1] + across[1], origin[2] + across[2]]);
  const [dx, dy] = project([origin[0] + down[0], origin[1] + down[1], origin[2] + down[2]]);
  return `matrix(${(ax - ox).toFixed(3)} ${(ay - oy).toFixed(3)} ${(dx - ox).toFixed(3)} ${(dy - oy).toFixed(3)} ${ox.toFixed(1)} ${oy.toFixed(1)})`;
}

export function KemasanPoster({ className }: { className?: string }) {
  const x0 = -W / 2, x1 = W / 2, z0 = -D / 2, z1 = D / 2;
  const [minX, minY] = project([x0, H + 0.4, z0]), [maxX] = project([x1, 0, z1]), [, maxY] = project([x1, -0.3, z1]);
  const left = Math.min(minX, project([x0, 0, z1])[0]) - 40, right = Math.max(maxX, project([x1, 0, z0])[0]) + 40;
  const viewBox = `${left.toFixed(0)} ${(minY - 40).toFixed(0)} ${(right - left).toFixed(0)} ${(maxY - minY + 80).toFixed(0)}`;
  const shadow = project([0, 0, 0]);
  return <svg className={className} viewBox={viewBox} preserveAspectRatio="xMidYMid meet" role="presentation" aria-hidden="true" focusable="false">
    <ellipse cx={shadow[0]} cy={shadow[1] + 8} rx={W * C * SCALE * 1.05} ry={D * S * SCALE * 1.05} fill="#0E3438" opacity="0.16" />
    {/* Front (+z) and right (+x) walls, then the lid. */}
    <polygon points={points([[x0, 0, z1], [x1, 0, z1], [x1, H, z1], [x0, H, z1]])} fill={TONE.front} />
    <polygon points={points([[x1, 0, z1], [x1, 0, z0], [x1, H, z0], [x1, H, z1]])} fill={TONE.side} />
    <polygon points={points([[x0, H, z0], [x1, H, z0], [x1, H, z1], [x0, H, z1]])} fill={TONE.top} />
    <polyline points={points([[x0, H, 0], [x1, H, 0]])} stroke={TONE.seam} strokeWidth="2" fill="none" />
    <polygon points={points([[x0 - 0.01, H + 0.002, -0.08], [x1 + 0.01, H + 0.002, -0.08], [x1 + 0.01, H + 0.002, 0.08], [x0 - 0.01, H + 0.002, 0.08]])} fill={TONE.tape} opacity="0.9" />
    <polygon points={points([[x1 + 0.002, H, -0.08], [x1 + 0.002, H - 0.22, -0.08], [x1 + 0.002, H - 0.22, 0.08], [x1 + 0.002, H, 0.08]])} fill={TONE.tape} opacity="0.9" />
    <g transform={onFace([0.01, H + 0.004, 0.04], [0.62, 0, 0], [0, 0, 0.41])}>
      <rect width="1" height="1" fill={TONE.label} />
      <rect width="1" height="0.17" fill={TONE.labelBar} />
      {[0.32, 0.48, 0.64].map(y => <rect key={y} x="0.06" y={y} width={y === 0.32 ? 0.62 : 0.42} height="0.05" fill="#C7D2D0" />)}
      <rect x="0.06" y="0.8" width="0.88" height="0.12" fill="#10343A" opacity="0.8" />
    </g>
    <g transform={onFace([x0 + 0.1, H - 0.1, z1], [1, 0, 0], [0, -1, 0])} fill={TONE.print} opacity="0.85">
      <path d={BRAND_PATH} transform="scale(0.0028)" />
      <rect x="0.26" y="0.04" width="0.62" height="0.09" />
      <rect x="0.26" y="0.17" width="0.42" height="0.045" />
    </g>
  </svg>;
}
