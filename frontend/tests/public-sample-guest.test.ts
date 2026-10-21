import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { GuestProductCard, GuestSampleBanner, GuestSampleEmpty, GuestUnavailable, MemberCatalogNote, guestSignInHref, memberRedirect, signedInEquivalent }
  from '../src/modules/catalog/PublicCatalogParts';
import { parsePublicCatalogItem } from '../src/modules/catalog/public-catalog-client';

const product = parsePublicCatalogItem({ publicId: '00000065-0000-4000-8000-000000000201', name: 'Air mineral 600 ml', description: 'Air mineral dalam botol.',
  categoryCode: 'AIR-MINUMAN', categoryName: 'Air dan minuman', packages: [
    { publicId: '00000065-0000-4000-8000-000000000101', code: 'AIR-600-K24', packageLabel: 'Karton isi 24 botol', packageKind: 'sealed', baseUnitCode: 'botol',
      baseUnitsPerPackage: 24, attributes: [], minimumOrderPackages: 1, orderIncrementPackages: 1 },
    { publicId: '00000065-0000-4000-8000-000000000102', code: 'AIR-600-K12', packageLabel: 'Pak', packageKind: 'sealed', baseUnitCode: 'botol',
      baseUnitsPerPackage: 12, attributes: [], minimumOrderPackages: 2, orderIncrementPackages: 1 }] });
const signIn = (path: string) => '/identity/login?returnPath=' + encodeURIComponent(path);

test('guests are told these are samples, and the catalog with prices is behind sign-in', () => {
  const html = renderToStaticMarkup(createElement(GuestSampleBanner, { target: '/shop?q=kopi' }));
  assert.match(html, /Contoh barang\. Masuk untuk melihat seluruh katalog dan harga cabang Anda\./);
  assert.ok(html.includes('href="' + signIn('/shop').replace(/&/g, '&amp;') + '"'));
});

test('with no sample chosen the page says so and offers sign-in', () => {
  const html = renderToStaticMarkup(createElement(GuestSampleEmpty, { target: '/shop' }));
  assert.match(html, /Contoh katalog belum dipilih/);
  assert.ok(html.includes('href="' + signIn('/shop') + '"'));
  assert.doesNotMatch(html, /Katalog publik belum dibuka/);
});

test('G-024: an unknown or non-sample product shows one neutral not-available state', () => {
  const id = product.packages[0].publicId, other = '00000065-0000-4000-8000-000000000999';
  const html = renderToStaticMarkup(createElement(GuestUnavailable, { publicId: id }));
  assert.match(html, /Barang ini tidak tersedia untuk tamu\./);
  assert.ok(html.includes('href="/katalog"'));
  // Signing in returns to the same address on the signed-in side, so a member following a shared link lands on it.
  assert.ok(html.includes('href="' + signIn('/shop/' + id) + '"'));
  assert.doesNotMatch(html, /khusus untuk akun cabang/);
  // Only the id from the address appears, so an unknown and a non-sample product render the same state.
  assert.equal(html.replaceAll(id, 'ID'), renderToStaticMarkup(createElement(GuestUnavailable, { publicId: other })).replaceAll(other, 'ID'));
});

test('G-004: one card per sample product, naming its packages and never a price', () => {
  const html = renderToStaticMarkup(createElement(GuestProductCard, { item: product }));
  assert.equal((html.match(/href="\/katalog\//g) ?? []).length, 1);
  assert.ok(html.includes('href="/katalog/' + product.publicId + '"'));
  assert.match(html, /2 kemasan/); assert.match(html, /Air mineral 600 ml/);
  assert.doesNotMatch(html, /Rp|harga\s*:/i);
});

test('G-025: every sign-in link returns to the signed-in equivalent of the page the guest is on', () => {
  assert.equal(signedInEquivalent(null), '/shop');
  // The catalog list keeps its search, category and order (never the page number).
  assert.equal(signedInEquivalent(null, undefined, { search: 'kopi bubuk', category: 'PANTRY', sort: 'name', offset: 48 }), '/shop?q=kopi+bubuk&kategori=PANTRY&urut=nama');
  // A missing or non-sample product keeps the id from the address.
  assert.equal(signedInEquivalent(null, '00000065-0000-4000-8000-000000000999'), '/shop/00000065-0000-4000-8000-000000000999');
  assert.equal(signedInEquivalent(product), '/shop/' + product.packages[0].publicId);
  assert.equal(signedInEquivalent(product, product.packages[1].publicId), '/shop/' + product.packages[1].publicId);
  assert.equal(signedInEquivalent(product, product.publicId), '/shop/' + product.packages[0].publicId);
  // Sign-in itself accepts only canonical paths (the identity layer refuses a query), so the link keeps the page and the
  // member redirect keeps the query.
  assert.equal(guestSignInHref('/shop?q=kopi&kategori=PANTRY'), signIn('/shop'));
  assert.equal(guestSignInHref('/shop/' + product.packages[1].publicId), signIn('/shop/' + product.packages[1].publicId));
});

test('G-029: only signed-in visitors who may read the organisation catalog are sent to it', () => {
  const member = { authenticated: true, account: { userId: '7' } };
  assert.equal(memberRedirect(member, ['catalog.read'], '/shop'), '/shop');
  assert.equal(memberRedirect(member, ['catalog.public.approve'], '/shop'), null);
  assert.equal(memberRedirect({ authenticated: false, account: null }, ['catalog.read'], '/shop'), null);
  const note = renderToStaticMarkup(createElement(MemberCatalogNote));
  assert.match(note, /Akun Anda tidak mencakup katalog organisasi/);
});
