/**
 * Every surface in the landing scene is painted here on a 2D canvas: no image files and no remote assets, so the
 * scene stays inside the site CSP. Painters return canvases; the kit wraps them in textures. Each painter is seeded,
 * so the same scene is drawn on every visit.
 */
export const INK = "#0E4448", INK_DARK = "#10343A", KRAFT = "#C79A62", KRAFT_INSIDE = "#B38652", KRAFT_EDGE = "#9C7446";
export const ORANGE = "#E2601F", PAPER = "#FBFBF8", STAGE = "#DDE5E2";
export type Fonts = { stencil: string; body: string };

// Unit Toko mark (a T held inside a U), the same path as BrandMark.
export const BRAND_PATH = "M8 8h48v8H36v24h-8V16H8V8Zm0 18h8v12c0 8 6 14 16 14s16-6 16-14V26h8v12c0 13-10 22-24 22S8 51 8 38V26Z";

export function seeded(seed: number) { return () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 2 ** 32; }; }

function canvas(width: number, height: number) {
  const element = document.createElement("canvas"); element.width = width; element.height = height;
  return { element, context: element.getContext("2d", { willReadFrequently: true })! };
}

/** Smooth value noise: a coarse random grid upscaled with bilinear smoothing, several octaves deep. */
function mottle(context: CanvasRenderingContext2D, width: number, height: number, random: () => number, octaves: [cells: number, alpha: number][],
  dark: string, light: string) {
  for (const [cells, alpha] of octaves) {
    const grid = canvas(cells, Math.max(2, Math.round(cells * height / width)));
    for (let y = 0; y < grid.element.height; y++) for (let x = 0; x < cells; x++) {
      grid.context.fillStyle = random() > 0.5 ? dark : light; grid.context.globalAlpha = random(); grid.context.fillRect(x, y, 1, 1);
    }
    context.save(); context.globalAlpha = alpha; context.imageSmoothingEnabled = true; context.imageSmoothingQuality = "high";
    context.drawImage(grid.element, 0, 0, width, height); context.restore();
  }
}

/** Recycled kraft liner: low-frequency mottling, long fibres and specks; `height` drives the normal map. */
export function kraft(width: number, height: number, tone: string, seed: number) {
  const color = canvas(width, height), bump = canvas(width, height), random = seeded(seed);
  color.context.fillStyle = tone; color.context.fillRect(0, 0, width, height);
  mottle(color.context, width, height, random, [[6, 0.22], [24, 0.14], [96, 0.08]], "#6E4A22", "#E9C99A");
  bump.context.fillStyle = "#808080"; bump.context.fillRect(0, 0, width, height);
  // The corrugation shows through the liner as very soft vertical ridges.
  for (let x = 0; x < width; x++) {
    const wave = Math.sin(x / width * Math.PI * 2 * Math.round(width / 22));
    bump.context.fillStyle = `rgba(${wave > 0 ? "255,255,255" : "0,0,0"},${Math.abs(wave) * 0.07})`; bump.context.fillRect(x, 0, 1, height);
    color.context.fillStyle = `rgba(90,60,25,${Math.max(0, -wave) * 0.025})`; color.context.fillRect(x, 0, 1, height);
  }
  for (let i = 0; i < width * height / 260; i++) {
    const x = random() * width, y = random() * height, length = 2 + random() * 9, angle = random() * Math.PI;
    const dark = random() > 0.45;
    color.context.strokeStyle = dark ? `rgba(78,50,20,${0.08 + random() * 0.12})` : `rgba(250,230,195,${0.06 + random() * 0.1})`;
    color.context.lineWidth = 0.6 + random() * 0.8; color.context.beginPath(); color.context.moveTo(x, y);
    color.context.lineTo(x + Math.cos(angle) * length, y + Math.sin(angle) * length); color.context.stroke();
    if (random() > 0.7) { bump.context.fillStyle = `rgba(255,255,255,${0.15 + random() * 0.2})`; bump.context.fillRect(x, y, 1 + random() * 2, 1); }
  }
  for (let i = 0; i < width * height / 9000; i++) {
    color.context.fillStyle = `rgba(55,35,15,${0.25 + random() * 0.3})`;
    color.context.beginPath(); color.context.arc(random() * width, random() * height, 0.6 + random() * 1.4, 0, Math.PI * 2); color.context.fill();
  }
  return { color: color.element, height: bump.element, context: color.context };
}

