// A read-only helper for the genuine signed-in browser rehearsal. No API interception or synthetic profile responses.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyWorkspaceDirectoryBrowser({ page, origin, directory, expectedLinks, search, actorId }) {
  const deadline = performance.now() + 60000, remaining = () => { const milliseconds = Math.ceil(deadline - performance.now()); assert.ok(milliseconds > 0, 'Workspace directory did not finish its actual identity/access reads'); return milliseconds; };
  const requests = [], observe = request => { if (new URL(request.url()).pathname.startsWith('/api/v1/')) requests.push({ method: request.method(), path: new URL(request.url()).pathname }); };
  page.on('request', observe);
  try {
    const [sessionResponse, accessResponse] = await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/identity/session', { timeout: remaining() }),
      page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/access/current', { timeout: remaining() }),
      page.goto(origin.origin + '/workspace', { timeout: remaining() }),
    ]);
    assert.equal(sessionResponse.status(), 200); assert.equal(accessResponse.status(), 200);
    const [session, current] = await Promise.all([sessionResponse.json(), accessResponse.json()]); assert.equal(session.authenticated, true); assert.equal(session.account.userId, actorId); assert.equal(current.userId, actorId);
    assert.equal(new URL(page.url()).pathname, '/identity/account'); assert.equal(new URL(page.url()).hash, '#workspace-heading');
    await page.getByRole('heading', { name: 'Ruang kerja tersedia', exact: true }).waitFor({ timeout: remaining() });
    const navigation = page.getByRole('navigation', { name: 'Ruang kerja akun', exact: true }); await navigation.waitFor({ timeout: remaining() });
    const visible = await navigation.getByRole('link').evaluateAll(elements => elements.map(element => element.getAttribute('href'))); assert.deepEqual([...visible].sort(), [...expectedLinks].sort()); assert.equal(new Set(visible).size, visible.length);
    const searchInput = page.getByRole('searchbox', { name: 'Cari tugas', exact: true }); await searchInput.focus(); assert.equal(await searchInput.evaluate(element => element === document.activeElement), true);
    await searchInput.fill(search.text); const selected = navigation.locator('a[href="' + search.href + '"]'); await selected.waitFor();
    await selected.focus(); assert.equal(await selected.evaluate(element => element === document.activeElement), true);
    await searchInput.fill('tidak-ada-tugas-yang-cocok-000'); await page.getByText('Tidak ada tugas yang cocok. Coba kata lain atau hapus pencarian untuk melihat tugas yang tersedia.', { exact: true }).waitFor(); assert.equal(await page.getByRole('navigation', { name: 'Ruang kerja akun', exact: true }).count(), 0);
    await page.getByRole('button', { name: 'Hapus pencarian', exact: true }).click(); assert.equal(await searchInput.inputValue(), ''); await navigation.waitFor();
    assert.deepEqual((await navigation.getByRole('link').evaluateAll(elements => elements.map(element => element.getAttribute('href')))).sort(), [...expectedLinks].sort());
    const section = page.getByRole('navigation', { name: 'Bidang kerja', exact: true }).getByRole('link').first(); const anchor = await section.getAttribute('href'); await section.focus(); await page.keyboard.press('Enter'); assert.equal(new URL(page.url()).hash, anchor);
    await page.setViewportSize({ width: 360, height: 800 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'workspace-directory-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1440, height: 1000 }); await page.screenshot({ path: resolve(directory, 'workspace-directory-desktop.png'), fullPage: true });
    assert.ok(requests.length >= 2); assert.ok(requests.every(request => request.method === 'GET' && ['/api/v1/identity/session', '/api/v1/access/current'].includes(request.path)));
    console.log('REAL_WORKSPACE_DIRECTORY_BROWSER_PASS ' + JSON.stringify({ actualActor: true, livePermissions: true, canonicalEntry: true, localSearch: true, keyboard: true, mobile: true, desktop: true, mutations: 0 }));
  } finally { page.off('request', observe); }
}

export async function verifyWorkspaceDirectoryWithoutSession({ page, origin }) {
  await page.goto(origin.origin + '/workspace'); await page.getByRole('heading', { name: 'Masuk untuk melanjutkan', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(await page.getByRole('navigation', { name: 'Ruang kerja akun', exact: true }).count(), 0); assert.equal(await page.getByRole('searchbox', { name: 'Cari tugas', exact: true }).count(), 0);
}
