import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matchesOrderNumber, matchesRecordNumber, orderNumber, recordNumber } from '../src/lib/document-numbers';

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

test('records without a printed document get their own typed prefix, so a return never reads like an order', () => {
  assert.equal(recordNumber('case', '17'), 'KLH-000017');
  assert.equal(recordNumber('return', '3'), 'RTR-000003');
  assert.equal(recordNumber('purchaseRequest', '120'), 'PP-000120');
  assert.equal(recordNumber('purchaseOrder', '9'), 'SP-000009');
  assert.equal(recordNumber('order', '5201'), orderNumber('5201'));
  assert.throws(() => recordNumber('return', '0'));
});

test('F-026: a branch request reads PB- with six digits like the others; the server PT- reference still finds it', () => {
  assert.equal(recordNumber('branchRequest', '1'), 'PB-000001');
  assert.equal(recordNumber('branchRequest', '88'), 'PB-000088');
  assert.equal(recordNumber('branchRequest', '1234567'), 'PB-1234567');
  for (const typed of ['PB-000088', 'pb 88', '88', 'PT-00000088', 'pt00000088']) assert.equal(matchesRecordNumber('branchRequest', typed), '88', typed);
  for (const typed of ['PSN-000088', 'PP-000088', 'PB-', 'PB-0']) assert.equal(matchesRecordNumber('branchRequest', typed), null, typed);
  // The PT- reference belongs to branch requests only.
  assert.equal(matchesRecordNumber('order', 'PT-00000088'), null);
});

test('a typed number matches only its own record type; a bare number matches any type', () => {
  assert.equal(matchesRecordNumber('return', 'rtr-000003'), '3');
  assert.equal(matchesRecordNumber('return', '3'), '3');
  assert.equal(matchesRecordNumber('purchaseOrder', 'SP 9'), '9');
  assert.equal(matchesRecordNumber('purchaseRequest', 'PP-000120'), '120');
  assert.equal(matchesRecordNumber('return', 'PSN-000003'), null);
  assert.equal(matchesRecordNumber('purchaseOrder', 'PP-000009'), null);
  assert.equal(matchesRecordNumber('case', 'KLH-'), null);
});
