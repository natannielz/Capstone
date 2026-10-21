// Screenshot and accessibility harness for the loopback review stack (start-local-pilot.ps1 -Profile review).
//   node frontend/scripts/review/capture.mjs --persona <name|all> --routes <group|all|/path,/path> --run <label>
//        [--out <folder>] [--reachable-only] [--viewports 1280,390] [--origin https://localhost:8443]
// Writes <out>/<persona>/<route>@<width>.png plus <out>/<persona>/axe.json and metrics.json (default out:
// <repository>/tmp/website-ready/screens/<label>). Personas sign in with the stack's minted session files
// (<repository>/tmp/website-ready/sessions/<persona>.json); tamu has none. Session contents are never printed.
import { createRequire } from 'node:module';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repository = resolve(here, '../../..');
const PLAYWRIGHT = 'C:/Users/User/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/package.json';
const AXE = resolve(repository, 'frontend/node_modules/axe-core/axe.min.js');
const API_BUDGET = 200; // of the API's 240 requests per minute per IP, shared by everything on this machine's loopback

function parseArgs(argv) {
  const options = { viewports: '1280,390', reachableOnly: false };
  for (let index = 0; index < argv.length; index++) {
    const key = argv[index];
    if (key === '--reachable-only') { options.reachableOnly = true; continue; }
    const value = argv[index + 1];
    if (!key.startsWith('--') || value === undefined || value.startsWith('--')) throw new Error('Unexpected argument ' + key);
    options[key.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value; index++;
  }
  for (const required of ['persona', 'routes', 'run']) if (!options[required]) throw new Error('Missing --' + required);
  if (!/^[A-Za-z0-9._-]{1,60}$/.test(options.run)) throw new Error('--run is a short label: letters, digits, dot, dash, underscore');
  return options;
}

const options = parseArgs(process.argv.slice(2));
const sessions = resolve(repository, 'tmp/website-ready/sessions');
const manifest = JSON.parse(readFileSync(resolve(here, 'routes.json'), 'utf8'));
const index = existsSync(resolve(sessions, 'personas.json')) ? JSON.parse(readFileSync(resolve(sessions, 'personas.json'), 'utf8')) : null;
const origin = new URL(options.origin ?? index?.origin ?? 'https://localhost:8443');
if (!['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname) || origin.protocol !== 'https:') throw new Error('The harness only opens the loopback review stack.');
const knownPersonas = manifest.personas;
const personas = options.persona === 'all' ? knownPersonas : options.persona.split(',');
for (const persona of personas) if (!knownPersonas.includes(persona)) throw new Error('Unknown persona ' + persona + ' (routes.json lists ' + knownPersonas.join(', ') + ')');
const routes = options.routes === 'all' ? manifest.routes
  : options.routes.startsWith('/') ? options.routes.split(',').map(path => manifest.routes.find(route => route.path === path) ?? (() => { throw new Error('Unknown route ' + path); })())
  : manifest.routes.filter(route => route.group === options.routes);
if (routes.length === 0) throw new Error('No routes in group ' + options.routes + ' (groups: ' + [...new Set(manifest.routes.map(route => route.group))].join(', ') + ')');
const viewports = options.viewports.split(',').map(Number);
if (viewports.some(width => !Number.isInteger(width) || width < 320 || width > 2560)) throw new Error('--viewports takes widths such as 1280,390');
const out = resolve(options.out ?? resolve(repository, 'tmp/website-ready/screens', options.run));

const load = createRequire(PLAYWRIGHT);
const { chromium } = load('playwright');
const apiTimes = [];
async function pace(budget) {
  for (;;) {
    const now = Date.now(); while (apiTimes.length && apiTimes[0] <= now - 60000) apiTimes.shift();
    if (apiTimes.length + budget <= API_BUDGET) return;
    await new Promise(done => setTimeout(done, apiTimes[0] + 60000 - now + 50));
  }
}
const slug = path => path === '/' ? 'root' : path.slice(1).replace(/\[([^\]]+)\]/g, '$1').replace(/\//g, '_');

async function settle(page) {
  await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('[aria-busy="true"]'), null, { timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(300);
}

async function measure(page) {
  return page.evaluate(() => {
    const visible = element => { const box = element.getBoundingClientRect(); const style = getComputedStyle(element);
      return box.width > 0 && box.height > 0 && style.visibility !== 'hidden' && style.display !== 'none'; };
    const text = value => (value ?? '').replace(/\s+/g, ' ').trim().slice(0, 80);
    const name = element => text(element.getAttribute('aria-label') || (element.labels && element.labels[0]?.textContent) || element.textContent ||
      element.getAttribute('placeholder') || element.getAttribute('name') || element.getAttribute('title'));
    const controls = [...document.querySelectorAll('button, input:not([type=hidden]), select, textarea, [role="button"]')].filter(visible).map(element => {
      const box = element.getBoundingClientRect();
      return { tag: element.tagName.toLowerCase(), type: element.getAttribute('type'), name: name(element), height: Math.round(box.height), width: Math.round(box.width) };
    });
    const headings = [...document.querySelectorAll('h1, h2, h3, h4, h5, h6')].filter(visible).map(element => ({ level: Number(element.tagName[1]), text: text(element.textContent) }));
    return { scrollWidth: document.documentElement.scrollWidth, innerWidth: window.innerWidth, overflowX: document.documentElement.scrollWidth > window.innerWidth,
      scrollHeight: document.documentElement.scrollHeight, title: document.title, controls, headings };
  });
}

async function resolvePath(context, route) {
  if (!route.sample) return route.path;
  if (route.sample.path) return route.sample.path;
  const page = await context.newPage();
  try {
    await pace(30); await page.goto(origin.origin + route.sample.from, { waitUntil: 'load', timeout: 30000 }); await settle(page);
    const pattern = new RegExp(route.sample.link);
    const hrefs = await page.locator('a[href]').evaluateAll(links => links.map(link => link.getAttribute('href')));
    return hrefs.filter(Boolean).map(href => new URL(href, origin).pathname).find(path => pattern.test(path)) ?? null;
  } finally { await page.close(); }
}

async function captureRoute(browser, persona, route, width, state, directory) {
  const mobile = width < 768;
  const context = await browser.newContext({ viewport: { width, height: mobile ? 844 : 900 }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 1,
    ignoreHTTPSErrors: true, bypassCSP: true, locale: 'id-ID', timezoneId: 'Asia/Jakarta', storageState: state, serviceWorkers: 'block' });
  const api = []; const consoleErrors = []; let rateLimited = false;
  context.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/v1') || new URL(request.url()).pathname.startsWith('/auth/')) apiTimes.push(Date.now()); });
  context.on('response', response => { const path = new URL(response.url()).pathname;
    if (path.startsWith('/api/v1') && response.status() >= 400) api.push({ status: response.status(), method: response.request().method(), path });
    if (response.status() === 429) rateLimited = true; });
  try {
    const path = await resolvePath(context, route);
    if (!path) return { route: route.path, width, skipped: 'no sample record found on ' + route.sample.from };
    const page = await context.newPage();
    page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text().slice(0, 200)); });
    page.on('pageerror', error => consoleErrors.push(('pageerror ' + error.message).slice(0, 200)));
    await pace(40);
    const started = Date.now();
    const response = await page.goto(origin.origin + path, { waitUntil: 'load', timeout: 30000 });
    await settle(page);
    const file = resolve(directory, slug(route.path) + '@' + width + '.png');
    await page.screenshot({ path: file, fullPage: true, animations: 'disabled', caret: 'hide' });
    const metrics = await measure(page);
    await page.addScriptTag({ path: AXE });
    const axe = await page.evaluate(async () => {
      const result = await window.axe.run(document, { resultTypes: ['violations'] });
      return result.violations.map(item => ({ id: item.id, impact: item.impact, help: item.help, helpUrl: item.helpUrl, count: item.nodes.length,
        targets: item.nodes.slice(0, 5).map(node => node.target.join(' ')) }));
    });
    const final = new URL(page.url());
    return { route: route.path, path, width, status: response?.status() ?? null, finalPath: final.pathname, redirected: final.pathname !== path,
      loadMs: Date.now() - started, screenshot: file, ...metrics, apiErrors: api, consoleErrors, rateLimited, axe };
  } finally { await context.close(); }
}

mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--no-first-run', '--disable-sync', '--no-proxy-server'] });
const run = { run: options.run, origin: origin.origin, startedAt: new Date().toISOString(), personas, routes: routes.map(route => route.path), viewports };
let failures = 0;
try {
  for (const persona of personas) {
    const directory = resolve(out, persona); mkdirSync(directory, { recursive: true });
    const metrics = {}, axe = {};
    const selected = options.reachableOnly ? routes.filter(route => route.personas.includes(persona)) : routes;
    for (const route of selected) {
      metrics[route.path] = { expectedReachable: route.personas.includes(persona) }; axe[route.path] = {};
      for (const width of viewports) {
        let state;
        if (persona !== 'tamu') {
          const file = resolve(sessions, persona + '.json');
          if (!existsSync(file)) throw new Error('No session for ' + persona + '; start the review stack (start-local-pilot.ps1 -Profile review) first.');
          state = JSON.parse(readFileSync(file, 'utf8')); // read per page: the stack refreshes sessions every few minutes
        }
        let result;
        for (let attempt = 1; attempt <= 2; attempt++) {
          try {
            result = await captureRoute(browser, persona, route, width, state, directory);
            if (!result.rateLimited || attempt === 2) break;
            await new Promise(done => setTimeout(done, 61000));
          } catch (error) {
            result = { route: route.path, width, error: String(error?.message ?? error).split('\n')[0].slice(0, 300) };
            if (attempt === 2) break;
          }
        }
        if (result.error) failures++;
        const { axe: violations, ...rest } = result;
        metrics[route.path][width] = rest; axe[route.path][width] = violations ?? null;
        console.log([persona, route.path, width, result.error ? 'ERROR ' + result.error : result.skipped ? 'SKIPPED ' + result.skipped :
          `${result.status} ${result.finalPath} axe=${violations?.length ?? 0} overflow=${result.overflowX} api4xx=${result.apiErrors.length}${result.rateLimited ? ' RATE-LIMITED' : ''}`].join(' '));
      }
      writeFileSync(resolve(directory, 'metrics.json'), JSON.stringify(metrics, null, 2));
      writeFileSync(resolve(directory, 'axe.json'), JSON.stringify(axe, null, 2));
    }
  }
} finally {
  await browser.close();
  writeFileSync(resolve(out, 'run.json'), JSON.stringify({ ...run, finishedAt: new Date().toISOString(), failures }, null, 2));
}
console.log('CAPTURE_DONE ' + out + ' failures=' + failures);
process.exit(failures ? 1 : 0);
