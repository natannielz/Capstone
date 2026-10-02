// Real Next/HTTPS/API/Mongo verification of a recorded private refund and actual bank debit.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyRefundSettlementBrowser({ config, verifier, user, origin, directory, settlementWrites, settlementRecoveries, originalReads, dropNext, dropped }) {
  const { page, context } = verifier;
  assert.notEqual(config.verifierId, config.recorderId); assert.notEqual(config.verifierId, config.importerId);
  async function open() {
    await page.getByRole('button', { name: 'Buka keluhan ' + config.caseId, exact: true }).click();
    const panel = page.getByRole('region', { name: 'Verifikasi debit bank refund', exact: true }); await panel.waitFor();
    assert.equal(await page.getByRole('region', { name: 'Pencatatan bukti refund', exact: true }).count(), 0);
    await panel.getByLabel('ID pencatatan refund', { exact: true }).fill(config.payoutId);
    await panel.getByRole('button', { name: 'Buka refund untuk pemeriksaan bank', exact: true }).click();
    await panel.getByText('Bukti refund telah dicatat; pembayaran belum diverifikasi.', { exact: true }).waitFor({ timeout: 60000 });
    return panel;
  }
  await page.goto(origin.origin + '/fulfillment/cases'); let panel = await open();
  assert.equal(await panel.getByRole('button', { name: 'Tinjau verifikasi debit refund', exact: true }).isDisabled(), true);
  await panel.getByRole('button', { name: 'Lihat bukti asli untuk verifikasi refund', exact: true }).click();
  const image = panel.getByRole('img', { name: 'Bukti transfer refund asli untuk dibandingkan dengan mutasi bank', exact: true }); await image.waitFor({ timeout: 60000 });
  assert.equal(await image.evaluate(async element => {
    if (!element.complete) await new Promise(resolve => { element.addEventListener('load', resolve, { once: true }); element.addEventListener('error', resolve, { once: true }); });
    return element.naturalWidth > 0;
  }), true);
  assert.equal(originalReads.length, 1); assert.equal(originalReads[0].actor, config.verifierId);
  assert.deepEqual(originalReads[0].body, { purpose: 'refund-settlement-evidence-view', reference: config.evidence });
  assert.equal(originalReads[0].path, '/api/v1/billing/refund-payouts/' + config.payoutId + '/settlement/evidence/' + config.reservationId + '/original/query');
  await panel.getByRole('button', { name: 'Tutup bukti asli refund', exact: true }).click();
  await panel.getByLabel('ID impor mutasi bank untuk debit refund', { exact: true }).fill(config.bankImportId);
  await panel.getByLabel('Nomor baris debit pada impor', { exact: true }).fill(String(config.bankRowNumber));
  await panel.getByLabel('Referensi pemeriksaan penerima refund', { exact: true }).fill(config.beneficiaryReference);
  await panel.getByLabel('Referensi pemeriksaan transfer dan debit bank', { exact: true }).fill(config.transferReference);
  await panel.getByLabel('Alasan verifikasi debit refund', { exact: true }).fill('Pemeriksaan independen bukti asli, penerima dan debit bank refund');
  await panel.getByRole('button', { name: 'Tinjau verifikasi debit refund', exact: true }).click();
  let review = panel.getByRole('region', { name: 'Konfirmasi verifikasi debit refund', exact: true }); await review.waitFor({ timeout: 60000 });
  assert.equal(await review.getByRole('heading', { name: 'Konfirmasi debit bank yang telah diperiksa', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('spinbutton').count(), 0);
  assert.equal(await review.getByRole('button', { name: 'Simpan verifikasi debit refund', exact: true }).isDisabled(), true);
  await review.getByRole('button', { name: 'Kembali memeriksa', exact: true }).click(); assert.equal(settlementWrites.length, 0);
  await panel.getByRole('button', { name: 'Tinjau verifikasi debit refund', exact: true }).click(); await review.waitFor({ timeout: 60000 });
  await review.getByRole('checkbox').check();
  await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await review.getByRole('button', { name: 'Simpan verifikasi debit refund', exact: true }).isDisabled(), true); assert.equal(settlementWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'refund-settlement-review-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'refund-settlement-review-desktop.png'), fullPage: true }); await page.setViewportSize({ width: 360, height: 800 });
  dropNext(); await review.getByRole('button', { name: 'Simpan verifikasi debit refund', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 }); assert.equal(settlementWrites.length, 1);
  const write = settlementWrites[0]; assert.equal(write.actor, config.verifierId); assert.match(write.key, new RegExp('^rsv\\.' + config.payoutId + '\\.[0-9a-f-]{36}$'));
  assert.deepEqual(write.body.input, { bankImportId: config.bankImportId, bankRowNumber: config.bankRowNumber,
    beneficiaryVerificationReference: config.beneficiaryReference, transferVerificationReference: config.transferReference });
  assert.equal(write.body.attested, true); assert.match(write.body.binding, /^[0-9a-f]{64}$/);
  const references = () => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const pending = await references(); assert.equal(pending.length, 1); assert.equal(pending[0].operation, 'billing.refund.payout.verify'); assert.equal(pending[0].key, write.key);
  assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [config.evidence.sha256, config.beneficiaryReference, config.transferReference, write.body.binding]) assert.ok(!JSON.stringify(pending).includes(privateValue));
  assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  await context.addCookies([{ name: '__Host-ut_session', value: config.recorderToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: 'Periksa sesi dan akses', exact: true }).waitFor(); assert.deepEqual(await references(), pending);
  await context.addCookies([{ name: '__Host-ut_session', value: config.verifierToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 });
  assert.equal(settlementWrites.length, 1); assert.equal(dropped(), 1); assert.equal((await references()).length, 0);
  assert.equal(settlementRecoveries.length, 2);
  for (const lookup of settlementRecoveries) {
    assert.equal(lookup.actor, config.verifierId); assert.equal(lookup.path, '/api/v1/billing/refund-payouts/' + config.payoutId + '/settlement/operations/query');
    assert.deepEqual(lookup.body, { purpose: 'operation-recovery', key: write.key });
  }
  await page.getByRole('button', { name: 'Buka keluhan ' + config.caseId, exact: true }).click();
  panel = page.getByRole('region', { name: 'Verifikasi debit bank refund', exact: true }); await panel.waitFor();
  await panel.getByLabel('ID pencatatan refund', { exact: true }).fill(config.payoutId); await panel.getByRole('button', { name: 'Buka refund untuk pemeriksaan bank', exact: true }).click();
  const saved = panel.getByRole('region', { name: 'Hasil verifikasi debit refund', exact: true }); await saved.waitFor({ timeout: 60000 });
  await saved.getByRole('heading', { name: 'Debit bank refund terverifikasi', exact: true }).waitFor();
  const receipts = saved.getByRole('listitem'); assert.equal(await receipts.count(), config.receipts.length);
  for (const expected of config.receipts) {
    const receipt = receipts.filter({ hasText: 'Penerimaan bank ' + expected.receiptId + ':' }); assert.equal(await receipt.count(), 1);
    const format = amount => { const [whole, fraction] = amount.split('.'); return 'IDR ' + BigInt(whole).toLocaleString('id-ID') + ',' + fraction; };
    const text = (await receipt.innerText()).replace(/\s/g, '');
    for (const value of [format(expected.paid), format(expected.pending), format(expected.available)]) assert.ok(text.includes(value.replace(/\s/g, '')));
  }
  assert.equal(await panel.getByRole('button', { name: 'Tinjau verifikasi debit refund', exact: true }).count(), 0);
  await page.screenshot({ path: resolve(directory, 'refund-settlement-saved-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'refund-settlement-saved-desktop.png'), fullPage: true });
  const recorder = await user(config.recorderToken); await recorder.page.goto(origin.origin + '/fulfillment/cases');
  await recorder.page.getByRole('button', { name: 'Buka keluhan ' + config.caseId, exact: true }).click();
  assert.equal(await recorder.page.getByRole('region', { name: 'Verifikasi debit bank refund', exact: true }).count(), 0);
  console.log('REAL_REFUND_SETTLEMENT_BROWSER_PASS ' + JSON.stringify({ writes: settlementWrites.length, amount: config.amount,
    independentVerifier: config.verifierId, recorder: config.recorderId, importer: config.importerId, actualPrivateOriginal: true, actualBankDebit: true,
    paidOnlyAfterSettlement: true, exactOwnerRecovery: true, actorChangeDenied: true, noResend: true, frozenReview: true, explicitConsent: true, offlineUnsent: true, mobile: true, desktop: true }));
}
