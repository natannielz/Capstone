import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { requiresSession, signInRedirect } from '../src/modules/identity/route-canonical';

const signIn = (returnPath: string) => '/identity/login?returnPath=' + encodeURIComponent(returnPath);

/** Every page route under src/app, as its URL path ([segments] kept as written). */
function pageRoutes(): string[] {
  const root = fileURLToPath(new URL('../src/app', import.meta.url)), routes: string[] = [];
  const walk = (directory: string) => {
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (name === 'page.tsx') routes.push('/' + relative(root, directory).split(sep).filter(Boolean).join('/'));
    }
  };
  walk(root);
  return routes.map(route => route === '/' ? '/' : route.replace(/\/$/, ''));
}

const guestPages = new Set(['/', '/katalog', '/katalog/[publicId]', '/shop', '/shop/[product]', '/identity/login']);
const legacyPages = new Set(['/login', '/customer/login', '/staff/login', '/account', '/account/orders', '/account/orders/[id]', '/checkout', '/catalog/browse']);

test('a visitor without a session never sees the shell of a signed-in page: every such page sends them to sign-in (G-012)', () => {
  const routes = pageRoutes();
  assert.ok(routes.length >= 81, 'page discovery found ' + routes.length);
  for (const route of routes) {
    if (guestPages.has(route) || legacyPages.has(route)) continue;
    assert.equal(requiresSession(route), true, route);
    assert.equal(signInRedirect(route, '', false), signIn(route), route);
    assert.equal(signInRedirect(route, '', true), null, route + ' with a session cookie');
  }
});

test('guest pages, sign-in, assets and the API are left alone', () => {
  for (const path of ['/', '/katalog', '/katalog/00000065-0000-4000-8000-000000000101', '/shop', '/shop/00000065-0000-4000-8000-000000000101', '/identity/login',
    '/api/v1/identity/session', '/auth/login', '/auth/callback', '/_next/static/chunks/app.js', '/images/editorial/hero.png', '/fonts/dm-sans-variable.ttf',
    '/favicon.svg', '/icon.svg', '/panduan.html', '/catalogue', '/orderingx', '/does-not-exist'])
    assert.equal(signInRedirect(path, '', false), null, path);
});

test('the sign-in redirect keeps the page and its query, and drops a query the server would refuse', () => {
  assert.equal(signInRedirect('/ordering/checkout', '?draft=7', false), signIn('/ordering/checkout?draft=7'));
  assert.equal(signInRedirect('/ordering/requests', '?package=00000065-0000-4000-8000-000000000101', false), signIn('/ordering/requests?package=00000065-0000-4000-8000-000000000101'));
  assert.equal(signInRedirect('/billing/invoices', '?next=https://outside.test', false), signIn('/billing/invoices'));
  assert.equal(signInRedirect('/inventory/replenishment', '', false), signIn('/inventory/replenishment'));
});

test('the proxy redirects legacy pages and session-less visits before any page renders, and never caches the answer', async () => {
  // Build the real proxy file next to this test (Next's own module names need their .js extension under plain Node).
  const { build } = await import('esbuild');
  const outfile = fileURLToPath(new URL('./proxy-under-test.mjs', import.meta.url));
  await build({ entryPoints: [fileURLToPath(new URL('../src/proxy.ts', import.meta.url))], outfile, bundle: true, platform: 'node', format: 'esm', target: 'node24',
    packages: 'external', alias: { 'next/server': 'next/server.js' }, logLevel: 'error' });
  const { proxy } = await import(new URL('./proxy-under-test.mjs', import.meta.url).href) as typeof import('../src/proxy');
  const { NextRequest } = await import('next/server.js');
  const location = (url: string, cookie?: string) => {
    const response = proxy(new NextRequest('https://unit-toko.test' + url, cookie ? { headers: { cookie } } : undefined));
    const target = response.headers.get('location');
    return target === null ? null : { status: response.status, cache: response.headers.get('cache-control'), to: new URL(target).pathname + new URL(target).search };
  };
  assert.deepEqual(location('/billing/invoices'), { status: 307, cache: 'no-store', to: signIn('/billing/invoices') });
  assert.deepEqual(location('/ordering/checkout?draft=7'), { status: 307, cache: 'no-store', to: signIn('/ordering/checkout?draft=7') });
  assert.equal(location('/billing/invoices', '__Host-ut_session=opaque'), null);
  assert.equal(location('/katalog'), null);
  assert.deepEqual(location('/staff/login', '__Host-ut_session=opaque'), { status: 307, cache: 'no-store', to: signIn('/workspace') });
  assert.deepEqual(location('/account/orders/41'), { status: 307, cache: 'no-store', to: '/ordering/orders?order=41' });
});
