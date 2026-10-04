import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { OrderCheckoutDetails } from '../src/modules/ordering/order-checkout-details';
import { parseCheckoutReview, parseSubmittedOrder } from '../src/modules/ordering/order-submit-client';
import { family, sku, definition } from './catalog-fixtures';

const input = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version, priceId: '9007199254740999', priceVersion: '2', packages: 2 } ],
  delivery: { recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: null, instructions: 'Hubungi penerima sebelum tiba.' }, purchaseReference: 'KEBUTUHAN-001' };
const source = { input, review: 'a'.repeat(64), calculatedAt: definition.effectiveAt, currency: 'IDR', catalogSubtotal: '24000.50', tax: '0.00', charges: '1500.00', total: '25500.50', spendingState: 'Pending',
  lines: [{ familyId: family.id, familyName: family.name, skuCode: sku.code, definition,
    price: { id: input.selections[0].priceId, skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.25', effectiveAt: definition.effectiveAt },
    packages: 2, baseUnits: '24', catalogSubtotal: '24000.50' }] };

test('checkout displays exact validated package and price facts without a guessed product photograph or shipping charge', () => {
  const value = parseCheckoutReview(source, input);
  const html = renderToStaticMarkup(createElement(OrderCheckoutDetails, { value, reviewing: true }));
  for (const text of ['Pena biru', 'PENA-12', '2 Dus · 24 PCS', 'Isi per Dus: 12 PCS', 'IDR 12.000,25 / Dus', 'IDR 24.000,50', 'Total pesanan: IDR 25.500,50',
    'Hubungi penerima sebelum tiba.', 'KEBUTUHAN-001', 'Foto belum tersedia']) assert.ok(html.includes(text), text);
  assert.doesNotMatch(html, /<img|<input|<select|Gratis ongkir|Pembayaran berhasil|>null</);
  assert.match(html, /<dt>Pajak<\/dt><dd>IDR 0,00<\/dd>/);
  assert.match(html, /<dt>Biaya lain<\/dt><dd>IDR 1.500,00<\/dd>/);
});

test('saved checkout presents the immutable submitted prices without retaining a review action', () => {
  const value = parseSubmittedOrder({ ...source, buyerName: 'Cabang fixture',
    submission: { orderId: '9007199254741001', revision: '1', suborderIds: ['9007199254741002'], recordedAt: definition.effectiveAt, spendingState: 'Pending' },
    progress: { revision: '1', spendingState: 'Pending', acceptedSuborders: [], rejectedSuborders: [], cancelled: false } });
  const html = renderToStaticMarkup(createElement(OrderCheckoutDetails, { value, reviewing: false }, createElement('button', null, 'Konfirmasi ajukan pesanan')));
  assert.match(html, /Rincian pesanan tersimpan/);
  assert.match(html, /Nilai ini mengikuti rincian saat pesanan diajukan/);
  assert.match(html, /Total pesanan: IDR 25\.500,50/);
  assert.doesNotMatch(html, /<button|Periksa sebelum mengajukan/);
});
