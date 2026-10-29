import assert from 'node:assert/strict';
import { openAccountFromFrame, returnToWorkspacePage } from './workspace-rehearsal.mjs';

// Actual signed provider callback and server session rotation; no replacement fixture token.
export async function renewValuationActor(participant, actorId, operation, origin) {
  const { page, context } = participant;
  await page.goto(origin.origin + '/procurement/stock-valuations');
  const session = () => page.evaluate(async () => { const response = await fetch('/api/v1/identity/session', { credentials: 'same-origin', cache: 'no-store' }); if (!response.ok) throw new Error('Actual valuation session unavailable'); return response.json(); });
  const before = await session(), originalCookie = (await context.cookies(origin.origin)).find(item => item.name === '__Host-ut_session'); assert.equal(before.account.userId, actorId); assert.ok(originalCookie);
  await openAccountFromFrame(page); await page.getByRole('heading', { name: 'Verifikasi tindakan penting', exact: true }).waitFor({ timeout: 60000 });
  const actions = page.getByRole('combobox', { name: 'Tindakan', exact: true }); await actions.locator(`option[value="${operation}"]`).waitFor({ state: 'attached', timeout: 60000 }); await actions.selectOption(operation);
  const callback = page.waitForResponse(response => new URL(response.url()).pathname === '/auth/callback', { timeout: 60000 }), navigation = page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.getByRole('button', { name: 'Verifikasi dua langkah', exact: true }).click(); assert.ok([302, 303].includes((await callback).status())); await navigation; assert.equal(new URL(page.url()).pathname, '/identity/account');
  const after = await session(), rotated = (await context.cookies(origin.origin)).find(item => item.name === '__Host-ut_session'); assert.equal(after.account.userId, actorId); assert.equal(after.absoluteExpiresAt, before.absoluteExpiresAt); assert.ok(Date.parse(after.factorCompletedAt) > Date.parse(before.factorCompletedAt)); assert.ok(Date.parse(after.freshFactorUntil) > Date.parse(after.serverTime)); assert.ok(rotated); assert.notEqual(rotated.value, originalCookie.value);
  await returnToWorkspacePage(page, '/procurement/stock-valuations');
  console.log('MIXED_QUALITY_VALUATION_STEP_UP ' + JSON.stringify({ actorId, operation, actualRotation: true, absoluteExpiryPreserved: true }));
}
