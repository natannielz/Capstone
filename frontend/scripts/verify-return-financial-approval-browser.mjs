// UI proof against the existing native Next/HTTPS/Kestrel/Mongo host. Responses are never mocked.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
const recordNo = (prefix, id) => prefix + '-' + String(id).padStart(6, '0');

export async function verifyReturnFinancialApprovalBrowser({ config, finance, user, origin, directory, financialWrites, financialRecoveries, dropNext, dropped }) {
  const { page, context } = finance;
  assert.equal(config.action, 'refund'); assert.notEqual(config.financeId, config.makerId);
  async function open() {
    await page.getByRole('button', { name: 'Buka keluhan ' + recordNo('KLH', config.caseId), exact: true }).click();
    const panel = page.getByRole('region', { name: 'Persetujuan finansial retur independen', exact: true }); await panel.waitFor();
    // The Finance checker has no case.manage, credit.request, refund.request or stock maker grants.
    assert.equal(await page.getByRole('button', { name: 'Tinjau pengajuan penyelesaian', exact: true }).count(), 0);
    await panel.getByRole('button', { name: 'Muat penerimaan retur untuk Finance', exact: true }).click();
    await panel.getByRole('combobox', { name: /^Penerimaan barang retur/ }).selectOption(config.returnId);
    await panel.getByRole('combobox', { name: /^Penyelesaian yang diajukan/ }).selectOption(config.action);
    await panel.getByRole('button', { name: 'Buka pengajuan finansial', exact: true }).click();
    const review = panel.getByRole('region', { name: 'Pemeriksaan finansial penerimaan ' + recordNo('RTR', config.returnId), exact: true }); await review.waitFor();
    await review.getByRole('button', { name: 'Periksa pengajuan dan saldo terbaru', exact: true }).click();
    await review.getByText(/Pengajuan tetap:/).waitFor({ timeout: 60000 }); return review;
  }
  await page.goto(origin.origin + '/fulfillment/cases');
  const form = await open(); const amount = form.getByLabel('Nilai refund dari alokasi ' + config.originalAllocationId, { exact: true });
  await amount.fill('1000.00'); await form.getByLabel('Alasan persetujuan Finance', { exact: true }).fill('Persetujuan sumber retur dan reservasi Finance privat');
  await form.getByRole('button', { name: 'Tinjau persetujuan finansial', exact: true }).click();
  let preview = page.getByRole('region', { name: 'Konfirmasi persetujuan Finance', exact: true }); await preview.waitFor();
  assert.equal(await preview.getByRole('heading', { name: 'Setujui nota kredit dan reservasi refund', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await preview.getByRole('textbox').count(), 0); assert.equal(await preview.getByRole('spinbutton').count(), 0);
  assert.equal(await preview.getByRole('button', { name: 'Setujui persetujuan Finance', exact: true }).isDisabled(), true);
  await preview.getByRole('button', { name: 'Kembali ke pemeriksaan', exact: true }).click(); assert.equal(financialWrites.length, 0);
  assert.equal(await amount.inputValue(), '1000.00');
  await form.getByRole('button', { name: 'Tinjau persetujuan finansial', exact: true }).click(); await preview.waitFor();
  await preview.getByRole('checkbox').check();
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await preview.getByRole('button', { name: 'Setujui persetujuan Finance', exact: true }).isDisabled(), true); assert.equal(financialWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'return-financial-approval-review-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'return-financial-approval-review-desktop.png'), fullPage: true }); await page.setViewportSize({ width: 360, height: 800 });
  dropNext(); await preview.getByRole('button', { name: 'Setujui persetujuan Finance', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 });
  assert.equal(financialWrites.length, 1); const write = financialWrites[0]; assert.equal(write.actor, config.financeId);
  assert.match(write.key, new RegExp('^fa\\.' + config.caseId + '\\.' + config.returnId + '\\.refund\\.[0-9a-f-]{36}$'));
  assert.equal(write.body.action, 'refund'); assert.equal(write.body.input.expectedInvoiceVersion, config.expectedInvoiceVersion);
  assert.deepEqual(write.body.input.releases, [{ originalAllocationId: config.originalAllocationId, expectedReceiptVersion: config.expectedReceiptVersion, amount: '1000.00' }]);
  const references = () => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const pending = await references(); assert.equal(pending.length, 1); assert.equal(pending[0].operation, 'billing.return.financial.approve'); assert.equal(pending[0].key, write.key);
  assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pending).includes('privat')); assert.ok(!JSON.stringify(pending).includes('1000.00'));
  assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  assert.equal(await form.getByRole('button', { name: 'Periksa pengajuan dan saldo terbaru', exact: true }).isDisabled(), true);
  // Change to the real maker's separate session. The cached checker cannot look up with the old actor header.
  await context.addCookies([{ name: '__Host-ut_session', value: config.makerToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(), pending); assert.equal(financialWrites.length, 1);
  await context.addCookies([{ name: '__Host-ut_session', value: config.financeToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 });
  assert.equal(financialWrites.length, 1); assert.equal(dropped(), 1); assert.equal((await references()).length, 0);
  assert.equal(financialRecoveries.length, 2);
  for (const lookup of financialRecoveries) {
    assert.equal(lookup.path, '/api/v1/billing/cases/' + config.caseId + '/returns/' + config.returnId + '/financial-approval/operations/query');
    assert.equal(lookup.actor, config.financeId); assert.deepEqual(lookup.body, { purpose: 'operation-recovery', action: 'refund', requestKey: write.key });
  }
  const saved = await open(); await saved.getByRole('region', { name: 'Persetujuan finansial tercatat', exact: true }).waitFor();
  await saved.getByText(/uang belum dikirim/).waitFor(); assert.equal(await saved.getByRole('button', { name: 'Tinjau persetujuan finansial', exact: true }).count(), 0);
  assert.equal(await saved.getByLabel('Alasan persetujuan Finance', { exact: true }).count(), 0);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); await page.screenshot({ path: resolve(directory, 'return-financial-approval-saved-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'return-financial-approval-saved-desktop.png'), fullPage: true });
  const maker = await user(config.makerToken); await maker.page.goto(origin.origin + '/fulfillment/cases'); await maker.page.getByRole('button', { name: 'Buka keluhan ' + recordNo('KLH', config.caseId), exact: true }).click();
  assert.equal(await maker.page.getByRole('region', { name: 'Persetujuan finansial retur independen', exact: true }).count(), 0);
  const outsider = await user(config.courierToken); await outsider.page.goto(origin.origin + '/fulfillment/cases'); await outsider.page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor();
  assert.equal(await outsider.page.getByRole('button', { name: /^Buka keluhan/ }).count(), 0);
  console.log('REAL_RETURN_FINANCIAL_APPROVAL_BROWSER_PASS ' + JSON.stringify({ approvals: financialWrites.length, actualIndependentActor: config.financeId,
    originalAllocation: config.originalAllocationId, exactOwnerRecovery: true, lostOriginalRecovered: true, currentActorDenied: true, noResend: true, noPayout: true,
    explicitConsent: true, frozenPreview: true, offlineUnsent: true, privateMetadataOnly: true, makerCannotApprove: true, outsiderDenied: true, mobile: true, desktop: true }));
}
