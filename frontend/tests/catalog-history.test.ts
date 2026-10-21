import { test } from 'node:test';
import assert from 'node:assert/strict';
import { catalogHistoryUrl, guestCatalogHref, memberCatalogPath, normalizeCatalogHistorySearch, readCatalogHistoryQuery, type CatalogHistoryQuery } from '../src/modules/catalog/catalog-history';

test('a visitor without a session on the member catalog goes to the guest catalog in every release, keeping only the public list query (G-030)', () => {
  assert.equal(guestCatalogHref(undefined, ''), '/katalog');
  assert.equal(guestCatalogHref(undefined, '?q=kopi&kategori=PANTRY&urut=nama&token=private'), '/katalog?q=kopi&kategori=PANTRY&urut=nama');
  assert.equal(guestCatalogHref('00000065-0000-4000-8000-000000000101', '?q=kopi'), '/katalog/00000065-0000-4000-8000-000000000101');
});

test('signing in from the member catalog returns to the open product, never only to the list (G-030)', () => {
  assert.equal(memberCatalogPath(), '/shop');
  assert.equal(memberCatalogPath('00000065-0000-4000-8000-000000000101'), '/shop/00000065-0000-4000-8000-000000000101');
});

test('the legacy /catalog/browse route is never produced as a catalog address (G-023)', () => {
  assert.throws(() => catalogHistoryUrl('/catalog/browse' as '/shop', { search: '', offset: 0 }), TypeError);
});

test('catalog history keeps a bounded normalized search and rejects malformed Unicode and controls', () => {
  assert.equal(normalizeCatalogHistorySearch('  Cafe\u0301 pena  '), 'Café pena');
  assert.equal(normalizeCatalogHistorySearch('🖊️'.repeat(42)), '🖊️'.repeat(42));
  assert.equal(normalizeCatalogHistorySearch('a'.repeat(128)), 'a'.repeat(128));
  for (const value of ['a'.repeat(129), '🖊️'.repeat(43), '\u0000pena', 'pena\n', '\tpena', 'pena\u007f', 'pena\u0085', '\uD800', '\uDC00', '\uFFFD']) {
    assert.equal(normalizeCatalogHistorySearch(value), '', JSON.stringify(value));
  }
  assert.deepEqual(readCatalogHistoryQuery('?q=%ED%A0%80&page=2'), { search: '', offset: 25 });
  assert.deepEqual(readCatalogHistoryQuery('?q=%E0%A4%A&page=2'), { search: '', offset: 25 });
});

test('catalog history rejects ambiguous fields and noncanonical pages without clamping other fields', () => {
  assert.deepEqual(readCatalogHistoryQuery('?q=PENA&page=401&actorId=17&session=private'), { search: 'PENA', offset: 10000 });
  assert.deepEqual(readCatalogHistoryQuery('?q=PENA&q=PENA&page=2'), { search: '', offset: 25 });
  assert.deepEqual(readCatalogHistoryQuery('?q=PENA&page=2&page=2'), { search: 'PENA', offset: 0 });
  for (const page of ['', '0', '-1', '+2', '02', '001', '1.0', '1e2', '402', '999', '1000', '9007199254740993', ' 2', '2\n']) {
    assert.deepEqual(readCatalogHistoryQuery('?q=PENA&page=' + encodeURIComponent(page)), { search: 'PENA', offset: 0 }, page);
  }
  assert.deepEqual(readCatalogHistoryQuery('?q=PENA&unknown='.padEnd(2048, 'x')), { search: 'PENA', offset: 0 });
  assert.deepEqual(readCatalogHistoryQuery('?q=PENA&unknown=' + 'x'.repeat(2048)), { search: '', offset: 0 });
  assert.deepEqual(readCatalogHistoryQuery('?q=PENA#private'), { search: '', offset: 0 });
});

test('catalog history canonicalizes only public list queries, exact list paths and known bounded anchors', () => {
  assert.equal(catalogHistoryUrl('/shop', { search: '', offset: 0 }), '/shop');
  const query = readCatalogHistoryQuery('?page=3&q=%20Cafe%CC%81%20%26%20pena%20&actorId=17&token=private');
  assert.equal(catalogHistoryUrl('/shop', query, '#catalog%2Dlist'), '/shop?q=Caf%C3%A9+%26+pena&page=3#catalog-list');
  assert.equal(catalogHistoryUrl('/shop', query, 'catalog-main'), '/shop?q=Caf%C3%A9+%26+pena&page=3#catalog-main');
  for (const hash of ['#item-private', '#session=private', '#catalog-list?token=private', '#catalog-main\n', '#%broken', '#'.repeat(129)]) {
    assert.equal(catalogHistoryUrl('/shop', { search: '', offset: 0 }, hash), '/shop');
  }
  for (const path of ['/shop/', '//shop', '/shop/private', '/catalog/browse?token=private', 'https://outside.test/shop']) {
    assert.throws(() => catalogHistoryUrl(path as '/shop', { search: '', offset: 0 }), TypeError);
  }
  for (const offset of [-25, 1, 24, 26, 10025, Infinity, NaN, Number.MAX_SAFE_INTEGER + 1]) {
    assert.equal(catalogHistoryUrl('/shop', { search: 'PENA', offset }), '/shop?q=PENA');
  }
});

