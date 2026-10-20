// Renders the landing's "Satu kardus" story to a WebM film, the way HyperFrames does it but entirely locally and free:
// a headless Edge on this machine's GPU seeks the scene's pure story function frame by frame (?film exposes
// window.__kemasanFilm), four sub-frames are blended per frame for real motion blur, the browser's own WebCodecs encoder
// writes VP9, and a small Matroska writer below packs it into a WebM. Nothing leaves the machine.
//
// Usage (dev server on BASE_URL, default http://localhost:3100):
//   node scripts/render-landing-film.mjs [--width 1280] [--height 720] [--fps 30] [--samples 4] [--out public/media/satu-kardus.webm]
// PLAYWRIGHT_PACKAGE may point to a playwright package.json (default: tests/browser/node_modules/playwright).
import { createRequire } from "node:module";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

const argument = (name, fallback) => { const index = process.argv.indexOf("--" + name); return index > 0 ? process.argv[index + 1] : fallback; };
const width = Number(argument("width", "1280")), height = Number(argument("height", "720")), fps = Number(argument("fps", "30"));
const samples = Number(argument("samples", "4")), out = resolve(argument("out", "public/media/satu-kardus.webm"));
const poster = out.replace(/\.webm$/, "-poster.jpg"), base = process.env.BASE_URL ?? "http://localhost:3100";
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_PACKAGE ?? resolve("tests/browser/node_modules/playwright/package.json").replace(/package\.json$/, ""));

const browser = await chromium.launch({ channel: "msedge", args: ["--use-angle=d3d11", "--enable-gpu-rasterization", "--ignore-gpu-blocklist"] });
const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1, reducedMotion: "no-preference" });
page.on("pageerror", error => console.error("page:", String(error)));
await page.goto(`${base}/?scene=cinematic&film=1`, { waitUntil: "networkidle" });
// Only the stage, full frame: no header, copy, request card or fades.
await page.addStyleTag({ content: `[class*=stage]{position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;z-index:9999!important;transform:none!important;opacity:1!important}
  [class*=stage]::after,[class*=stage]::before{display:none!important} body{overflow:hidden!important}` });
await page.waitForFunction(() => Boolean(window.__kemasanFilm), null, { timeout: 60_000 });

const result = await page.evaluate(async ({ width, height, fps, samples }) => {
  const { FILM_SECONDS, progressAt } = window.__kemasanFilmTimeline;
  const film = window.__kemasanFilm, accumulate = document.createElement("canvas"); accumulate.width = width; accumulate.height = height;
  const context = accumulate.getContext("2d", { alpha: false });
  const config = { codec: "vp09.00.10.08", width, height, bitrate: Math.round(width * height * 1.9), framerate: fps, bitrateMode: "variable", latencyMode: "quality" };
  if (!(await VideoEncoder.isConfigSupported(config)).supported) throw new Error("VP9 encoding is not supported by this browser");
  const chunks = [];
  const encoder = new VideoEncoder({ output: chunk => { const data = new Uint8Array(chunk.byteLength); chunk.copyTo(data); chunks.push({ key: chunk.type === "key", time: chunk.timestamp / 1000, data }); }, error: error => { throw error; } });
  encoder.configure(config);
  const frames = Math.round(FILM_SECONDS * fps); let posterData = "";
  for (let index = 0; index < frames; index++) {
    // A 180-degree shutter: sub-frames spread over the first half of the frame interval, averaged.
    for (let sample = 0; sample < samples; sample++) {
      const seconds = (index + (sample / samples) * 0.5) / fps, canvas = film.frame(progressAt(seconds), seconds);
      context.globalAlpha = 1 / (sample + 1); context.drawImage(canvas, 0, 0, width, height);
    }
    context.globalAlpha = 1;
    if (index === Math.round(1.5 * fps)) posterData = accumulate.toDataURL("image/jpeg", 0.86);
    const frame = new VideoFrame(accumulate, { timestamp: Math.round(index * 1e6 / fps), duration: Math.round(1e6 / fps) });
    encoder.encode(frame, { keyFrame: index % (fps * 2) === 0 }); frame.close();
    while (encoder.encodeQueueSize > 3) await new Promise(next => setTimeout(next, 1));
  }
  await encoder.flush(); encoder.close();

  // Minimal WebM (Matroska) writer: EBML header, Segment with Info and one VP9 track, a Cluster per keyframe group.
  const bytes = []; const push = array => bytes.push(array);
  const id = value => { const out = []; for (let v = value; v > 0; v = Math.floor(v / 256)) out.unshift(v % 256); return Uint8Array.from(out); };
  const size = length => { let width = 1; while (length >= 2 ** (7 * width) - 1) width++; const out = new Uint8Array(width); let v = length;
    for (let i = width - 1; i >= 0; i--) { out[i] = v % 256; v = Math.floor(v / 256); } out[0] |= 1 << (8 - width); return out; };
  const join = parts => { const total = parts.reduce((sum, part) => sum + part.length, 0), out = new Uint8Array(total); let at = 0; for (const part of parts) { out.set(part, at); at += part.length; } return out; };
  const element = (key, ...children) => { const body = join(children); return join([id(key), size(body.length), body]); };
  const uint = (key, value) => { const out = []; let v = value; do { out.unshift(v % 256); v = Math.floor(v / 256); } while (v > 0); return element(key, Uint8Array.from(out)); };
  const text = (key, value) => element(key, new TextEncoder().encode(value));
  const float = (key, value) => { const out = new Uint8Array(8); new DataView(out.buffer).setFloat64(0, value); return element(key, out); };
  const clusters = []; let current = null;
  for (const chunk of chunks) {
    if (chunk.key || !current) { current = { start: Math.round(chunk.time), blocks: [] }; clusters.push(current); }
    const relative = Math.round(chunk.time) - current.start, header = new Uint8Array(4);
    header[0] = 0x81; new DataView(header.buffer).setInt16(1, relative); header[3] = chunk.key ? 0x80 : 0;
    current.blocks.push(element(0xA3, header, chunk.data));
  }
  const ebml = element(0x1A45DFA3, uint(0x4286, 1), uint(0x42F7, 1), uint(0x42F2, 4), uint(0x42F3, 8), text(0x4282, "webm"), uint(0x4287, 4), uint(0x4285, 2));
  const info = element(0x1549A966, uint(0x2AD7B1, 1000000), text(0x4D80, "unit-toko-film"), text(0x5741, "unit-toko-film"), float(0x4489, FILM_SECONDS * 1000));
  const tracks = element(0x1654AE6B, element(0xAE, uint(0xD7, 1), uint(0x73C5, 1), uint(0x83, 1), text(0x86, "V_VP9"), element(0xE0, uint(0xB0, width), uint(0xBA, height))));
  const segment = element(0x18538067, info, tracks, ...clusters.map(cluster => element(0x1F43B675, uint(0xE7, cluster.start), ...cluster.blocks)));
  push(ebml); push(segment);
  const webm = join(bytes); let binary = ""; for (let i = 0; i < webm.length; i += 0x8000) binary += String.fromCharCode(...webm.subarray(i, i + 0x8000));
  return { webm: btoa(binary), poster: posterData.split(",")[1], frames, chunks: chunks.length };
}, { width, height, fps, samples });

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, Buffer.from(result.webm, "base64")); writeFileSync(poster, Buffer.from(result.poster, "base64"));
console.log(`film: ${out} (${result.frames} frames, ${result.chunks} chunks, ${(Buffer.byteLength(result.webm, "base64") / 1e6).toFixed(2)} MB); poster: ${poster}`);
await browser.close();
