// Run from website: node --env-file=../.tools/v7-qa.env scripts/smoke-access-v8.mjs
// Login/logout only, against the explicitly isolated local QA server. No business mutations.
import assert from "node:assert/strict";
import {assertLocalQaEnvironment, qaPassword} from "./customer-qa-fixture.mjs";

const base = assertLocalQaEnvironment();
const checks = [];
const sessions = [];
const customers = ["/shop", "/shop/kopi", "/cart", "/checkout", "/account", "/account/orders"];
const staff = ["pic-a", "pic-b", "kepala", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin"];
function request(path, cookie, data) {
  return fetch(base + path, {
    method: data === undefined ? "GET" : "POST", redirect: "manual",
    headers: {origin: base, ...(cookie ? {cookie} : {}), ...(data === undefined ? {} : {"content-type": "application/json"})},
    ...(data === undefined ? {} : {body: JSON.stringify(data)}), signal: AbortSignal.timeout(60000),
  });
}
async function redirected(path, destination, cookie) {
  const response = await request(path, cookie);
  const location = response.headers.get("location");
  if ([307, 308].includes(response.status)) {
    assert.equal(new URL(location, base).pathname + new URL(location, base).search, destination, path);
  } else {
    const html = await response.text();
    const refresh = html.match(/<meta\b[^>]*id="__next-page-redirect"[^>]*content="[^"]*url=([^"]+)"/);
    assert.equal(response.status, 200, path);
    assert.equal(refresh?.[1].replaceAll("&amp;", "&"), destination, `${path} must send a server redirect`);
  }
}
async function page(path, cookie, expected) {
  const response = await request(path, cookie);
  assert.equal(response.status, 200, path);
  const html = await response.text();
  assert.doesNotMatch(html, /id="__next-page-redirect"/);
  if (expected) assert.match(html, expected, path);
}
async function login(id, portal) {
  const email = id === "customer-demo" ? "customer@unit-toko.demo" : `${id}@unit-toko.demo`;
  const response = await request("/api/auth/login", null, {email, password: qaPassword(id), portal});
  assert.equal(response.status, 200, `login ${id}`);
  const cookie = response.headers.get("set-cookie")?.split(";")[0];
  assert.ok(cookie); sessions.push(cookie); return cookie;
}
async function check(name, fn) { await fn(); checks.push(name); console.log(`PASS ${name}`); }

try {
  await check("guest landing and separate portals are available", async () => {
    await page("/", null, /href="\/staff\/login"/);
    await page("/customer/login", null, /Masuk pelanggan/);
    await page("/staff/login", null, /Masuk staf &amp; admin/);
    for (const path of customers.slice(0, 4)) await page(path);
    await redirected("/account/orders", "/customer/login?next=%2Faccount%2Forders");
    await redirected("/workspace?view=profile", "/staff/login?next=%2Fworkspace%3Fview%3Dprofile");
  });
  await check("legacy login preserves valid portal context and rejects unsafe destinations", async () => {
    await redirected("/login", "/staff/login");
    await redirected("/login?next=%2Fcheckout%3Fmode%3Dbuy", "/customer/login?next=%2Fcheckout%3Fmode%3Dbuy");
    await redirected("/login?next=%2Fworkspace%3Fview%3Dadmin", "/staff/login?next=%2Fworkspace%3Fview%3Dadmin");
    await redirected("/login?next=https%3A%2F%2Fevil.invalid", "/staff/login");
  });
  for (const id of staff) await check(`${id} remains in internal workspace at every customer route`, async () => {
    const cookie = await login(id, "staff");
    for (const path of customers) await redirected(path, "/workspace", cookie);
    await redirected("/customer/login?next=%2Fcheckout", "/workspace", cookie);
    await redirected("/staff/login?next=%2Fcheckout", "/workspace", cookie);
    await page("/workspace", cookie);
    await page("/", cookie);
  });
  await check("customer retains shopping pages and cannot enter staff workspace", async () => {
    const cookie = await login("customer-demo", "customer");
    for (const path of customers) await page(path, cookie);
    await redirected("/workspace", "/account/orders", cookie);
    await redirected("/staff/login?next=%2Fworkspace", "/shop", cookie);
    await redirected("/customer/login?next=%2Fcheckout", "/checkout", cookie);
    await redirected("/login?next=%2Faccount", "/account", cookie);
  });
  await check("wrong portal cannot establish a session", async () => {
    for (const [id, portal] of [["admin", "customer"], ["staf", "customer"], ["pic-a", "customer"], ["customer-demo", "staff"]]) {
      const email = id === "customer-demo" ? "customer@unit-toko.demo" : `${id}@unit-toko.demo`;
      const response = await request("/api/auth/login", null, {email, password: qaPassword(id), portal});
      assert.equal(response.status, 403, id); assert.equal(response.headers.get("set-cookie"), null);
    }
    const malformed = await request("/api/auth/login", null, {email: "customer@unit-toko.demo", password: qaPassword("customer-demo"), portal: "admin"});
    assert.equal(malformed.status, 400); assert.equal(malformed.headers.get("set-cookie"), null);
  });
  console.log(`ACCESS_V8_OK ${checks.length} groups; all 11 accounts; no business mutations`);
} finally {
  for (const cookie of sessions) await request("/api/auth/logout", cookie, {});
}
