import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkDelivery, deliveryFromApproved, emptyDelivery } from '../src/modules/ordering/order-delivery';

const filled = { recipientName: ' Rina Pratiwi ', phone: '0812-3456-7890', address: 'Jl. Sudirman 1\nLantai 3', city: 'Jakarta', postalCode: '10220', instructions: '', purchaseReference: ' PO-2026-114 ' };

test('a complete delivery is trimmed into the saved draft shape, and empty optional fields become null', () => {
  const result = checkDelivery(filled);
  assert.ok('delivery' in result);
  assert.deepEqual(result.delivery, { recipientName: 'Rina Pratiwi', phone: '0812-3456-7890', address: 'Jl. Sudirman 1\nLantai 3', city: 'Jakarta', postalCode: '10220', instructions: null });
  assert.equal(result.purchaseReference, 'PO-2026-114');
  const plain = checkDelivery({ ...filled, postalCode: ' ', purchaseReference: '' });
  assert.ok('delivery' in plain); assert.equal(plain.delivery.postalCode, null); assert.equal(plain.purchaseReference, null);
});

test('each missing or malformed field gets its own message, next to the field', () => {
  const result = checkDelivery({ ...emptyDelivery, phone: '12', postalCode: '1234' });
  assert.ok('errors' in result);
  assert.deepEqual(Object.keys(result.errors).sort(), ['address', 'city', 'phone', 'postalCode', 'recipientName']);
  assert.match(result.errors.phone!, /7 sampai 15 angka/);
  assert.match(result.errors.postalCode!, /5 angka/);
  assert.ok('errors' in checkDelivery({ ...filled, phone: '0812 abc 7890' }));
  assert.ok('errors' in checkDelivery({ ...filled, recipientName: 'x'.repeat(161) }));
});

test('the branch address approved by the organisation fills the address, never the recipient', () => {
  const fields = deliveryFromApproved({ ...emptyDelivery, recipientName: 'Rina' }, { line1: 'Jl. Sudirman 1', line2: null, city: 'Jakarta Pusat', region: 'DKI Jakarta', postalCode: '10220', countryCode: 'ID' });
  assert.equal(fields.recipientName, 'Rina'); assert.equal(fields.phone, '');
  assert.equal(fields.address, 'Jl. Sudirman 1'); assert.equal(fields.city, 'Jakarta Pusat'); assert.equal(fields.postalCode, '10220');
  assert.equal(deliveryFromApproved(emptyDelivery, { line1: 'Jl. A', line2: 'Blok B', city: 'Bogor', region: null, postalCode: null, countryCode: 'ID' }).address, 'Jl. A\nBlok B');
});
