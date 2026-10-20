/**
 * Every surface in the landing scene is painted here on a 2D canvas: no image files and no remote assets, so the
 * scene stays inside the site CSP. Painters return canvases; the scene wraps them in textures.
 */
export const INK = "#0F4C52", INK_DARK = "#12343A", KRAFT = "#C69A62", KRAFT_EDGE = "#A97E4B", KRAFT_INSIDE = "#9E7444";
export const MARKER = "#E8622A", LABEL_PAPER = "#FBFCFA", WOOD = "#D6B585", KRAFT_TEXT = "#E0BE8E";
export type Fonts = { stencil: string; body: string };

// Unit Toko mark (a T held inside a U), the same path as BrandMark.
export const BRAND_PATH = "M8 8h48v8H36v24h-8V16H8V8Zm0 18h8v12c0 8 6 14 16 14s16-6 16-14V26h8v12c0 13-10 22-24 22S8 51 8 38V26Z";

export function seeded(seed: number) { return () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 2 ** 32; }; }

function canvas(width: number, height: number) {
  const element = document.createElement("canvas"); element.width = width; element.height = height;
  return { element, context: element.getContext("2d")! };
}

/** Kraft liner: faint corrugation bands and recycled-fibre speckle. */
export function board(width: number, height: number, base: string, seed: number) {
  const { element, context } = canvas(width, height), random = seeded(seed);
  context.fillStyle = base; context.fillRect(0, 0, width, height);
  for (let x = 0; x < width; x += 7) { context.fillStyle = `rgba(80,52,20,${0.02 + random() * 0.03})`; context.fillRect(x, 0, 3, height); }
  for (let i = 0; i < width * height / 700; i++) {
    context.fillStyle = random() > 0.5 ? "rgba(70,45,15,0.11)" : "rgba(255,240,210,0.10)";
    context.fillRect(random() * width, random() * height, 1 + random() * 3, 1);
  }
  return { canvas: element, context };
}

/** Grey flutes for a bump map: the corrugation shows through the liner as soft ridges. */
export function corrugationBump() {
  const { element, context } = canvas(256, 64);
  for (let x = 0; x < 256; x++) { const shade = 128 + Math.round(Math.sin(x / 256 * Math.PI * 2 * 18) * 60); context.fillStyle = `rgb(${shade},${shade},${shade})`; context.fillRect(x, 0, 1, 64); }
  return element;
}

/** Cut edge of corrugated board: two liners with the wavy flute between them. */
export function fluteEdge() {
  const { element, context } = canvas(256, 32);
  context.fillStyle = KRAFT_EDGE; context.fillRect(0, 0, 256, 32);
  context.fillStyle = "#8A6236"; context.fillRect(0, 0, 256, 4); context.fillRect(0, 28, 256, 4);
  context.strokeStyle = "#7A5530"; context.lineWidth = 2; context.beginPath();
  for (let x = 0; x <= 256; x += 2) context.lineTo(x, 16 + Math.sin(x / 256 * Math.PI * 2 * 16) * 9);
  context.stroke();
  return element;
}

