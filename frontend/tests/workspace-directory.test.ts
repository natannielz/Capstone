import { test } from 'node:test';
import assert from 'node:assert/strict';
import { workspaceDirectory } from '../src/modules/identity/workspace-directory';
import { identityDestinations } from '../src/modules/identity/identity-workflow';

const links = (actions: readonly string[], query = '') => workspaceDirectory(actions, query).flatMap(group => group.items.map(item => item.href));

test('workspace groups preserve real discovery and do not grant tasks from role names or unrelated permissions', () => {
  assert.deepEqual(workspaceDirectory([]), []);
  for (const role of ['admin', 'finance', 'customer', 'store-staff', '680', 'procurement.container.receive']) assert.deepEqual(workspaceDirectory([role]), []);
  assert.deepEqual(links(['procurement.read']), ['/procurement/receiving', '/procurement/purchase-orders', '/procurement/requests']);
  assert.deepEqual(links(['payment.evidence']), []); assert.deepEqual(links(['payment.evidence', 'invoice.read']).sort(), ['/billing/invoices', '/billing/payments']);
  assert.deepEqual(links(['procurement.read', 'procurement.read']), links(['procurement.read']));
  assert.deepEqual(links(['procurement.read'], 'rekening'), []);
});

test('available routes appear exactly once under their business area, including catalog administration and receiving', () => {
  const permissions = ['order.read', 'order.submit', 'order.accept', 'order.spending.policy.read', 'case.read', 'shipment.prepare', 'shipment.dispatch', 'shipment.assign', 'shipment.read', 'receipt.accept', 'invoice.read', 'payment.evidence', 'payment.review.read', 'bank.receipt.read', 'bank.statement.read', 'receiving.account.read', 'supplier.change.propose', 'supplier.bank.read', 'procurement.read', 'procurement.disbursement.read', 'procurement.settlement.read', 'funding.read', 'accounting.book.read', 'catalog.read', 'catalog.manage', 'pricing.manage', 'stock.source.read', 'stock.count', 'invite.manage', 'access.audit.read', 'person.review.read', 'organization.address.read'];
  const groups = workspaceDirectory(permissions), paths = links(permissions);
  assert.deepEqual([...paths].sort(), identityDestinations(permissions).map(item => item.href).sort()); assert.equal(new Set(paths).size, paths.length);
  assert.ok(groups.find(group => group.label === 'Pengadaan dan pemasok')?.items.some(item => item.href === '/procurement/receiving'));
  assert.ok(groups.find(group => group.label === 'Katalog dan persediaan')?.items.some(item => item.href === '/administration/catalog'));
  assert.equal(groups.find(group => group.label === 'Organisasi dan akses')?.items.some(item => item.href === '/administration/catalog'), false);
});

test('local search matches user-facing words across case and spacing without exposing identifiers or unavailable tasks', () => {
  const actions = ['procurement.read', 'invoice.read', 'catalog.manage'];
  assert.deepEqual(links(actions, '   PENERIMAAN   '), ['/procurement/receiving', '/billing/invoices']);
  assert.deepEqual(links(actions, 'pengadaan kemasan').sort(), ['/procurement/purchase-orders', '/procurement/requests']);
  assert.deepEqual(links(actions, 'penerimaan gudang'), []);
  assert.deepEqual(links(actions, 'procurement.read'), []);
  assert.deepEqual(links(actions, 'supplier.bank.read'), []);
  assert.deepEqual(links(actions, '０００９'), []);
  assert.deepEqual(links(actions, ' 	 '), links(actions));
});