/**
 * Tangent-space normal map from a grey height canvas (Sobel), for MeshStandardMaterial.normalMap. The liner's relief is
 * soft, so it is computed at half resolution (a quarter of the work); the strength is scaled so the slopes stay the same.
 */
export function normalFromHeight(source: HTMLCanvasElement, strength: number, scale = 0.5) {
  const width = Math.max(1, Math.round(source.width * scale)), height = Math.max(1, Math.round(source.height * scale));
  const out = canvas(width, height); out.context.drawImage(source, 0, 0, width, height);
  const pixels = out.context.getImageData(0, 0, width, height).data, relief = new Float32Array(width * height);
  for (let index = 0; index < relief.length; index++) relief[index] = pixels[index * 4] / 255;
  const image = out.context.createImageData(width, height), data = image.data, k = strength * scale;
  for (let y = 0; y < height; y++) {
    const up = ((y - 1 + height) % height) * width, row = y * width, down = ((y + 1) % height) * width;
    for (let x = 0; x < width; x++) {
      const left = x === 0 ? width - 1 : x - 1, right = x === width - 1 ? 0 : x + 1;
      const dx = (relief[up + right] + 2 * relief[row + right] + relief[down + right]) - (relief[up + left] + 2 * relief[row + left] + relief[down + left]);
      const dy = (relief[down + left] + 2 * relief[down + x] + relief[down + right]) - (relief[up + left] + 2 * relief[up + x] + relief[up + right]);
      const nx = -dx * k, ny = -dy * k, inverse = 1 / Math.sqrt(nx * nx + ny * ny + 1), index = (row + x) * 4;
      data[index] = (nx * inverse * 0.5 + 0.5) * 255; data[index + 1] = (ny * inverse * 0.5 + 0.5) * 255; data[index + 2] = (inverse * 0.5 + 0.5) * 255; data[index + 3] = 255;
    }
  }
  out.context.putImageData(image, 0, 0); return out.element;
}

export function brandMark(context: CanvasRenderingContext2D, x: number, y: number, size: number) {
  context.save(); context.translate(x, y); context.scale(size / 64, size / 64); context.fill(new Path2D(BRAND_PATH)); context.restore();
}

