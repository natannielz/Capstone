import {test} from 'node:test';
import assert from 'node:assert/strict';
import {catalogTabCurrent, storeMember, storeLinks, storeSignInHref} from '../src/components/store/store-navigation';

const buyer = ['catalog.read', 'order.request.read', 'order.request.submit', 'branch.pic', 'order.submit', 'order.read', 'invoice.read'];

test('buyers, approvers and staff are told apart by their permissions, never by their name', () => {
  assert.deepEqual(storeMember('Rina', buyer), {kind: 'member', name: 'Rina', staff: false, approver: false, orders: true, requests: true, tasks: [{href: '/billing/invoices', label: 'Tagihan'}]});
  const andi = storeMember('Andi', [...buyer, 'order.spend.approve']);
  assert.deepEqual({staff: andi.staff, approver: andi.approver, orders: andi.orders}, {staff: false, approver: true, orders: true});
  assert.ok(andi.tasks.some(task => task.href === '/ordering/reviews'));
  assert.equal(storeMember('Petugas', ['catalog.read', 'catalog.manage']).staff, true);
  assert.equal(storeMember('Petugas', ['catalog.read', 'catalog.manage']).orders, false);
  assert.equal(storeMember('Peninjau', ['catalog.read', 'pricing.approve']).staff, true);
});

test('signing in returns to the page the person was on, and staff enter their back office', () => {
  assert.equal(storeSignInHref('/shop'), '/identity/login?returnPath=%2Fshop');
  assert.equal(storeSignInHref('/workspace'), '/identity/login?returnPath=%2Fworkspace');
});

test('guests get the catalog and a sign-in door; cart and orders ask them to sign in first', () => {
  const links = storeLinks({kind: 'guest'}, false, '/shop');
  assert.equal(links.home, '/');
  assert.equal(links.catalog, '/katalog');
  assert.equal(links.signIn, '/identity/login?returnPath=%2Fshop');
  assert.equal(links.cart, '/identity/login?returnPath=%2Fcart');
  assert.equal(links.orders, '/identity/login?returnPath=%2Fordering%2Forders');
  assert.equal(links.staffDoor, '/identity/login?returnPath=%2Fworkspace');
});

test('signed-in buyers reach their cart and orders directly; only staff see the back-office door', () => {
  const member = storeMember('Rina', buyer);
  const links = storeLinks(member, false, '/shop');
  assert.equal(links.catalog, '/shop');
  assert.equal(links.cart, '/cart');
  assert.equal(links.orders, '/ordering/orders');
  assert.equal(links.staffDoor, null);
  assert.equal(storeLinks(storeMember('Petugas', ['catalog.manage']), false, '/shop').staffDoor, '/workspace');
});

test('the bounded first release keeps buyers on branch requests instead of priced orders', () => {
  const links = storeLinks(storeMember('Rina', buyer), true, '/shop');
  assert.equal(links.cart, '/ordering/requests');
  assert.equal(links.orders, null, 'the first release has no priced orders; branch requests are the cart (M2: no "Pesanan saya" label on /ordering/requests)');
});

test('the Katalog tab is current on the member catalog and on the guest catalog (G-025)', () => {
  for (const path of ['/shop', '/shop/00000065-0000-4000-8000-000000000101', '/katalog', '/katalog/00000065-0000-4000-8000-000000000101']) assert.equal(catalogTabCurrent(path), true, path);
  for (const path of ['/cart', '/shopping', '/katalogus', '/ordering/orders', '/']) assert.equal(catalogTabCurrent(path), false, path);
});

test('the footer follows the viewer: sign-in returns to the page, and buyers get no staff door (G-021)', () => {
  const guest = storeLinks({kind: 'guest'}, false, '/katalog');
  assert.equal(guest.signIn, '/identity/login?returnPath=%2Fkatalog');
  assert.equal(guest.catalog, '/katalog');
  assert.equal(storeLinks(storeMember('Rina', buyer), true, '/shop').orders, null);
  assert.equal(storeLinks(storeMember('Rina', buyer), false, '/shop').staffDoor, null);
});

test('store staff who never place orders get no "Pesanan saya" entry that could only ever be empty (F-010)', () => {
  const storeStaff = ['catalog.read', 'order.read', 'stock.read', 'shipment.prepare', 'shipment.dispatch', 'procurement.read'];
  assert.equal(storeLinks(storeMember('Petugas', storeStaff), false, '/shop').orders, null);
  assert.equal(storeLinks(storeMember('Sari', [...storeStaff, 'order.submit']), false, '/shop').orders, '/ordering/orders');
});

test('cart and orders are offered only to members who can order; staff without ordering rights see neither (G-033)', () => {
  for (const staff of [['shipment.read', 'delivery.attempt'], ['invite.manage', 'technical.configure'], ['invoice.read', 'invoice.issue'], ['payment.review.read', 'invoice.read']]) {
    const links = storeLinks(storeMember('Petugas', staff), false, '/shop');
    assert.equal(links.cart, null, staff.join());
    assert.equal(links.orders, null, staff.join());
  }
  const buyer = storeLinks(storeMember('Rina', ['catalog.read', 'order.read', 'order.submit']), false, '/shop');
  assert.equal(buyer.cart, '/cart'); assert.equal(buyer.orders, '/ordering/orders');
  // First release: the cart is the branch request, offered to members who may request.
  assert.equal(storeLinks(storeMember('Rina', ['catalog.read', 'order.request.submit']), true, '/shop').cart, '/ordering/requests');
  assert.equal(storeLinks(storeMember('Petugas', ['catalog.manage']), true, '/shop').cart, null);
  // Guests still meet the sign-in door for both.
  assert.match(storeLinks({kind: 'guest'}, false, '/shop').cart!, /^\/identity\/login/);
});