export function brandMark(context: CanvasRenderingContext2D, x: number, y: number, size: number) {
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

export function printedLong(fonts: Fonts) {
  const { canvas: element, context } = board(1024, 400, KRAFT, 7);
  context.fillStyle = INK; context.globalAlpha = 0.86;
  brandMark(context, 52, 44, 96);
  context.font = `66px ${fonts.stencil}`; context.fillText("UNIT TOKO", 166, 112);
  context.font = `30px ${fonts.stencil}`; context.fillText("AIR MINERAL 600 ML", 168, 160);
  upArrows(context, 56, 236, 104);
  context.font = `26px ${fonts.stencil}`; context.fillText("JANGAN DIBANTING", 176, 336);
  context.textAlign = "right"; context.font = `132px ${fonts.stencil}`; context.fillText("ISI 24", 972, 300);
  context.font = `36px ${fonts.stencil}`; context.fillText("BOTOL", 972, 350);
  return element;
}

export function printedShort(fonts: Fonts) {
  const { canvas: element, context } = board(704, 400, KRAFT, 11);
  context.fillStyle = INK; context.globalAlpha = 0.86; context.textAlign = "center";
  upArrows(context, 302, 34, 100);
  context.font = `72px ${fonts.stencil}`; context.fillText("ISI 24 BOTOL", 352, 238);
  brandMark(context, 322, 286, 60);
  return element;
}

export function bottleWrap() {
  const { element, context } = canvas(256, 128);
  context.fillStyle = "#EEF6F5"; context.fillRect(0, 0, 256, 128);
  context.fillStyle = "rgba(255,255,255,0.55)"; context.fillRect(0, 18, 256, 6);
  context.fillStyle = INK; context.fillRect(0, 52, 256, 26);
  context.strokeStyle = "#DCEFEC"; context.lineWidth = 3; context.beginPath();
  for (let x = 0; x <= 256; x += 4) context.lineTo(x, 65 + Math.sin(x / 256 * Math.PI * 6) * 4);
  context.stroke();
  return element;
}

/** Light pallet pine with long grain lines and a few knots. */
export function woodGrain(seed: number) {
  const { element, context } = canvas(512, 128), random = seeded(seed);
  context.fillStyle = WOOD; context.fillRect(0, 0, 512, 128);
  for (let line = 0; line < 46; line++) {
    const y = random() * 128, amplitude = 1 + random() * 3, phase = random() * 6;
    context.strokeStyle = `rgba(120,80,40,${0.06 + random() * 0.12})`; context.lineWidth = 0.6 + random() * 1.4; context.beginPath();
    for (let x = 0; x <= 512; x += 8) context.lineTo(x, y + Math.sin(x / 90 + phase) * amplitude);
    context.stroke();
  }
  for (let knot = 0; knot < 3; knot++) { context.fillStyle = "rgba(110,70,35,0.25)"; context.beginPath(); context.ellipse(random() * 512, random() * 128, 7, 3, 0, 0, Math.PI * 2); context.fill(); }
  return element;
}

export type LabelText = { number: string; from: string; to: string; summary: string };

/** The printed request label, with an optional "DITERIMA" stamp impression. */
export function shippingLabel(fonts: Fonts, text: LabelText, stamped: boolean) {
  const { element, context } = canvas(620, 400);
  context.fillStyle = LABEL_PAPER; context.fillRect(0, 0, 620, 400);
  context.fillStyle = INK_DARK; context.fillRect(0, 0, 620, 64);
  context.strokeStyle = INK_DARK; context.lineWidth = 6; context.strokeRect(3, 3, 614, 394);
  context.fillStyle = "#FFFFFF"; context.font = `600 26px ${fonts.body}`; context.fillText("Permintaan kebutuhan cabang", 24, 42);
  context.fillStyle = INK_DARK;
  context.font = `44px ${fonts.stencil}`; context.fillText(text.number, 24, 124);
  context.fillRect(0, 146, 620, 3);
  context.font = `500 20px ${fonts.body}`; context.fillStyle = "#4A6266"; context.fillText("Dari", 24, 186); context.fillText("Ke", 24, 226);
  context.fillStyle = INK_DARK; context.font = `600 22px ${fonts.body}`; context.fillText(text.from, 92, 186); context.fillText(text.to, 92, 226);
  context.fillRect(0, 252, 620, 3);
  context.font = `40px ${fonts.stencil}`; context.fillText(text.summary, 24, 312);
  for (let x = 24, index = 0; x < 596; index++) { const width = [2, 4, 3, 6, 2, 3][index % 6]; context.fillRect(x, 340, width, 40); x += width + [3, 2, 4, 3, 2][index % 5]; }
  if (stamped) stampImpression(context, fonts, 418, 210, -0.16);
  return element;
}

/** Marker-orange rubber-stamp impression, slightly worn so it reads as ink on paper. */
function stampImpression(context: CanvasRenderingContext2D, fonts: Fonts, x: number, y: number, angle: number) {
  context.save(); context.translate(x, y); context.rotate(angle);
  context.strokeStyle = MARKER; context.fillStyle = MARKER; context.globalAlpha = 0.9; context.lineWidth = 7;
  context.beginPath(); context.roundRect(-150, -62, 300, 124, 14); context.stroke();
  context.lineWidth = 2.5; context.beginPath(); context.roundRect(-138, -50, 276, 100, 9); context.stroke();
  context.textAlign = "center"; context.font = `56px ${fonts.stencil}`; context.fillText("DITERIMA", 0, 14);
  context.font = `18px ${fonts.stencil}`; context.fillText("UNIT TOKO  SEN 10.40", 0, 40);
  context.globalCompositeOperation = "destination-out"; const random = seeded(29);
  for (let i = 0; i < 420; i++) context.fillRect(-150 + random() * 300, -62 + random() * 124, 1 + random() * 3, 1 + random() * 2);
  context.restore();
}

/** Floor paint around the pallet: a dashed loading-bay outline and the bay number, stencilled. */
export function bayMarking(fonts: Fonts) {
  const { element, context } = canvas(1024, 768);
  context.strokeStyle = "rgba(224,190,142,0.55)"; context.lineWidth = 10; context.setLineDash([46, 26]);
  context.strokeRect(40, 40, 944, 688); context.setLineDash([]);
  context.fillStyle = "rgba(224,190,142,0.42)"; context.font = `150px ${fonts.stencil}`; context.textAlign = "left";
  context.fillText("DOK 03", 64, 712);
  return element;
}

/** The road: asphalt a shade darker than the dock, faint edges and a dashed kraft centre line. */
export function road() {
  const { element, context } = canvas(2048, 256);
  context.fillStyle = "rgba(4,24,27,0.42)"; context.fillRect(0, 0, 2048, 256);
  context.fillStyle = "rgba(224,190,142,0.25)"; context.fillRect(0, 10, 2048, 5); context.fillRect(0, 241, 2048, 5);
  context.fillStyle = "rgba(224,190,142,0.85)";
  for (let x = 30; x < 2048; x += 120) context.fillRect(x, 120, 64, 14);
  return element;
}

export function pavement() {
  const { element, context } = canvas(512, 512);
  context.fillStyle = "rgba(241,244,239,0.10)"; context.fillRect(0, 0, 512, 512);
  context.strokeStyle = "rgba(241,244,239,0.10)"; context.lineWidth = 2;
  for (let i = 0; i <= 512; i += 64) { context.beginPath(); context.moveTo(i, 0); context.lineTo(i, 512); context.moveTo(0, i); context.lineTo(512, i); context.stroke(); }
  return element;
}

export function branchSign(fonts: Fonts) {
  const { element, context } = canvas(1024, 160);
  context.fillStyle = INK; context.fillRect(0, 0, 1024, 160);
  context.fillStyle = "#FFFFFF"; context.font = `86px ${fonts.stencil}`; context.textAlign = "center"; context.fillText("KANTOR CABANG", 512, 110);
  return element;
}

/** Side of the Unit Toko van: the mark, the name and what it carries. */
export function vanSide(fonts: Fonts) {
  const { element, context } = canvas(1024, 512);
  context.fillStyle = "#F4F6F2"; context.fillRect(0, 0, 1024, 512);
  context.fillStyle = INK; context.fillRect(0, 420, 1024, 92);
  brandMark(context, 70, 120, 150);
  context.font = `118px ${fonts.stencil}`; context.fillText("UNIT TOKO", 250, 240);
  context.font = `500 44px ${fonts.body}`; context.fillStyle = "#4A6266"; context.fillText("Kebutuhan kerja & harian", 254, 310);
  context.fillStyle = KRAFT_TEXT; context.font = `48px ${fonts.stencil}`; context.fillText("ANTAR KE CABANG", 70, 486);
  return element;
}

/** The invoice that appears once receipt is recorded. */
export function invoiceSheet(fonts: Fonts, summary: string) {
  const { element, context } = canvas(480, 640);
  context.fillStyle = LABEL_PAPER; context.fillRect(0, 0, 480, 640);
  context.fillStyle = INK_DARK; context.font = `56px ${fonts.stencil}`; context.fillText("TAGIHAN", 36, 92);
  context.fillRect(36, 116, 408, 4);
  context.font = `500 24px ${fonts.body}`; context.fillStyle = "#4A6266";
  context.fillText("Sesuai barang yang diterima", 36, 166);
  context.fillStyle = INK_DARK; context.font = `600 28px ${fonts.body}`; context.fillText(summary, 36, 226);
  context.fillStyle = "rgba(18,52,58,0.14)";
  for (let y = 270; y < 520; y += 46) context.fillRect(36, y, 300 + ((y / 46) % 3) * 36, 16);
  context.fillStyle = INK_DARK; context.fillRect(36, 548, 408, 3);
  context.font = `40px ${fonts.stencil}`; context.textAlign = "right"; context.fillText("LUNAS SAAT JATUH TEMPO", 444, 604);
  return element;
}

/** Round confirmation mark for "Diterima cabang". */
export function checkBadge() {
  const { element, context } = canvas(256, 256);
  context.fillStyle = "#FFFFFF"; context.beginPath(); context.arc(128, 128, 116, 0, Math.PI * 2); context.fill();
  context.strokeStyle = INK; context.lineWidth = 26; context.lineCap = "round"; context.lineJoin = "round";
  context.beginPath(); context.moveTo(70, 132); context.lineTo(110, 172); context.lineTo(188, 90); context.stroke();
  return element;
}

export function contactShadow() {
  const { element, context } = canvas(256, 256), gradient = context.createRadialGradient(128, 128, 10, 128, 128, 128);
  gradient.addColorStop(0, "rgba(6,38,42,0.55)"); gradient.addColorStop(0.6, "rgba(6,38,42,0.22)"); gradient.addColorStop(1, "rgba(6,38,42,0)");
  context.fillStyle = gradient; context.fillRect(0, 0, 256, 256);
  return element;
}
