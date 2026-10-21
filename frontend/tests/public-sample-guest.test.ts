import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { GuestProductCard, GuestSampleBanner, GuestSampleEmpty, GuestUnavailable, MemberCatalogNote, memberRedirect, signedInEquivalent }
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
  const html = renderToStaticMarkup(createElement(GuestSampleBanner));
  assert.match(html, /Contoh barang\. Masuk untuk melihat seluruh katalog dan harga cabang Anda\./);
  assert.ok(html.includes('href="' + signIn('/shop').replace(/&/g, '&amp;') + '"'));
});

test('with no sample chosen the page says so and offers sign-in', () => {
  const html = renderToStaticMarkup(createElement(GuestSampleEmpty));
  assert.match(html, /Contoh katalog belum dipilih/);
  assert.ok(html.includes('href="' + signIn('/shop') + '"'));
  assert.doesNotMatch(html, /Katalog publik belum dibuka/);
});

test('G-024: an unknown or non-sample product shows one neutral not-available state', () => {
  const html = renderToStaticMarkup(createElement(GuestUnavailable));
  assert.match(html, /Barang ini tidak tersedia untuk tamu\./);
  assert.ok(html.includes('href="/katalog"'));
  assert.ok(html.includes('href="' + signIn('/shop') + '"'));
  assert.doesNotMatch(html, /khusus untuk akun cabang/);
  // The state takes no id at all, so unknown and non-sample ids cannot render differently.
  assert.equal(GuestUnavailable.length, 0);
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
  assert.equal(signedInEquivalent(product), '/shop/' + product.packages[0].publicId);
  assert.equal(signedInEquivalent(product, product.packages[1].publicId), '/shop/' + product.packages[1].publicId);
  assert.equal(signedInEquivalent(product, product.publicId), '/shop/' + product.packages[0].publicId);
});

test('G-029: only signed-in visitors who may read the organisation catalog are sent to it', () => {
  const member = { authenticated: true, account: { userId: '7' } };
  assert.equal(memberRedirect(member, ['catalog.read'], '/shop'), '/shop');
  assert.equal(memberRedirect(member, ['catalog.public.approve'], '/shop'), null);
  assert.equal(memberRedirect({ authenticated: false, account: null }, ['catalog.read'], '/shop'), null);
  const note = renderToStaticMarkup(createElement(MemberCatalogNote));
  assert.match(note, /Akun Anda tidak mencakup katalog organisasi/);
});