test('every bounded catalog page roundtrips through one canonical URL', () => {
  for (let offset = 0; offset <= 10000; offset += 25) {
    for (const search of ['', 'PENA', 'Café & 🖊️ + #']) {
      const query = { search, offset }, url = new URL(catalogHistoryUrl('/shop', query), 'https://unit-toko.test');
      assert.deepEqual(readCatalogHistoryQuery(url.search), query);
      assert.equal(catalogHistoryUrl('/shop', readCatalogHistoryQuery(url.search)), url.pathname + url.search);
    }
  }
});

test('native list history restores search and page without rewriting another route during Back or stale clicks', () => {
  const entries: { url: string; state: Record<string, never> }[] = [];
  let cursor = 0;
  const location = () => new URL(entries[cursor].url, 'https://unit-toko.test');
  const restore = () => location().pathname === '/shop' ? readCatalogHistoryQuery(location().search) : null;
  const history = {
    replaceState(state: Record<string, never>, _unused: string, url: string) { entries[cursor] = { url, state }; },
    pushState(state: Record<string, never>, _unused: string, url: string) { entries.splice(cursor + 1); entries.push({ url, state }); cursor = entries.length - 1; },
    back() { cursor = Math.max(0, cursor - 1); },
    forward() { cursor = Math.min(entries.length - 1, cursor + 1); },
  };
  function write(query: CatalogHistoryQuery, replace = false) {
    if (location().pathname !== '/shop') return;
    history[replace ? 'replaceState' : 'pushState']({}, '', catalogHistoryUrl('/shop', query, location().hash));
  }
  entries.push({ url: '/shop?q=%20PENA%20&page=2&actorId=17&session=private#catalog-list', state: {} });
  const initial = restore(); assert.ok(initial); write(initial, true);
  assert.equal(entries[0].url, '/shop?q=PENA&page=2#catalog-list');
  write({ search: 'TAS', offset: 0 });
  write({ search: 'TAS', offset: 25 });
  history.back();
  assert.deepEqual(restore(), { search: 'TAS', offset: 0 });
  history.back();
  assert.deepEqual(restore(), { search: 'PENA', offset: 25 });
  history.forward();
  assert.deepEqual(restore(), { search: 'TAS', offset: 0 });
  const reloaded = new URL(entries[cursor].url, 'https://unit-toko.test');
  assert.deepEqual(readCatalogHistoryQuery(reloaded.search), restore());
  const catalogUrl = entries[cursor].url;
  for (const otherRoute of ['/', '/identity/account', '/shop/', '/catalog/browse?q=PENA']) {
    history.pushState({}, '', otherRoute);
    history.pushState({}, '', catalogUrl);
    history.back();
    const before = JSON.stringify(entries), originalCursor = cursor;
    assert.equal(restore(), null);
    const staleRestore = restore(); if (staleRestore) write(staleRestore, true);
    write({ search: 'stale catalog click', offset: 25 });
    assert.equal(JSON.stringify(entries), before);
    assert.equal(cursor, originalCursor);
    assert.equal(location().pathname + location().search, otherRoute);
    history.forward(); assert.deepEqual(restore(), { search: 'TAS', offset: 0 });
  }
  for (const entry of entries) {
    assert.deepEqual(entry.state, {});
    assert.equal(/actorId|session|private|skuId|priceId|__NA|__PRIVATE_NEXTJS_INTERNALS_TREE/.test(JSON.stringify(entry)), false);
  }
});

test('catalog history keeps one category chip and the name order in the URL and drops anything malformed', () => {
  assert.deepEqual(readCatalogHistoryQuery('?kategori=pantry&urut=nama&page=2'), { search: '', offset: 25, category: 'PANTRY', sort: 'name' });
  assert.equal(catalogHistoryUrl('/shop', { search: 'teh', offset: 25, category: 'PANTRY', sort: 'name' }), '/shop?q=teh&kategori=PANTRY&urut=nama&page=2');
  assert.equal(catalogHistoryUrl('/shop', { search: '', offset: 0, category: 'bad code', sort: 'price' as 'name' }), '/shop');
  for (const raw of ['?kategori=', '?kategori=a%20b', '?kategori=PANTRY&kategori=ATK', '?kategori=' + 'A'.repeat(65), '?kategori=%C3%85', '?urut=harga', '?urut=nama&urut=nama', '?urut=NAMA']) {
    assert.deepEqual(readCatalogHistoryQuery(raw), { search: '', offset: 0 }, raw);
  }
  const query = { search: 'Teh', offset: 50, category: 'SNACK-RAPAT', sort: 'name' as const };
  const url = new URL(catalogHistoryUrl('/shop', query), 'https://unit-toko.test');
  assert.deepEqual(readCatalogHistoryQuery(url.search), query);
});
