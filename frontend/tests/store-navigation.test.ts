import {test} from 'node:test';
import assert from 'node:assert/strict';
import {storeMember, storeLinks, storeSignInHref} from '../src/components/store/store-navigation';

const buyer = ['catalog.read', 'order.request.read', 'order.request.submit', 'branch.pic', 'order.submit', 'order.read', 'invoice.read'];

test('buyers, approvers and staff are told apart by their permissions, never by their name', () => {
  assert.deepEqual(storeMember('Rina', buyer), {kind: 'member', name: 'Rina', staff: false, approver: false});
  assert.deepEqual(storeMember('Andi', [...buyer, 'order.spend.approve']), {kind: 'member', name: 'Andi', staff: false, approver: true});
  assert.equal(storeMember('Petugas', ['catalog.read', 'catalog.manage']).staff, true);
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
  assert.equal(links.orders, '/ordering/requests');
});
