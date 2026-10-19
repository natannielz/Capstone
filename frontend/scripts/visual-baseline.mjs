// Visual safety net for UI-foundation work: screenshots public routes at phone/tablet/desktop
// widths and checks the 360px no-horizontal-scroll rule. Usage (from frontend/):
//   node scripts/visual-baseline.mjs <label>        e.g. before | layered | final
// Serves the existing production build (no API needed; data regions show their unavailable state).
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { mkdirSync, writeFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";

const label = process.argv[2];
if (!label || !/^[a-z0-9-]{1,40}$/.test(label)) throw new Error("Pass a short lowercase label, e.g. before");
const playwrightPackage = process.env.UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE;
if (!playwrightPackage || !existsSync(playwrightPackage)) throw new Error("Set UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE to playwright/package.json");
const { chromium } = createRequire(playwrightPackage)("playwright");
const full = process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE === "full";
if (!existsSync(resolve(full ? ".next-full" : ".next", "BUILD_ID"))) throw new Error("Build the frontend first");

const port = 3017, origin = `http://127.0.0.1:${port}`;
const routes = ["/", "/shop", "/catalog/browse", "/identity/login"];
const widths = [360, 768, 1440];
const directory = resolve("..", "tmp", "visual", label);
mkdirSync(directory, { recursive: true });

const server = spawn(process.execPath, ["node_modules/next/dist/bin/next", "start", "-p", String(port), "-H", "127.0.0.1"], { stdio: ["ignore", "pipe", "pipe"], env: process.env });
const summary = { label, release: full ? "full" : "release1", at: new Date().toISOString(), shots: [], overflow: [], contrast: [], errors: [] };

// Runs in the page: visible text nodes whose colour is below WCAG AA against the nearest opaque background
// (4.5:1, or 3:1 for large text: ≥24px, or ≥18.66px bold). Gradients/images behind text are skipped.
function textContrastAudit() {
  const parse = value => { const m = value.match(/rgba?\(([^)]+)\)/); if (!m) return null; const [r, g, b, a = 1] = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return { r, g, b, a }; };
  const lum = ({ r, g, b }) => [r, g, b].map(v => v / 255).map(c => c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4).reduce((sum, c, i) => sum + c * [0.2126, 0.7152, 0.0722][i], 0);
  const ratio = (x, y) => { const [a, b] = [lum(x), lum(y)].sort((p, q) => q - p); return (a + 0.05) / (b + 0.05); };
  const background = element => { for (let node = element; node; node = node.parentElement) { const style = getComputedStyle(node); if (style.backgroundImage !== "none") return null; const color = parse(style.backgroundColor); if (color && color.a >= 0.95) return color; } return { r: 255, g: 255, b: 255, a: 1 }; };
  const findings = new Map();
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let text = walker.nextNode(); text; text = walker.nextNode()) {
    const element = text.parentElement; if (!element || !text.textContent.trim()) continue;
    const box = element.getBoundingClientRect(), style = getComputedStyle(element);
    if (!box.width || !box.height || style.visibility === "hidden" || Number(style.opacity) === 0) continue;
    const fg = parse(style.color), bg = background(element); if (!fg || !bg || fg.a < 0.95) continue;
    const size = parseFloat(style.fontSize), large = size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700);
    const value = ratio(fg, bg); if (value >= (large ? 3 : 4.5)) continue;
    const key = `${style.color} on ${`rgb(${bg.r}, ${bg.g}, ${bg.b})`}`;
    const entry = findings.get(key) ?? { colors: key, ratio: Math.round(value * 100) / 100, sample: text.textContent.trim().slice(0, 40), selector: element.tagName.toLowerCase() + (element.className && typeof element.className === "string" ? "." + element.className.trim().split(/\s+/).slice(0, 2).join(".") : ""), count: 0 };
    entry.count++; findings.set(key, entry);
  }
  return [...findings.values()];
}
try {
  const deadline = Date.now() + 60000;
  for (;;) {
    try { if ((await fetch(origin + "/", { signal: AbortSignal.timeout(2000) })).status < 500) break; } catch { /* starting */ }
    if (Date.now() > deadline) throw new Error("next start did not answer within 60s");
    await new Promise(done => setTimeout(done, 500));
  }
  const browser = await chromium.launch({ channel: process.env.UNITTOKO_BROWSER_CHANNEL || "msedge" });
  try {
    const page = await browser.newPage();
    page.on("pageerror", error => summary.errors.push(error.message));
    for (const route of routes) {
      for (const width of widths) {
        await page.setViewportSize({ width, height: 900 });
        // Without an API the data regions keep retrying and first-time image optimisation can stall "load",
        // so wait for the DOM, then a bounded settle for images and fonts.
        const name = (route === "/" ? "home" : route.slice(1).replaceAll("/", "_")) + "-" + width + ".png";
        try {
          await page.goto(origin + route, { waitUntil: "domcontentloaded", timeout: 45000 });
          await page.evaluate(() => Promise.race([document.fonts.ready, new Promise(done => setTimeout(done, 5000))]));
          await page.waitForTimeout(2500);
          await page.addStyleTag({ content: "*,*::before,*::after{animation:none!important;transition:none!important;caret-color:transparent!important}" });
          await page.screenshot({ path: resolve(directory, name), fullPage: true, timeout: 45000 });
          summary.shots.push(name);
          if (width === 360 && !(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))) summary.overflow.push(route);
          if (width === 1440) for (const finding of await page.evaluate(textContrastAudit)) summary.contrast.push({ route, ...finding });
        } catch (error) { summary.errors.push(`${name}: ${error.message.split("\n")[0]}`); }
      }
    }
  } finally { await browser.close(); }
} finally {
  server.kill();
  writeFileSync(resolve(directory, "summary.json"), JSON.stringify(summary, null, 2));
}
console.log(`${summary.shots.length} screenshots in ${directory}; no horizontal overflow at 360: ${summary.overflow.length === 0 ? "yes" : "NO " + summary.overflow.join(", ")}; text contrast findings: ${summary.contrast.length}`);
if (summary.overflow.length) process.exitCode = 1;
