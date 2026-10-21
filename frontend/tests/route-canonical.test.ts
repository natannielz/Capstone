import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canonicalRedirect } from '../src/modules/identity/route-canonical';

const go = (url: string) => { const parsed = new URL(url, 'https://unit-toko.test'); return canonicalRedirect(parsed.pathname, parsed.searchParams); };
const signIn = (returnPath: string) => '/identity/login?returnPath=' + encodeURIComponent(returnPath);
const product = '/shop/00000065-0000-4000-8000-000000000101';

test('every legacy page maps to its canonical page and keeps its query', () => {
  assert.equal(go('/account'), '/identity/account');
  assert.equal(go('/account?operation=catalog.manage'), '/identity/account?operation=catalog.manage');
  assert.equal(go('/account/orders'), '/ordering/orders');
  assert.equal(go('/account/orders?status=open'), '/ordering/orders?status=open');
  assert.equal(go('/checkout'), '/ordering/checkout');
  assert.equal(go('/checkout?draft=7'), '/ordering/checkout?draft=7');
  assert.equal(go('/catalog/browse'), '/shop');
  assert.equal(go('/catalog/browse?q=kopi&kategori=pantry'), '/shop?q=kopi&kategori=pantry');
});

test('a legacy order link opens that order on the order page (G-009)', () => {
  assert.equal(go('/account/orders/41'), '/ordering/orders?order=41');
  assert.equal(go('/account/orders/9223372036854775807'), '/ordering/orders?order=9223372036854775807');
  // An old non-numeric id names no current order: the list opens instead.
  assert.equal(go('/account/orders/order_old'), '/ordering/orders');
});

test('every old sign-in door opens the one sign-in page and keeps where the person was going (D2, G-008)', () => {
  for (const door of ['/login', '/customer/login']) {
    assert.equal(go(door), '/identity/login');
    assert.equal(go(door + '?loggedOut=1'), '/identity/login?loggedOut=1');
    assert.equal(go(door + '?returnPath=%2Fcart'), signIn('/cart'));
    assert.equal(go(door + '?next=/workspace'), signIn('/workspace'));
    assert.equal(go(door + '?next=' + product), signIn(product));
    assert.equal(go(door + '?next=' + encodeURIComponent('/ordering/checkout?draft=7')), signIn('/ordering/checkout?draft=7'));
    assert.equal(go(door + '?next=/account'), signIn('/identity/account'));
    assert.equal(go(door + '?next=/checkout'), signIn('/ordering/checkout'));
    assert.equal(go(door + '?next=/account/orders/41'), signIn('/ordering/orders?order=41'));
    assert.equal(go(door + '?next=/catalog/browse'), signIn('/shop'));
    // Unknown or outside destinations are dropped; the person still reaches the sign-in page.
    for (const forged of ['https://example.com', '//example.com', '/\\example.com', '/api/state', '/auth/login', '/shop/../api/state', '/shop/legacy-product', '/' + 'a'.repeat(2048)])
      assert.equal(go(door + '?next=' + encodeURIComponent(forged)), '/identity/login', forged);
  }
  assert.equal(go('/staff/login'), signIn('/workspace'));
  assert.equal(go('/staff/login?returnPath=%2Fbilling%2Finvoices'), signIn('/billing/invoices'));
  assert.equal(go('/staff/login?next=https%3A%2F%2Fexample.com'), signIn('/workspace'));
});

test('canonical pages are never redirected', () => {
  for (const path of ['/', '/identity/login', '/identity/account', '/ordering/orders', '/ordering/checkout', '/shop', product, '/katalog', '/workspace', '/cart', '/billing/invoices'])
    assert.equal(go(path), null, path);
});

test('the order page reads which order an address asks it to open (G-009)', async () => {
  const { requestedOrderId, orderDetailHref } = await import('../src/modules/ordering/order-links');
  assert.equal(requestedOrderId(new URL(orderDetailHref('41'), 'https://unit-toko.test').search), '41');
  assert.equal(requestedOrderId('?order=9223372036854775807'), '9223372036854775807');
  for (const search of ['', '?order=', '?order=0', '?order=41x', '?order=9223372036854775808', '?order=-1']) assert.equal(requestedOrderId(search), null, search);
});

test('no page links a legacy address; links go straight to the canonical page (G-023)', async () => {
  const { readdirSync, readFileSync, statSync } = await import('node:fs');
  const { join } = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const root = fileURLToPath(new URL('../src', import.meta.url)), offenders: string[] = [];
  const legacy = /(?:href=\{?\s*|router\.(?:push|replace)\(\s*|Link href=)["'`](\/(?:catalog\/browse|account(?:\/orders)?|checkout|login|customer\/login|staff\/login))(?=["'`?#/])/g;
  const walk = (directory: string) => {
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (/\.(tsx?|mjs)$/.test(name)) for (const match of readFileSync(path, 'utf8').matchAll(legacy)) offenders.push(path.slice(root.length) + ': ' + match[1]);
    }
  };
  walk(root);
  assert.deepEqual(offenders, []);
});
