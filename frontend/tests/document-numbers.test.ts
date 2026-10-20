import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matchesOrderNumber, orderNumber } from '../src/lib/document-numbers';

test('an order number is a typed prefix and the record number, padded so lists line up', () => {
  assert.equal(orderNumber('5201'), 'PSN-005201');
  assert.equal(orderNumber('42'), 'PSN-000042');
  assert.equal(orderNumber('1234567'), 'PSN-1234567');
  assert.throws(() => orderNumber('0'));
  assert.throws(() => orderNumber('52a1'));
});

test('staff can type an order number the way they read it, with or without the prefix, dash or zeros', () => {
  for (const typed of ['PSN-005201', 'psn005201', '5201', ' PSN 5201 ', '005201']) assert.equal(matchesOrderNumber(typed), '5201', typed);
  for (const typed of ['', 'PSN-', 'INV-5201', 'PSN-0', 'PSN-52O1']) assert.equal(matchesOrderNumber(typed), null, typed);
});
