// Browser proof uses the existing native Next/HTTPS/Kestrel/Mongo host and actual private evidence.
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

export async function verifyRefundPayoutBrowser({ config, recorder, user, origin, directory, draftWrites, payoutWrites, payoutRecoveries,
  dropNext, dropped, validateEvidence }) {
  const { page, context } = recorder;
  assert.notEqual(config.recorderId, config.approverId);
  async function open() {
    await page.getByRole('button', { name: 'Buka keluhan ' + config.caseId, exact: true }).click();
    const panel = page.getByRole('region', { name: 'Pencatatan bukti refund', exact: true }); await panel.waitFor();
    assert.equal(await page.getByRole('region', { name: 'Persetujuan finansial retur independen', exact: true }).count(), 0);
    await panel.getByRole('button', { name: 'Muat retur untuk pencatatan refund', exact: true }).click();
    await panel.getByRole('combobox', { name: 'Penerimaan barang retur untuk refund', exact: true }).selectOption(config.returnId);
    await panel.getByRole('button', { name: 'Buka persetujuan refund', exact: true }).click();
    return panel;
  }
  await page.goto(origin.origin + '/fulfillment/cases'); const panel = await open();
  const desk = panel.getByRole('region', { name: 'Dossier bukti refund', exact: true }); await desk.waitFor({ timeout: 60000 });
  await desk.getByLabel('Tanggal transfer refund', { exact: true }).fill(config.transferDate);
  await desk.getByLabel('Referensi transfer refund', { exact: true }).fill(config.transferReference);
  await desk.getByLabel('Alasan pencatatan refund', { exact: true }).fill('Draf bukti transfer refund privat untuk pemeriksaan bank');
  assert.equal(await desk.getByLabel(/Nomor rekening|Nilai refund|ID rekening/).count(), 0);
  await desk.getByRole('button', { name: 'Tinjau draf refund', exact: true }).click();
  let review = desk.getByRole('region', { name: 'Konfirmasi pencatatan refund', exact: true }); await review.waitFor({ timeout: 60000 });
  assert.equal(await review.getByRole('heading', { name: 'Simpan draf bukti refund', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('button', { name: 'Simpan draf refund', exact: true }).isDisabled(), true);
  await review.getByRole('button', { name: 'Kembali ke pemeriksaan refund', exact: true }).click(); assert.equal(draftWrites.length, 0);
  await desk.getByRole('button', { name: 'Tinjau draf refund', exact: true }).click(); await review.waitFor(); await review.getByRole('checkbox').check();
  await review.getByRole('button', { name: 'Simpan draf refund', exact: true }).click(); await desk.getByLabel('Gambar bukti transfer refund asli', { exact: true }).waitFor({ timeout: 60000 });
  assert.equal(draftWrites.length, 1); const draftWrite = draftWrites[0]; assert.equal(draftWrite.actor, config.recorderId);
  assert.equal(draftWrite.body.input.amount, config.reservedRefund); assert.equal(draftWrite.body.input.transferDate, config.transferDate); assert.equal(draftWrite.body.input.transferReference, config.transferReference);
  assert.match(draftWrite.key, new RegExp('^rpd\\.' + config.approvalId + '\\.[0-9a-f-]{36}$'));
  // A reload discovers the recorder's actual draft and its private reservations, without account input.
  await page.reload(); const resumed = await open(); await resumed.getByRole('button', { name: 'Lanjutkan draf refund tersimpan', exact: true }).click();
  await resumed.getByLabel('Gambar bukti transfer refund asli', { exact: true }).waitFor({ timeout: 60000 });
  await resumed.getByLabel('Alasan pencatatan bukti refund', { exact: true }).fill('Pencatatan bukti transfer refund privat untuk verifikasi bank independen');
  await resumed.getByLabel('Gambar bukti transfer refund asli', { exact: true }).setInputFiles(config.imagePath);
  await resumed.getByRole('button', { name: 'Unggah bukti refund asli', exact: true }).click();
  await resumed.getByText(/Gambar tersimpan; menunggu pemeriksaan keamanan/).waitFor({ timeout: 60000 });
  await validateEvidence(); // Actual owner scan/decoder pipeline; never replace statuses in the browser.
  await resumed.getByRole('button', { name: 'Muat status bukti refund', exact: true }).click();
  await resumed.getByText(/Gambar lolos pemeriksaan keamanan/).waitFor({ timeout: 60000 });
  const imageButton = resumed.getByRole('button', { name: /^Lihat bukti refund / }); await imageButton.click();
  const image = resumed.getByRole('img', { name: 'Bukti transfer refund asli untuk diperiksa', exact: true }); await image.waitFor();
  assert.equal(await image.evaluate(async element => { if (!element.complete) await new Promise(resolve => { element.addEventListener('load', resolve, { once: true }); element.addEventListener('error', resolve, { once: true }); }); return element.naturalWidth > 0; }), true);
  await resumed.getByRole('button', { name: 'Tutup gambar bukti refund', exact: true }).click();
  await resumed.getByRole('button', { name: /^Tinjau pencatatan bukti / }).click();
  review = resumed.getByRole('region', { name: 'Konfirmasi pencatatan refund', exact: true }); await review.waitFor();
  assert.equal(await review.getByRole('heading', { name: 'Catat bukti refund untuk verifikasi', exact: true }).evaluate(element => element === document.activeElement), true);
  assert.equal(await review.getByRole('textbox').count(), 0); assert.equal(await review.getByRole('button', { name: 'Catat refund belum diverifikasi', exact: true }).isDisabled(), true);
  await review.getByRole('checkbox').check(); await context.setOffline(true); await page.getByText(/Koneksi terputus/).waitFor();
  assert.equal(await review.getByRole('button', { name: 'Catat refund belum diverifikasi', exact: true }).isDisabled(), true); assert.equal(payoutWrites.length, 0);
  await context.setOffline(false); await page.getByText(/Koneksi terputus/).waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'refund-payout-review-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'refund-payout-review-desktop.png'), fullPage: true }); await page.setViewportSize({ width: 360, height: 800 });
  dropNext(); await review.getByRole('button', { name: 'Catat refund belum diverifikasi', exact: true }).click();
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ timeout: 60000 }); assert.equal(payoutWrites.length, 1);
  const write = payoutWrites[0]; assert.equal(write.actor, config.recorderId); assert.match(write.key, /^rpr\.[1-9][0-9]*\.[0-9a-f-]{36}$/);
  const references = () => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('unit-toko:pending:v1:')).map(key => JSON.parse(localStorage[key])));
  const pending = await references(); assert.equal(pending.length, 1); assert.equal(pending[0].operation, 'billing.refund.payout.record'); assert.equal(pending[0].key, write.key);
  assert.deepEqual(Object.keys(pending[0]).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.ok(!JSON.stringify(pending).includes(config.transferReference));
  assert.ok(!JSON.stringify(pending).includes(write.body.reference.sha256)); assert.ok(!JSON.stringify(pending).includes('privat')); assert.equal(await page.getByRole('button', { name: /Selesaikan dan tutup/ }).count(), 0);
  await context.addCookies([{ name: '__Host-ut_session', value: config.approverToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('heading', { name: /^(Sesi Anda perlu diperbarui|Halaman ini di luar tugas akun Anda|Halaman belum dapat dibuka)$/ }).waitFor(); assert.deepEqual(await references(), pending);
  await context.addCookies([{ name: '__Host-ut_session', value: config.recorderToken, domain: origin.hostname, path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
  await page.reload(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).click(); await page.getByRole('button', { name: 'Periksa hasil', exact: true }).waitFor({ state: 'hidden', timeout: 60000 });
  assert.equal(payoutWrites.length, 1); assert.equal(dropped(), 1); assert.equal((await references()).length, 0);
  assert.equal(payoutRecoveries.length, 2);
  for (const lookup of payoutRecoveries) { assert.equal(lookup.actor, config.recorderId); assert.equal(lookup.path, '/api/v1/billing/refund-payout-drafts/' + write.key.split('.')[1] + '/operations/query'); assert.deepEqual(lookup.body, { purpose: 'operation-recovery', key: write.key }); }
  const recorded = await open(); await recorded.getByRole('region', { name: 'Refund tercatat belum diverifikasi', exact: true }).waitFor();
  await recorded.getByText(/belum membuktikan uang diterima pelanggan/).waitFor(); assert.equal(await recorded.getByRole('button', { name: 'Tinjau draf refund', exact: true }).count(), 0);
  await page.screenshot({ path: resolve(directory, 'refund-payout-saved-mobile.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 }); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'refund-payout-saved-desktop.png'), fullPage: true });
  const approver = await user(config.approverToken); await approver.page.goto(origin.origin + '/fulfillment/cases');
  await approver.page.getByRole('button', { name: 'Buka keluhan ' + config.caseId, exact: true }).click(); assert.equal(await approver.page.getByRole('region', { name: 'Pencatatan bukti refund', exact: true }).count(), 0);
  console.log('REAL_REFUND_PAYOUT_BROWSER_PASS ' + JSON.stringify({ draftWrites: draftWrites.length, payoutWrites: payoutWrites.length, exactReservedAmount: config.reservedRefund,
    actualRecorder: config.recorderId, independentApproval: config.approverId, privateOriginalViewed: true, actualScanDecoded: true, recordedUnverified: true,
    actualOwnerRecovery: true, actorChangeDenied: true, noResend: true, noTransfer: true, noSettlement: true, explicitConsent: true, frozenReview: true, offlineUnsent: true, mobile: true, desktop: true }));
}