/** Flexographic ink on kraft never prints solid: knock small specks out of whatever was just drawn. */
function wearInk(context: CanvasRenderingContext2D, x: number, y: number, width: number, height: number, random: () => number, amount: number) {
  context.save(); context.globalCompositeOperation = "destination-out";
  for (let i = 0; i < width * height * amount; i++) context.fillRect(x + random() * width, y + random() * height, 1 + random() * 2.5, 1 + random() * 1.5);
  context.restore();
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

/** ISO 780 "keep dry": an umbrella with rain. */
function keepDry(context: CanvasRenderingContext2D, x: number, y: number, size: number) {
  context.save(); context.translate(x, y); context.scale(size / 100, size / 100); context.lineWidth = 8; context.lineCap = "round";
  context.beginPath(); context.arc(50, 52, 38, Math.PI, 0); context.closePath(); context.fill();
  context.beginPath(); context.moveTo(50, 52); context.lineTo(50, 86); context.arc(42, 86, 8, 0, Math.PI); context.stroke();
  for (const [dx, dy] of [[14, 0], [40, -6], [66, 2], [88, -4]]) { context.beginPath(); context.moveTo(dx, dy + 2); context.lineTo(dx - 4, dy + 12); context.stroke(); }
  context.restore();
}

/** Outer faces of the box: the mark, the name and what the box is for, printed in teal ink. */
export function boxPrint(fonts: Fonts, face: "long" | "short", seed: number) {
  const width = face === "long" ? 1536 : 1024, height = 896, sheet = kraft(width, height, KRAFT, seed), context = sheet.context, random = seeded(seed + 101);
  context.fillStyle = INK; context.globalAlpha = 0.88;
  if (face === "long") {
    brandMark(context, 96, 120, 170);
    context.font = `128px ${fonts.stencil}`; context.fillText("UNIT TOKO", 300, 250);
    context.font = `52px ${fonts.stencil}`; context.fillText("KEBUTUHAN CABANG", 304, 330);
    context.fillRect(96, 400, width - 192, 7);
    upArrows(context, 96, 470, 150); keepDry(context, 286, 476, 140);
    context.font = `44px ${fonts.stencil}`; context.fillText("JANGAN DIBANTING", 470, 560);
    context.fillText("SIMPAN DI TEMPAT KERING", 470, 624);
    context.textAlign = "right"; context.font = `40px ${fonts.stencil}`; context.fillText("DIKEMAS PER PERMINTAAN", width - 96, 780);
  } else {
    context.textAlign = "center"; upArrows(context, width / 2 - 80, 120, 160);
    context.font = `92px ${fonts.stencil}`; context.fillText("ATAS", width / 2, 400);
    brandMark(context, width / 2 - 70, 470, 140);
    context.font = `40px ${fonts.stencil}`; context.fillText("UNIT TOKO", width / 2, 690);
  }
  wearInk(context, 0, 0, width, height, random, 0.012);
  context.globalAlpha = 1;
  return sheet;
}

/** Inside liner: plain, a little lighter, with the manufacturer's joint stamp. */
export function boxInside(seed: number) { return kraft(1024, 1024, KRAFT_INSIDE, seed); }

/** Cut edge of corrugated board: two liners with the wavy flute between them. */
export function fluteEdge() {
  const { element, context } = canvas(512, 32);
  context.fillStyle = "#B08654"; context.fillRect(0, 0, 512, 32);
  context.fillStyle = "#8E6638"; context.fillRect(0, 0, 512, 5); context.fillRect(0, 27, 512, 5);
  context.strokeStyle = "#6E4C26"; context.lineWidth = 2.2; context.beginPath();
  for (let x = 0; x <= 512; x += 2) context.lineTo(x, 16 + Math.sin(x / 512 * Math.PI * 2 * 30) * 8.5);
  context.stroke();
  return element;
}

/** Clear tan packing tape: streaky, with a faint repeating print. */
export function tape(fonts: Fonts) {
  const { element, context } = canvas(1024, 96), random = seeded(41);
  context.fillStyle = "#D9B57A"; context.fillRect(0, 0, 1024, 96);
  for (let y = 0; y < 96; y += 2) { context.fillStyle = `rgba(255,245,220,${random() * 0.12})`; context.fillRect(0, y, 1024, 1); }
  context.fillStyle = "rgba(14,68,72,0.35)"; context.font = `26px ${fonts.stencil}`;
  for (let x = 30; x < 1024; x += 260) context.fillText("UNIT TOKO", x, 58);
  return element;
}

export type LabelText = { number: string; from: string; to: string; summary: string };

/** The printed request label on the lid. */
export function shippingLabel(fonts: Fonts, text: LabelText) {
  const { element, context } = canvas(640, 420);
  context.fillStyle = PAPER; context.fillRect(0, 0, 640, 420);
  context.fillStyle = INK_DARK; context.fillRect(0, 0, 640, 70);
  context.fillStyle = "#FFFFFF"; context.font = `600 28px ${fonts.body}`; context.fillText("Permintaan kebutuhan cabang", 26, 46);
  context.fillStyle = INK_DARK; context.font = `50px ${fonts.stencil}`; context.fillText(text.number, 26, 136);
  context.fillRect(0, 158, 640, 3);
  context.font = `500 22px ${fonts.body}`; context.fillStyle = "#4A6266"; context.fillText("Dari", 26, 200); context.fillText("Ke", 26, 242);
  context.fillStyle = INK_DARK; context.font = `600 24px ${fonts.body}`; context.fillText(text.from, 96, 200); context.fillText(text.to, 96, 242);
  context.fillRect(0, 268, 640, 3);
  context.font = `38px ${fonts.stencil}`; context.fillText(text.summary, 26, 322);
  for (let x = 26, index = 0; x < 614; index++) { const width = [2, 4, 3, 6, 2, 3][index % 6]; context.fillRect(x, 346, width, 50); x += width + [3, 2, 4, 3, 2][index % 5]; }
  return element;
}

/** Receipt slip on the branch counter, with the rubber-stamp impression drawn when `stamped`. */
export function receiptSlip(fonts: Fonts, stamped: boolean) {
  const { element, context } = canvas(512, 704), random = seeded(53);
  context.fillStyle = PAPER; context.fillRect(0, 0, 512, 704);
  context.fillStyle = INK_DARK; context.font = `46px ${fonts.stencil}`; context.fillText("TANDA TERIMA", 34, 82);
  context.fillRect(34, 104, 444, 4);
  context.font = `500 24px ${fonts.body}`; context.fillStyle = "#4A6266";
  ["Nomor kiriman", "Penerima", "Jumlah kemasan"].forEach((field, index) => context.fillText(field, 34, 160 + index * 56));
  context.fillStyle = INK_DARK; context.font = `600 26px ${fonts.body}`;
  ["KRM-0388", "PIC Cabang A", "5 kemasan"].forEach((value, index) => context.fillText(value, 250, 160 + index * 56));
  context.fillStyle = "rgba(16,52,58,0.13)";
  for (let y = 360; y < 560; y += 40) context.fillRect(34, y, 280 + (y % 3) * 50, 14);
  if (stamped) {
    context.save(); context.translate(300, 560); context.rotate(-0.14);
    context.strokeStyle = ORANGE; context.fillStyle = ORANGE; context.globalAlpha = 0.9; context.lineWidth = 7;
    context.beginPath(); context.roundRect(-160, -64, 320, 128, 14); context.stroke();
    context.lineWidth = 2.5; context.beginPath(); context.roundRect(-148, -52, 296, 104, 9); context.stroke();
    context.textAlign = "center"; context.font = `58px ${fonts.stencil}`; context.fillText("DITERIMA", 0, 16);
    context.font = `18px ${fonts.stencil}`; context.fillText("CABANG A  SEL 11.20", 0, 42);
    context.globalCompositeOperation = "destination-out";
    for (let i = 0; i < 520; i++) context.fillRect(-160 + random() * 320, -64 + random() * 128, 1 + random() * 3, 1 + random() * 2);
    context.restore();
  }
  return element;
}

/** The invoice that appears once receipt is recorded. */
export function invoiceSheet(fonts: Fonts) {
  const { element, context } = canvas(480, 640);
  context.fillStyle = PAPER; context.fillRect(0, 0, 480, 640);
  context.fillStyle = INK_DARK; context.font = `56px ${fonts.stencil}`; context.fillText("TAGIHAN", 36, 92);
  context.fillRect(36, 116, 408, 4);
  context.font = `500 24px ${fonts.body}`; context.fillStyle = "#4A6266"; context.fillText("Sesuai barang yang diterima", 36, 166);
  context.fillStyle = INK_DARK; context.font = `600 28px ${fonts.body}`; context.fillText("5 kemasan, Cabang A", 36, 226);
  context.fillStyle = "rgba(16,52,58,0.14)";
  for (let y = 270; y < 520; y += 46) context.fillRect(36, y, 300 + ((y / 46) % 3) * 36, 16);
  context.fillStyle = INK_DARK; context.fillRect(36, 548, 408, 3);
  context.font = `38px ${fonts.stencil}`; context.textAlign = "right"; context.fillText("TERBIT SEL 11.20", 444, 604);
  return element;
}

/** Round confirmation mark for each reviewed item. */
export function checkBadge() {
  const { element, context } = canvas(256, 256);
  context.fillStyle = "#FFFFFF"; context.beginPath(); context.arc(128, 128, 116, 0, Math.PI * 2); context.fill();
  context.strokeStyle = "#1F7A5A"; context.lineWidth = 26; context.lineCap = "round"; context.lineJoin = "round";
  context.beginPath(); context.moveTo(70, 132); context.lineTo(110, 172); context.lineTo(188, 90); context.stroke();
  return element;
}

/** Package art for the five catalog items. Each returns the front face; the kit derives the other faces from its colours. */
export type PackageArt = { front: HTMLCanvasElement; side: string; top: string };

export function coffeeArt(fonts: Fonts): PackageArt {
  const { element, context } = canvas(512, 416), gradient = context.createLinearGradient(0, 0, 0, 416);
  gradient.addColorStop(0, "#4A2E1C"); gradient.addColorStop(1, "#2C1A10"); context.fillStyle = gradient; context.fillRect(0, 0, 512, 416);
  context.fillStyle = "#7A4A2A";
  for (const [x, y, r] of [[380, 120, 46], [440, 190, 38], [360, 220, 34]]) {
    context.save(); context.translate(x, y); context.rotate(0.5); context.beginPath(); context.ellipse(0, 0, r, r * 0.66, 0, 0, Math.PI * 2); context.fill();
    context.strokeStyle = "#2C1A10"; context.lineWidth = 5; context.beginPath(); context.moveTo(-r * 0.8, 0); context.quadraticCurveTo(0, r * 0.25, r * 0.8, 0); context.stroke(); context.restore();
  }
  context.fillStyle = "#F3E3C7"; context.font = `64px ${fonts.stencil}`; context.fillText("KOPI", 34, 110); context.fillText("BUBUK", 34, 180);
  context.font = `500 26px ${fonts.body}`; context.fillText("Robusta sangrai", 36, 232);
  context.fillStyle = INK; context.fillRect(0, 300, 512, 116);
  context.fillStyle = "#F3E3C7"; context.font = `44px ${fonts.stencil}`; context.fillText("ISI 24 SACHET", 36, 372);
  brandMark(context, 430, 322, 52);
  return { front: element, side: "#3A2416", top: "#4A2E1C" };
}

export function teaArt(fonts: Fonts): PackageArt {
  const { element, context } = canvas(512, 224);
  context.fillStyle = "#2E6A4E"; context.fillRect(0, 0, 512, 224);
  context.fillStyle = "#F5F7EF";
  for (const [x, y, r] of [[400, 70, 28], [460, 140, 22], [370, 160, 18]]) for (let petal = 0; petal < 5; petal++) {
    const angle = petal / 5 * Math.PI * 2; context.beginPath(); context.ellipse(x + Math.cos(angle) * r * 0.7, y + Math.sin(angle) * r * 0.7, r * 0.55, r * 0.3, angle, 0, Math.PI * 2); context.fill();
  }
  context.fillStyle = "#E8C34A"; for (const [x, y] of [[400, 70], [460, 140], [370, 160]]) { context.beginPath(); context.arc(x, y, 6, 0, Math.PI * 2); context.fill(); }
  context.fillStyle = "#F5F7EF"; context.font = `54px ${fonts.stencil}`; context.fillText("TEH MELATI", 28, 92);
  context.font = `500 24px ${fonts.body}`; context.fillText("25 kantong celup", 30, 138);
  context.fillStyle = INK; context.fillRect(0, 176, 512, 48); context.fillStyle = "#F5F7EF"; brandMark(context, 28, 182, 34);
  return { front: element, side: "#285C44", top: "#2E6A4E" };
}

export function paperArt(fonts: Fonts): PackageArt {
  const { element, context } = canvas(768, 192);
  context.fillStyle = "#F7F8F4"; context.fillRect(0, 0, 768, 192);
  context.fillStyle = ORANGE; context.fillRect(0, 0, 768, 30); context.fillStyle = INK; context.fillRect(0, 30, 768, 12);
  context.fillStyle = INK_DARK; context.font = `64px ${fonts.stencil}`; context.fillText("HVS A4", 32, 126);
  context.font = `500 26px ${fonts.body}`; context.fillStyle = "#4A6266"; context.fillText("70 gsm, 500 lembar", 300, 116);
  brandMark(context, 690, 92, 46);
  return { front: element, side: "#F2F3EE", top: "#F7F8F4" };
}

export function pensArt(fonts: Fonts): PackageArt {
  const { element, context } = canvas(512, 96);
  context.fillStyle = "#1F2F57"; context.fillRect(0, 0, 512, 96);
  context.fillStyle = "#F2F4FA"; context.font = `40px ${fonts.stencil}`; context.fillText("PULPEN HITAM", 24, 58);
  context.font = `500 18px ${fonts.body}`; context.fillText("0,5 mm, isi 12", 24, 84);
  context.strokeStyle = "#C9D2E8"; context.lineWidth = 6; context.lineCap = "round";
  context.beginPath(); context.moveTo(360, 64); context.lineTo(488, 30); context.stroke();
  return { front: element, side: "#1A2849", top: "#1F2F57" };
}

/** The wrap-around label of the 500 ml bottle. */
export function sanitizerLabel(fonts: Fonts) {
  const { element, context } = canvas(512, 256);
  context.fillStyle = "#FFFFFF"; context.fillRect(0, 0, 512, 256);
  context.fillStyle = "#2A8FA3"; context.beginPath(); context.moveTo(90, 52); context.quadraticCurveTo(130, 110, 90, 150); context.quadraticCurveTo(50, 110, 90, 52); context.fill();
  context.fillStyle = INK_DARK; context.font = `34px ${fonts.stencil}`; context.fillText("PENYANITASI", 150, 96); context.fillText("TANGAN", 150, 136);
  context.font = `500 22px ${fonts.body}`; context.fillStyle = "#4A6266"; context.fillText("Gel, 500 ml", 152, 176);
  context.fillStyle = INK; context.fillRect(0, 214, 512, 42);
  return element;
}

/** Matte studio floor: soft value noise, nothing that competes with the box. */
export function studioFloor() {
  const { element, context } = canvas(1024, 1024), random = seeded(7);
  context.fillStyle = "#C9D3D0"; context.fillRect(0, 0, 1024, 1024);
  mottle(context, 1024, 1024, random, [[8, 0.18], [40, 0.1], [160, 0.06]], "#9FADAA", "#E6ECEA");
  return element;
}

/** The route the trolley follows: a dashed teal line. */
export function routeLine() {
  const { element, context } = canvas(2048, 64);
  context.fillStyle = "rgba(14,68,72,0.0)"; context.fillRect(0, 0, 2048, 64);
  context.fillStyle = "rgba(14,68,72,0.78)";
  for (let x = 0; x < 2048; x += 96) context.fillRect(x, 26, 56, 12);
  return element;
}

export function branchSign(fonts: Fonts) {
  const { element, context } = canvas(1024, 192);
  context.fillStyle = INK; context.fillRect(0, 0, 1024, 192);
  context.fillStyle = "#FFFFFF"; brandMark(context, 48, 46, 100);
  context.font = `84px ${fonts.stencil}`; context.fillText("KANTOR CABANG A", 180, 124);
  return element;
}
