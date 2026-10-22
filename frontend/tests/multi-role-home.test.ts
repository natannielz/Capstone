import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buyerTasks, destinationHrefs, destinationLabel, homeFor, identityDestinations, isStaffActor, storeDoor } from '../src/modules/identity/identity-workflow';
import { workspaceNavigation } from '../src/modules/identity/workspace-navigation';
import { storeLinks, storeMember } from '../src/components/store/store-navigation';

// Role templates as granted by the server (backend/src/UnitToko.Modules/Identity/RoleTemplates.cs) plus each persona's extra grants
// (docs/quality/website-ready/journeys.md).
const buyer = ['catalog.read', 'order.read', 'order.submit', 'order.substitute', 'order.cancel', 'shipment.read', 'receipt.accept', 'invoice.read', 'case.read', 'case.request', 'payment.evidence'];
const pemohon = [...buyer, 'order.request.read', 'order.request.submit', 'substitution.accept', 'organization.address.read'];
const penyetujuCabang = [...buyer, 'order.spend.approve'];
const storeStaff = ['catalog.read', 'order.read', 'stock.read', 'stock.receive', 'stock.count', 'stock.adjust.propose', 'shipment.read', 'shipment.prepare', 'shipment.dispatch',
  'procurement.read', 'procurement.prepare', 'source.prepare', 'expense.request'];
const stafPembeli = [...storeStaff, 'order.submit'];
const courier = ['shipment.read', 'delivery.attempt', 'delivery.evidence', 'delivery.return', 'expense.request'];

const navigationHrefs = (actions: readonly string[]) => workspaceNavigation(actions, '/workspace').flatMap(group => group.items.map(item => item.href.split('#')[0]));

test('staf-pembeli lands on /workspace with a "Belanja untuk cabang" door (Review Focus 3, G-020)', () => {
  assert.equal(isStaffActor(stafPembeli), true);
  assert.equal(homeFor(stafPembeli), '/workspace');
  assert.deepEqual(storeDoor(stafPembeli) && { href: storeDoor(stafPembeli)!.href, label: storeDoor(stafPembeli)!.label }, { href: '/shop', label: 'Belanja untuk cabang' });
  const store = workspaceNavigation(stafPembeli, '/workspace').find(group => group.id === 'store');
  assert.deepEqual(store?.items.map(item => [item.href, item.label]), [['/shop', 'Belanja untuk cabang']]);
  // In the storefront the same person keeps the door back to their work.
  assert.equal(storeLinks(storeMember('Sari', stafPembeli), false, '/shop').staffDoor, '/workspace');
});

test('navigation lists only the pages the grants open; buying stays behind the one store door (R2, G-018, G-031)', () => {
  const hrefs = navigationHrefs(stafPembeli);
  const granted = new Set(['/workspace', '/identity/account', '/shop', ...identityDestinations(stafPembeli).map(item => item.href)]);
  assert.deepEqual(hrefs.filter(href => !granted.has(href)), []);
  assert.ok(hrefs.includes('/fulfillment/dispatches'), 'the role that dispatches finds the dispatch page');
  for (const href of ['/fulfillment/courier-assignments', '/billing/invoices', '/catalog/prices', '/administration/access', '/ordering/drafts', '/ordering/orders', '/ordering/requests'])
    assert.equal(hrefs.includes(href), false, href);
});

test('a standard branch buyer is not staff: no staff door, home is the store, and their tasks are in the store menu (G-017, F-002, G-018)', () => {
  for (const actions of [buyer, pemohon, penyetujuCabang]) {
    assert.equal(isStaffActor(actions), false);
    assert.equal(homeFor(actions), '/shop');
    const member = storeMember('Rina', actions);
    assert.equal(member.staff, false);
    assert.equal(storeLinks(member, false, '/shop').staffDoor, null);
  }
  const tasks = buyerTasks(pemohon).map(item => item.href);
  for (const href of ['/fulfillment/receive', '/fulfillment/shipments', '/fulfillment/cases', '/billing/invoices', '/billing/payments']) assert.ok(tasks.includes(href), href);
  assert.ok(buyerTasks(penyetujuCabang).some(item => item.href === '/ordering/reviews'));
  assert.equal(buyerTasks(stafPembeli).some(item => item.href === '/billing/invoices'), false);
});

