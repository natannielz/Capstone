// Real shared HTTPS/Next/API/Mongo host supplied by verify-catalog-price-browser.
// Every account and provider proof in this rehearsal is explicitly synthetic.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyPrimaryShopBrowser({ config, user, origin, directory, writes, requests, firstApiAt, loseNext, dropped }) {
  assert.match(config.publicId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  const buyer = await user('buyer'); const page = buyer.page;
  const primaryPhotoPath = /^\/api\/v1\/catalog\/variants\/[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\/primary-photo\/query$/;
  const csrfResponses = [], photoResponses = [];
  const requestFacts = new WeakMap();
  const issuedTokens = new Set(), native204Facts = []; let native204Events = 0;
  let actionGeneration = 0, actionStage = 'initial_catalog', transitionActive = false;
  const pacing = [];
  async function paceWorkflow(label, reserve) {
    const first = firstApiAt(); assert.ok(Number.isFinite(first));
    const now = performance.now(), recent = requests.filter(request => Number.isFinite(request.apiAt) && request.apiAt > now - 60000 &&
      (request.path.startsWith('/api/v1/') || request.path.startsWith('/auth/')));
    const used = recent.length;
    if (used + reserve <= 240) return;
    const retire = used + reserve - 240, waitMs = Math.max(0, recent[retire - 1].apiAt + 61000 - now);
    assert.ok(waitMs <= 61000);
    const observation = { workflow: label, used, reserve, permitLimit: 240, windowMilliseconds: 60000,
      elapsedSinceFirstApi: now - first, waitMs };
    pacing.push(observation); console.log('FIXTURE_RATE_WINDOW ' + JSON.stringify(observation));
    await new Promise(resolve => setTimeout(resolve, waitMs));
    const after = performance.now(), remaining = requests.filter(request => Number.isFinite(request.apiAt) && request.apiAt > after - 60000 &&
      (request.path.startsWith('/api/v1/') || request.path.startsWith('/auth/'))).length;
    assert.ok(remaining + reserve <= 240, 'The real request window must have capacity before the next workflow starts');
  }
  let reportReadFailure;
  const firstReadFailure = new Promise(resolve => { reportReadFailure = resolve; });
  function boundedRead(promise, milliseconds, failure) {
    let timer;
    return Promise.race([promise, new Promise((_resolve, reject) => { timer = setTimeout(() => reject(failure()), milliseconds); })])
      .finally(() => clearTimeout(timer));
  }
  function isCatalogRead(request) {
    const path = new URL(request.url()).pathname;
    return path === '/api/v1/identity/csrf' || path === '/api/v1/catalog/variants/query' || primaryPhotoPath.test(path);
  }
  function factsFor(request) {
    let facts = requestFacts.get(request);
    if (facts) return facts;
    const path = new URL(request.url()).pathname;
    let complete;
    facts = { path, documentPath: new URL(page.url()).pathname, method: request.method(), requestBody: request.postDataJSON(),
      generation: actionGeneration, createdStage: actionStage, responseArrived: false, status: null, noStore: false,
      headersReady: false, completionReady: false,
      completion: new Promise(resolve => { complete = resolve; }), complete: result => { facts.completionReady = true; complete(result); } };
    requestFacts.set(request, facts);
    // Start exact (including HttpOnly cookie) header capture at request arrival.
    facts.headers = Promise.allSettled([request.allHeaders()]).then(([result]) => {
      facts.headersReady = true;
      if (result.status === 'fulfilled') facts.exactHeaders = result.value;
      if (result.status === 'rejected') reportReadFailure(new Error('request_headers_unavailable path=' + path));
      return result;
    });
    return facts;
  }
  const observeRequest = request => { if (isCatalogRead(request)) factsFor(request); };
  const observeFinished = request => { if (isCatalogRead(request)) factsFor(request).complete(null); };
  const observeFailed = request => {
    if (!isCatalogRead(request)) return;
    const facts = factsFor(request), reason = request.failure()?.errorText;
    const safe = typeof reason === 'string' && /^[A-Za-z0-9:_ .-]{1,160}$/.test(reason) ? reason : 'request_failed';
    const event = { path: facts.path, reason: safe, requestGeneration: facts.generation, requestStage: facts.createdStage,
      actionGeneration, actionStage, transitionActive, responseArrived: facts.responseArrived, status: facts.status, noStore: facts.noStore,
      headersReady: facts.headersReady };
    const phase = boundedRead(page.evaluate(publicId => {
      const photos = [...document.querySelectorAll('section[aria-label^="Foto kemasan "]')];
      const kind = photo => {
        if (!photo) return 'not_rendered';
        if (photo.getAttribute('aria-busy') === 'true') return 'loading';
        const text = photo.querySelector('[role="status"]')?.textContent?.trim() ?? '';
        if (text === 'Foto belum tersedia') return 'none';
        if (text.startsWith('Foto disembunyikan.')) return 'hidden';
        return photo.querySelector('img') ? 'ready' : 'error';
      };
      const anchor = publicId ? [...document.querySelectorAll('a[href]')].find(link => new URL(link.href).pathname === '/shop/' + publicId) : null;
      const target = anchor?.closest('li')?.querySelector('section[aria-label^="Foto kemasan "]');
      const counts = { loading: 0, none: 0, hidden: 0, ready: 0, error: 0 };
      for (const photo of photos) counts[kind(photo)]++;
      return { pathname: location.pathname, online: navigator.onLine, visibility: document.visibilityState, nativeEdge: /Edg\//.test(navigator.userAgent),
        total: photos.length, counts, targetPhase: kind(target) };
    }, primaryPhotoPath.test(facts.path) ? facts.path.split('/')[5] : null), 1000,
    () => new Error('photo_phase_unavailable'));
    void Promise.allSettled([phase]).then(([result]) => {
      const dom = result.status === 'fulfilled' ? result.value : { phase: 'unavailable' }, headers = facts.exactHeaders;
      const native204 = safe === 'net::ERR_ABORTED' && primaryPhotoPath.test(facts.path) && facts.method === 'POST' &&
        event.responseArrived && event.status === 204 && event.noStore && event.headersReady && headers &&
        event.requestGeneration === event.actionGeneration && !event.transitionActive && dom.nativeEdge === true &&
        dom.pathname === facts.documentPath && dom.targetPhase === 'none' && dom.online === true && dom.visibility === 'visible' &&
        dom.counts?.hidden === 0 && dom.counts?.error === 0 && headers['x-expected-actor-id'] === config.buyerId &&
        headers.cookie?.includes('__Host-ut_session=') && headers.cookie.includes('__Host-ut_csrf=') &&
        headers.authorization === undefined && headers['idempotency-key'] === undefined &&
        typeof headers['x-csrf-token'] === 'string' && issuedTokens.has(headers['x-csrf-token']) &&
        Object.keys(facts.requestBody ?? {}).length === 1 && facts.requestBody?.purpose === 'catalog-photo';
      const diagnostic = { ...event, dom, native204Instrumentation: Boolean(native204) };
      if (native204) {
        native204Events++; if (native204Facts.length < 8) native204Facts.push(diagnostic);
        if (native204Events <= 8) console.log('EDGE_NATIVE_204_COMPLETE ' + JSON.stringify(diagnostic));
        facts.complete(null); return;
      }
      console.log('CATALOG_READ_FAILURE_FACTS ' + JSON.stringify(diagnostic));
      const failure = new Error('catalog_request_failed path=' + facts.path + ' reason=' + safe +
        ' requestGeneration=' + event.requestGeneration + ' actionGeneration=' + event.actionGeneration +
        ' stage=' + event.actionStage + ' responseArrived=' + event.responseArrived + ' status=' + event.status);
      facts.complete(failure); reportReadFailure(failure);
    });
  };
  page.on('request', observeRequest); page.on('requestfinished', observeFinished); page.on('requestfailed', observeFailed);
  function snapshotResponse(response, jsonBody) {
    const request = response.request(), facts = factsFor(request);
    const snapshot = { url: response.url(), method: request.method(), status: response.status(),
      responseHeaders: response.headers(), requestBody: request.postDataJSON() };
    facts.responseArrived = true; facts.status = snapshot.status;
    facts.noStore = /\bno-store\b/i.test(snapshot.responseHeaders['cache-control'] ?? '');
    const path = new URL(snapshot.url).pathname, photoFailure = primaryPhotoPath.test(path) && snapshot.status !== 204;
    let code = 'problem_code_unavailable';
    let bodyReady = !jsonBody && !photoFailure;
    const failedPhoto = () => new Error('photo_read_failed status=' + snapshot.status + ' code=' + code + ' path=' + path);
    // Start body/header reads at arrival, while this document still owns the response.
    const body = jsonBody || photoFailure ? response.json().then(value => {
      bodyReady = true;
      if (photoFailure) {
        if (typeof value?.code === 'string' && /^[a-z][a-z0-9_.-]{0,95}$/.test(value.code)) code = value.code;
        throw failedPhoto();
      }
      return value;
    }, () => { bodyReady = true; throw photoFailure ? failedPhoto() : new Error('response_body_unavailable status=' + snapshot.status + ' path=' + path); }) : Promise.resolve(null);
    return boundedRead(Promise.all([body, facts.headers, facts.completion])
      .then(([body, headers, failure]) => {
        if (failure) throw failure;
        if (headers.status === 'rejected') throw new Error('request_headers_unavailable path=' + path);
        return { ...snapshot, body, headers: headers.value };
      }), 5000, () => {
        if (photoFailure) return failedPhoto();
        const pending = [!bodyReady && 'body', !facts.headersReady && 'request_headers', !facts.completionReady && 'request_completion'].filter(Boolean).join(',');
        return new Error('response_snapshot_timeout status=' + snapshot.status + ' path=' + path + ' pending=' + pending +
          ' requestGeneration=' + facts.generation + ' actionGeneration=' + actionGeneration + ' stage=' + actionStage);
      });
  }
  function retainSnapshot(response, jsonBody) {
    const pending = Promise.allSettled([snapshotResponse(response, jsonBody)]);
    void pending.then(([result]) => {
      if (result.status === 'rejected') reportReadFailure(result.reason);
      else if (new URL(result.value.url).pathname === '/api/v1/identity/csrf' && result.value.status === 200 &&
        /\bno-store\b/i.test(result.value.responseHeaders['cache-control'] ?? '') && typeof result.value.body?.requestToken === 'string')
        issuedTokens.add(result.value.body.requestToken);
    });
    return pending;
  }
  const observeReads = response => {
    const path = new URL(response.url()).pathname;
    // allSettled retains failures without an unhandled observer rejection; drainReads
    // explicitly throws every rejected snapshot before any navigation can continue.
    if (path === '/api/v1/identity/csrf' && response.request().method() === 'GET') csrfResponses.push(retainSnapshot(response, true));
    if (primaryPhotoPath.test(path)) photoResponses.push(retainSnapshot(response, false));
  };
  page.on('response', observeReads);
  await page.getByRole('heading', { name: 'Katalog kemasan', exact: true }).waitFor();
  const search = page.getByLabel('Cari kode atau kemasan', { exact: true });
  const catalog = page.getByRole('region', { name: 'Kemasan yang tersedia', exact: true });
  await catalog.locator('[aria-label="Rincian PRIMARY-12 Dus dua belas"]').waitFor();
  function historyOnly(value) {
    assert.doesNotMatch(JSON.stringify(value), /"(?:actorId|account|items|selected|session|requestToken|csrfToken|skuId|definitionId|priceId|currentPrice|binding)"\s*:/,
      'Catalog history must contain no account, item, session or request-token data');
  }
  async function snapshots(pending) {
    return Promise.all(pending.map(async promise => {
      const [result] = await promise; if (result.status === 'rejected') throw result.reason; return result.value;
    }));
  }
  async function drainReads() {
    await boundedRead(Promise.race([drainReadSnapshots(), firstReadFailure.then(error => { throw error; })]), 15000,
      () => new Error('catalog_read_drain_timeout issuerReads=' + csrfResponses.length + ' photoReads=' + photoResponses.length +
        ' actionGeneration=' + actionGeneration + ' stage=' + actionStage));
  }
  async function drainReadSnapshots() {
    await page.waitForFunction(() => [...document.querySelectorAll('section[aria-label^="Foto kemasan "]')]
      .every(photo => photo.getAttribute('aria-busy') === 'false'));
    let observed;
    do {
      observed = csrfResponses.length + photoResponses.length;
      const issued = await snapshots(csrfResponses), photos = await snapshots(photoResponses), tokens = new Set();
      for (const issuer of issued) {
        assert.equal(issuer.status, 200, 'Every observed CSRF issuer read must succeed');
        assert.match(issuer.responseHeaders['cache-control'] ?? '', /\bno-store\b/i);
        assert.ok(typeof issuer.body.requestToken === 'string' && issuer.body.requestToken.length > 0);
        tokens.add(issuer.body.requestToken);
      }
      for (const photo of photos) {
        const url = new URL(photo.url), headers = photo.headers;
        assert.equal(url.origin, origin.origin); assert.equal(url.search, ''); assert.equal(photo.method, 'POST');
        assert.equal(photo.status, 204, 'The genuine empty-media fixture must return no photo, never an invented image or denied read');
        assert.match(photo.responseHeaders['cache-control'] ?? '', /\bno-store\b/i);
        assert.deepEqual(photo.requestBody, { purpose: 'catalog-photo' });
        assert.equal(headers['x-expected-actor-id'], config.buyerId);
        assert.ok(headers.cookie?.includes('__Host-ut_session=') && headers.cookie.includes('__Host-ut_csrf='));
        assert.equal(headers.authorization, undefined); assert.equal(headers['idempotency-key'], undefined);
        assert.ok(typeof headers['x-csrf-token'] === 'string' && headers['x-csrf-token'].length > 0 && headers['x-csrf-token'].length <= 8192);
        assert.ok(tokens.has(headers['x-csrf-token']), 'The exact photo read token must match a real successful issuer response');
      }
      const state = await page.evaluate(() => history.state); historyOnly(state);
      for (const token of tokens) assert.ok(!JSON.stringify(state).includes(token), 'Request tokens stay out of browser history');
      assert.equal(writes.length, 0, 'Scoped photo and catalog reads cannot create a draft or order');
    } while (observed !== csrfResponses.length + photoResponses.length);
  }
  async function navigate(label, action, submitted, offset = 0) {
    actionStage = 'before_' + label; transitionActive = false;
    console.log('CATALOG_ACTION ' + JSON.stringify({ generation: actionGeneration, stage: actionStage }));
    await drainReads();
    const csrfStart = csrfResponses.length;
    const queried = page.waitForResponse(response => {
      if (new URL(response.url()).pathname !== '/api/v1/catalog/variants/query' || response.request().method() !== 'POST') return false;
      const body = response.request().postDataJSON(); return body.search === submitted && body.offset === offset;
    }).then(response => snapshotResponse(response, true));
    actionGeneration++; actionStage = label; transitionActive = true;
    console.log('CATALOG_ACTION ' + JSON.stringify({ generation: actionGeneration, stage: actionStage }));
    const [response] = await Promise.all([queried, action()]);
    transitionActive = false; actionStage = 'after_' + label;
    assert.equal(response.status, 200); assert.match(response.responseHeaders['cache-control'] ?? '', /\bno-store\b/i);
    assert.deepEqual(response.requestBody, { search: submitted, offset, limit: 25 });
    const headers = response.headers;
    assert.equal(headers['x-expected-actor-id'], config.buyerId);
    assert.ok(headers.cookie?.includes('__Host-ut_session='), 'Catalog queries use the real session cookie');
    assert.equal(headers.authorization, undefined);
    const issued = await snapshots(csrfResponses.slice(csrfStart)), freshTokens = [];
    for (const issuer of issued) {
      assert.equal(issuer.status, 200, 'A fresh CSRF issuer response must succeed');
      assert.match(issuer.responseHeaders['cache-control'] ?? '', /\bno-store\b/i);
      const csrf = issuer.body; assert.ok(typeof csrf.requestToken === 'string' && csrf.requestToken.length > 0);
      freshTokens.push(csrf.requestToken);
    }
    assert.ok(freshTokens.includes(headers['x-csrf-token']), 'This exact query must use a token freshly issued after its action boundary');
    const result = response.body, total = submitted === 'PRIMARY-12' ? 1 : 26;
    const codes = submitted === 'PRIMARY-12' ? ['PRIMARY-12'] : offset === 25 ? ['PRIMARY-NAV-25'] :
      ['PRIMARY-12', ...Array.from({ length: 24 }, (_, index) => 'PRIMARY-NAV-' + String(index + 1).padStart(2, '0'))];
    assert.equal(result.total, String(total)); assert.deepEqual(result.items.map(item => item.code), codes);
    await page.locator('section[aria-label="Kemasan yang tersedia"][aria-busy="false"]').waitFor();
    await page.getByRole('heading', { name: 'Katalog kemasan', exact: true }).waitFor();
    const pagination = catalog.getByRole('navigation', { name: 'Halaman katalog', exact: true });
    await pagination.getByText('Halaman ' + (offset / 25 + 1), { exact: true }).waitFor();
    assert.equal(await search.inputValue(), submitted); assert.equal(await search.isDisabled(), false);
    assert.equal(await page.getByRole('button', { name: 'Cari', exact: true }).isDisabled(), false);
    assert.equal(await pagination.getByRole('button', { name: 'Sebelumnya', exact: true }).isDisabled(), offset === 0);
    assert.equal(await pagination.getByRole('button', { name: 'Berikutnya', exact: true }).isDisabled(), offset + result.items.length >= total);
    assert.deepEqual(await catalog.getByRole('button', { name: /^Rincian / }).evaluateAll(elements => elements.map(element => element.getAttribute('aria-label'))),
      result.items.map(item => 'Rincian ' + item.code + ' ' + item.definition.packageLabel));
    assert.match(await catalog.innerText(), new RegExp(total + ' kemasan dalam katalog'));
    const expected = '/shop' + (submitted ? '?q=' + encodeURIComponent(submitted) : '') + (offset ? '&page=2' : '');
    assert.equal(page.url(), origin.origin + expected);
    const state = await page.evaluate(() => history.state); historyOnly(state);
    for (const token of freshTokens) assert.ok(!JSON.stringify(state).includes(token), 'Request tokens stay out of browser history');
    assert.equal(await page.getByRole('dialog').count(), 0); assert.equal(writes.length, 0, 'Navigation cannot save an order or draft');
    await drainReads();
  }
  const navigationStart = requests.length;
  try {
    await navigate('search_exact', async () => { await search.fill('PRIMARY-12'); await page.getByRole('button', { name: 'Cari', exact: true }).click(); }, 'PRIMARY-12');
    await navigate('search_all', async () => { await search.fill('PRIMARY'); await page.getByRole('button', { name: 'Cari', exact: true }).click(); }, 'PRIMARY');
    await navigate('next_page', () => page.getByRole('button', { name: 'Berikutnya', exact: true }).click(), 'PRIMARY', 25);
    for (const [size, viewport] of [['desktop', { width: 1280, height: 960 }], ['mobile', { width: 390, height: 844 }]]) {
      await page.setViewportSize(viewport); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      await page.screenshot({ path: resolve(directory, 'primary-shop-history-' + size + '.png'), fullPage: true });
    }
    await navigate('back_page', () => page.goBack(), 'PRIMARY');
    await navigate('forward_page', () => page.goForward(), 'PRIMARY', 25);
    await navigate('reload_page', () => page.reload(), 'PRIMARY', 25);
    await navigate('canonicalize_invalid', () => page.goto(origin.origin + '/shop?q=PRIMARY&q=PRIMARY-12&page=02&page=2&ignored=legacy#invalid'), '');
    actionStage = 'before_home'; await drainReads(); await paceWorkflow('home_link_catalog', 128);
    actionGeneration++; actionStage = 'home'; transitionActive = true;
    await page.goto(origin.origin + '/'); transitionActive = false; actionStage = 'after_home';
    const homeLink = page.getByRole('link', { name: 'Belanja pelanggan', exact: true }); await homeLink.waitFor();
    assert.equal(await homeLink.getAttribute('href'), '/shop');
    await navigate('home_link_catalog', () => homeLink.click(), '');
    actionStage = 'before_back_home'; await drainReads(); actionGeneration++; actionStage = 'back_home'; transitionActive = true;
    await page.goBack(); await page.waitForURL(origin.origin + '/'); transitionActive = false; actionStage = 'after_back_home';
    await page.getByRole('heading', { name: /Pilihan baik/ }).waitFor();
    assert.equal(page.url(), origin.origin + '/'); assert.equal(await page.getByRole('heading', { name: 'Katalog kemasan', exact: true }).count(), 0);
    historyOnly(await page.evaluate(() => history.state)); assert.equal(writes.length, 0);
    await navigate('forward_catalog', () => page.goForward(), '');
    for (const request of requests.slice(navigationStart).filter(request => request.method === 'POST' && request.path.startsWith('/api/v1/')))
      assert.ok(request.path === '/api/v1/catalog/variants/query' || primaryPhotoPath.test(request.path),
        'Catalog navigation permits only exact variant and UUID primary-photo read queries: ' + request.path);
    await drainReads();
    assert.ok(photoResponses.length > 0, 'The real catalog also performs scoped private-photo reads');
    assert.equal(writes.length, 0, 'Scoped photo reads cannot create a draft or order');
    await page.setViewportSize({ width: 1280, height: 960 });
  } finally {
    page.off('response', observeReads); page.off('request', observeRequest);
    page.off('requestfinished', observeFinished); page.off('requestfailed', observeFailed);
  }
  await paceWorkflow('original_package_and_draft', 96);
  const open = page.getByRole('button', { name: 'Rincian PRIMARY-12 Dus dua belas', exact: true }); await open.waitFor();
  assert.equal(await page.getByRole('dialog').count(), 0, 'A package must never be selected implicitly');
  const link = page.getByRole('link', { name: 'Halaman PRIMARY-12 Dus dua belas', exact: true });
  assert.equal(await link.getAttribute('href'), '/shop/' + config.publicId);
  await page.setViewportSize({ width: 360, height: 800 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-shop-mobile.png'), fullPage: true });
  await open.click(); const dialog = page.getByRole('dialog', { name: 'Pensil utama — Dus dua belas', exact: true }); await dialog.waitFor();
  assert.match(await dialog.innerText(), /12 piece per kemasan/); assert.match(await dialog.innerText(), /IDR 12\.000,25/);
  assert.equal(await dialog.getByLabel('Cabang pemesan', { exact: true }).inputValue(), '');
  assert.equal(await dialog.getByLabel('Jumlah kemasan', { exact: true }).inputValue(), '');
  assert.equal(writes.length, 0); await dialog.getByRole('button', { name: 'Tutup rincian', exact: true }).click();
  await dialog.waitFor({ state: 'hidden' }); assert.equal(await open.evaluate(element => element === document.activeElement), true);
  await open.click(); await dialog.waitFor(); await page.keyboard.press('Escape'); await dialog.waitFor({ state: 'hidden' });
  assert.equal(await open.evaluate(element => element === document.activeElement), true); assert.equal(writes.length, 0, 'Dismissal saves nothing');
  await link.click(); await page.waitForURL(origin.origin + '/shop/' + config.publicId);
  await page.getByRole('heading', { name: 'Rincian kemasan', exact: true }).waitFor();
  const details = page.getByRole('region', { name: 'Rincian kemasan', exact: true }); await details.waitFor();
  await details.getByRole('heading', { name: 'Pensil utama — Dus dua belas', exact: true }).waitFor();
  assert.equal(await page.getByRole('dialog').count(), 0); assert.match(await details.innerText(), /Pensil utama — Dus dua belas/);
  assert.match(await details.innerText(), /12 piece per kemasan/); assert.match(await details.innerText(), /IDR 12\.000,25/);
  assert.equal(await details.getByRole('img').count(), 0, 'There are no invented product images in the empty-media fixture');
  const branch = details.getByLabel('Cabang pemesan', { exact: true }), quantity = details.getByLabel('Jumlah kemasan', { exact: true });
  await branch.waitFor(); assert.equal(await branch.inputValue(), ''); assert.equal(await quantity.inputValue(), '');
  await branch.selectOption('10:11:');
  for (const invalid of ['3', '5']) {
    const previews = requests.filter(request => request.path === '/api/v1/ordering/quote/query').length;
    await quantity.fill(invalid); await details.getByRole('button', { name: 'Periksa subtotal', exact: true }).click();
    await details.getByRole('alert').filter({ hasText: 'Isi jumlah kemasan sesuai minimum dan kelipatan pemesanan.' }).waitFor();
    assert.equal(await details.getByRole('button', { name: 'Simpan sebagai draf', exact: true }).count(), 0);
    assert.equal(requests.filter(request => request.path === '/api/v1/ordering/quote/query').length, previews, 'Invalid minimum/increment never reaches the API');
  }
  await quantity.fill('6'); await details.getByRole('button', { name: 'Periksa subtotal', exact: true }).click();
  const subtotal = details.getByRole('status').filter({ hasText: 'Subtotal barang' }); await subtotal.waitFor();
  assert.match(await subtotal.innerText(), /IDR 72\.001,50/); assert.match(await subtotal.innerText(), /6 Dus dua belas · 72 piece/);
  assert.match(await subtotal.innerText(), /Pesanan belum dikirim dan stok belum dipesan/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-package-subtotal-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 960 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-package-subtotal-desktop.png'), fullPage: true });
  loseNext(); await details.getByRole('button', { name: 'Simpan sebagai draf', exact: true }).click();
  await details.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(dropped(), 1); assert.equal(writes.length, 1);
  const attempt = await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  assert.equal(attempt.length, 1); assert.deepEqual(Object.keys(attempt[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(attempt[0].actorId, config.buyerId); assert.equal(attempt[0].operation, 'ordering.draft.save'); assert.equal(attempt[0].key, writes[0].key);
  const selection = writes[0].body.input.selections;
  assert.equal(selection.length, 1); assert.equal(selection[0].skuId, config.skuId); assert.equal(selection[0].definitionId, config.definitionId);
  assert.equal(selection[0].priceId, config.priceId); assert.equal(selection[0].packages, 6);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Selesaikan dan tutup', exact: true }).count(), 0);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('status').filter({ hasText: 'Draf tersimpan.' }).waitFor();
  assert.equal(writes.length, 1, 'A lost completed reply is recovered by lookup, without a replacement draft');
  const recoveries = requests.filter(request => request.path === '/api/v1/ordering/drafts/operations/query');
  assert.equal(recoveries.length, 1); assert.deepEqual(recoveries[0].body, { purpose: 'operation-recovery', key: attempt[0].key });
  assert.equal(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).length), 0);
  await page.getByRole('link', { name: 'Buka draf pesanan', exact: true }).click();
  await page.getByLabel('Cabang pemesan', { exact: true }).selectOption('10:11:');
  const savedDraft = page.getByRole('region', { name: 'Daftar draf', exact: true }).getByRole('button', { name: /Draf [0-9]+/ });
  await savedDraft.waitFor(); assert.equal(await savedDraft.count(), 1);
  await page.screenshot({ path: resolve(directory, 'primary-draft-recovered-desktop.png'), fullPage: true });
  const draftTitle = await savedDraft.locator('strong').innerText(); await savedDraft.click();
  const editor = page.getByRole('region', { name: 'Isi draf', exact: true }); await editor.getByRole('heading', { name: draftTitle, exact: true }).waitFor();
  assert.equal(await editor.getByRole('region', { name: 'Alamat penagihan organisasi', exact: true }).count(), 0, 'Approved postal facts require an explicit read');
  await editor.getByLabel('Isi rencana pengiriman', { exact: true }).check();
  const deliveryFields = ['Nama penerima', 'Telepon penerima', 'Alamat pengiriman', 'Kota', 'Kode pos (opsional)', 'Catatan pengiriman (opsional)'];
  for (const label of deliveryFields) assert.equal(await editor.getByLabel(label, { exact: true }).inputValue(), '');
  const postalResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/ordering/addresses/query' && response.request().method() === 'POST');
  await editor.getByRole('button', { name: 'Periksa alamat yang disetujui', exact: true }).click();
  assert.equal((await postalResponse).status(), 200);
  const postal = editor.getByRole('region', { name: 'Alamat yang disetujui', exact: true }); await postal.waitFor();
  const billing = postal.getByRole('region', { name: 'Alamat penagihan organisasi', exact: true });
  const delivery = postal.getByRole('region', { name: 'Alamat pengiriman cabang', exact: true });
  for (const text of ['BUYER', 'Synthetic buying organization', 'Jalan Penagihan Utama 5', 'Lantai administrasi', 'Jakarta Selatan', 'DKI Jakarta', '12820', 'ID'])
    assert.ok((await billing.innerText()).includes(text), 'The approved billing card must include ' + text);
  for (const text of ['TEBET', 'Synthetic Tebet branch', 'Jalan Tebet Raya 12', 'Lantai 2', 'Jakarta Selatan', 'DKI Jakarta', '12810', 'ID'])
    assert.ok((await delivery.innerText()).includes(text), 'The approved delivery card must include ' + text);
  for (const label of deliveryFields) assert.equal(await editor.getByLabel(label, { exact: true }).inputValue(), '', 'Postal review must not invent or overwrite recipient/contact input');
  assert.equal(writes.length, 1, 'Reading postal facts cannot save a draft or submit an order');
  assert.equal(requests.filter(request => request.path === '/api/v1/ordering/addresses/query').length, 1);
  await page.screenshot({ path: resolve(directory, 'primary-draft-approved-addresses-desktop.png'), fullPage: true });
  await page.setViewportSize({ width: 360, height: 800 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'primary-draft-approved-addresses-mobile.png'), fullPage: true });
  const foreign = await user('foreign');
  // Active branch permissions do not replace an independently approved catalog entitlement.
  await foreign.page.getByText('Katalog ini belum tersedia untuk akun Anda. Hubungi pengelola akun.', { exact: true }).waitFor();
  assert.equal(await foreign.page.getByRole('button', { name: 'Rincian PRIMARY-12 Dus dua belas', exact: true }).count(), 0);
  const unavailable = await foreign.page.evaluate(async ({ id, actor }) => {
    const csrf = await fetch('/api/v1/identity/csrf', { credentials: 'same-origin', cache: 'no-store' });
    if (!csrf.ok) throw new Error('Foreign catalog probe requires the real CSRF issuer');
    const { requestToken } = await csrf.json();
    if (typeof requestToken !== 'string' || !requestToken) throw new Error('Invalid real CSRF response');
    const results = [];
    for (const [path, body] of [
      ['/api/v1/catalog/variants/query', {}], ['/api/v1/catalog/variants/' + id + '/query', {}],
      ['/api/v1/ordering/addresses/query', { purpose: 'order-preparation', context: { organizationId: '10', branchId: '11', divisionId: null } }],
    ]) {
      const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: {
        'Content-Type': 'application/json', 'X-Expected-Actor-Id': actor, 'X-CSRF-Token': requestToken,
      }, body: JSON.stringify(body) });
      results.push({ status: response.status, body: await response.json() });
    }
    return results;
  }, { id: config.publicId, actor: config.foreignId });
  assert.equal(unavailable.length, 3);
  for (const denial of unavailable.slice(0, 2)) {
    assert.equal(denial.status, 403); assert.equal(denial.body.code, 'catalog_visibility_denied');
    for (const field of ['items', 'total', 'skuId', 'publicId', 'definition', 'currentPrice', 'familyId']) assert.equal(Object.hasOwn(denial.body, field), false);
    assert.doesNotMatch(JSON.stringify(denial.body), /PRIMARY-12|Pensil utama|12000\.25/);
  }
  assert.equal(unavailable[2].status, 403);
  for (const field of ['legalBilling', 'delivery', 'address', 'name', 'binding']) assert.equal(Object.hasOwn(unavailable[2].body, field), false);
  assert.doesNotMatch(JSON.stringify(unavailable[2].body), /Jalan|Tebet|Penagihan|12810|12820|Synthetic buying organization/);
  assert.doesNotMatch(await foreign.page.locator('main').innerText(), /PRIMARY-12|Pensil utama/);
  assert.equal(writes.length, 1);
  assert.deepEqual(requests.filter(request => /^\/api\/(catalog|session|commands)(?:\/|$)/.test(request.path)), [], 'Primary shop uses no legacy catalog/session/command routes');
  await foreign.context.close(); await buyer.context.close();
  console.log('REAL_PRIMARY_SHOP_BROWSER_PASS ' + JSON.stringify({ savedDraftCommands: writes.length, exactReceiptLookups: recoveries.length,
    realRateWindowPacing: pacing,
    native204InstrumentationEvents: native204Events, native204InstrumentationPredicate: 'edge_current_view_proven_204', native204InstrumentationFacts: native204Facts,
    catalogHistoryRestored: true, crossRouteBackPreserved: true, malformedHistoryCanonicalized: true,
    approvedPostalRead: true, recipientFieldsUnchanged: true, droppedCommittedResponses: dropped(), packagePublicId: config.publicId, screenshotDirectory: directory }));
}
