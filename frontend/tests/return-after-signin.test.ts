import { test } from 'node:test';
import assert from 'node:assert/strict';
import { identityReturnPath, loginContinuation, loginFields } from '../src/modules/identity/identity-workflow';
import { returnTarget, sessionLoginHref } from '../src/components/workspace-kit/session-access-state';
import { signInRedirect } from '../src/modules/identity/route-canonical';

/** The whole round trip: the page's "Masuk kembali" link, the sign-in page reading it, and the form it posts to /auth/login. */
function afterSignIn(href: string): string {
  const returnPath = new URL(href, 'https://unit-toko.test').searchParams.get('returnPath');
  const fields = loginFields(identityReturnPath(returnPath), '', '');
  return fields.returnPath;
}
const expiredOn = (url: string, pagePath = new URL(url, 'https://unit-toko.test').pathname) => {
  const location = new URL(url, 'https://unit-toko.test');
  return sessionLoginHref(returnTarget(pagePath, { pathname: location.pathname, search: location.search }));
};

test('an expired session on the cart returns to the cart after sign-in', () => {
  assert.equal(afterSignIn(expiredOn('/cart')), '/cart');
});

test('an expired session mid-checkout returns to the same draft (G-011)', () => {
  assert.equal(afterSignIn(expiredOn('/ordering/checkout?draft=7')), '/ordering/checkout?draft=7');
});

test('an expired session on a staff form returns to the same form and record (G-011, G-007, F-011)', () => {
  assert.equal(afterSignIn(expiredOn('/ordering/substitutions?order=41')), '/ordering/substitutions?order=41');
  assert.equal(afterSignIn(expiredOn('/ordering/requests?package=00000065-0000-4000-8000-000000000101')), '/ordering/requests?package=00000065-0000-4000-8000-000000000101');
  assert.equal(afterSignIn(expiredOn('/inventory/replenishment')), '/inventory/replenishment');
  assert.equal(afterSignIn(expiredOn('/administration/accounts')), '/administration/accounts');
});

test('a guest stopped at the door of a page returns to that page and query', () => {
  assert.equal(afterSignIn(signInRedirect('/ordering/checkout', '?draft=7', false)!), '/ordering/checkout?draft=7');
  assert.equal(afterSignIn(signInRedirect('/billing/invoices', '', false)!), '/billing/invoices');
});

test('a page only keeps its own query, and only a query the server accepts', () => {
  // A page that reports a different path keeps its own path and nothing from the address bar.
  assert.equal(returnTarget('/ordering/checkout', { pathname: '/cart', search: '?draft=7' }), '/ordering/checkout');
  for (const search of ['?next=https://outside.test', '?returnPath=%2F%2Fevil.test', '?q=kopi+susu', '?a=1&b=2&c=3&d=4&e=5', '?draft=' + '7'.repeat(65), '?Draft=7', '?draft=7#x'])
    assert.equal(returnTarget('/ordering/checkout', { pathname: '/ordering/checkout', search }), '/ordering/checkout', search);
  for (const forged of ['/ordering/checkout?draft=https://outside.test', '/ordering/checkout?draft=7&', '/ordering/checkout?', '/auth/login?draft=7', '//example.test?draft=7'])
    assert.equal(identityReturnPath(forged), '/workspace', forged);
});

test('without a destination, sign-in leads home; an already signed-in visitor continues where asked (G-010, G-020)', () => {
  assert.equal(identityReturnPath(null), '/workspace');
  assert.deepEqual(loginContinuation(''), { returnPath: '/workspace', requested: false });
  assert.deepEqual(loginContinuation('?returnPath=%2Fcart'), { returnPath: '/cart', requested: true });
  assert.deepEqual(loginContinuation('?returnPath=' + encodeURIComponent('/ordering/checkout?draft=7')), { returnPath: '/ordering/checkout?draft=7', requested: true });
  assert.deepEqual(loginContinuation('?returnPath=https%3A%2F%2Fevil.test'), { returnPath: '/workspace', requested: false });
});