test('a buyer on a task page in the workspace frame has a clear way back to the store (G-018)', () => {
  const groups = workspaceNavigation(pemohon, '/billing/invoices');
  assert.deepEqual([groups[0].items[0].href, groups[0].items[0].label], ['/shop', 'Kembali ke toko']);
  assert.equal(navigationHrefs(pemohon).includes('/workspace'), false);
});

test('back-office roles are staff by the pages their grants open, never by what is missing from a buyer list', () => {
  assert.equal(isStaffActor(courier), true);
  assert.equal(homeFor(courier), '/workspace');
  assert.equal(isStaffActor(storeStaff), true);
  assert.equal(isStaffActor(['catalog.read', 'catalog.manage']), true);
  assert.equal(isStaffActor([...buyer, 'invoice.issue']), true);
  assert.equal(homeFor([]), '/identity/account');
});

test('"Delegasi sementara" is listed only for staff, never for guests or pure buyers (G-028)', () => {
  assert.deepEqual(identityDestinations([]).map(item => item.href), []);
  assert.equal(identityDestinations(pemohon).some(item => item.href === '/administration/delegations'), false);
  assert.equal(identityDestinations(stafPembeli).some(item => item.href === '/administration/delegations'), true);
});

test('one name per page (G-019)', () => {
  const labels = destinationHrefs().map(href => destinationLabel(href));
  assert.equal(new Set(labels).size, labels.length);
  assert.equal(destinationLabel('/ordering/spending-policies'), 'Aturan persetujuan belanja');
  assert.equal(buyerTasks(penyetujuCabang).find(item => item.href === '/ordering/reviews')?.label, destinationLabel('/ordering/reviews'));
});

test('a buyer who may cancel reaches the page where cancelling happens, from the store menu and from the order (M1)', async () => {
  assert.ok(buyerTasks(buyer).some(item => item.href === '/ordering/reviews'), 'order.cancel opens /ordering/reviews as a buyer task');
  assert.equal(isStaffActor(buyer), false);
  const { readFileSync } = await import('node:fs');
  const history = readFileSync(new URL('../src/modules/ordering/OrderHistoryWorkspace.tsx', import.meta.url), 'utf8');
  assert.match(history, /account\.actions\.includes\('order\.cancel'\) && !selected\.progress\.cancelled && <p><Link href="\/ordering\/reviews"/);
});

test('the branch spending approver is a buyer-side role: home is the store, approvals reachable from it (G-043)', () => {
  const approver = [...penyetujuCabang, 'order.spending.policy.read'];
  assert.equal(isStaffActor(approver), false);
  assert.equal(homeFor(approver), '/shop');
  const tasks = buyerTasks(approver).map(item => item.href);
  assert.ok(tasks.includes('/ordering/reviews')); assert.ok(tasks.includes('/ordering/spending-policies'));
  assert.equal(storeLinks(storeMember('Andi', approver), false, '/shop').staffDoor, null);
  // Staff with real back-office grants stay staff.
  const storeHead = ['catalog.read', 'catalog.manage', 'pricing.manage', 'order.read', 'stock.read', 'shipment.read', 'shipment.assign', 'case.read', 'case.manage', 'procurement.read'];
  const peninjauToko = [...storeHead, 'order.accept', 'order.request.read', 'order.request.decide'];
  assert.equal(isStaffActor(peninjauToko), true); assert.equal(homeFor(peninjauToko), '/workspace');
  assert.equal(isStaffActor([...approver, 'order.spending.policy.approve']), true, 'proposing or approving spending rules is back-office work');
  assert.equal(isStaffActor(stafPembeli), true);
});
