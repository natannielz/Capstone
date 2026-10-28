import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
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


test('while the viewer is still loading, the tab bar links neither cart nor orders (round 1)', () => {
  const links = storeLinks({kind: 'loading'}, false, '/shop');
  assert.equal(links.cart, null); assert.equal(links.orders, null);
});

// F-025: staff without branch-request grants are not offered "Permintaan saya" in the account menu or the footer.
test('"Permintaan saya" is offered only to accounts that can read or submit branch requests (F-025)', () => {
  assert.equal(storeMember('Rina', ['catalog.read', 'order.request.read']).requests, true);
  assert.equal(storeMember('Rina', ['catalog.read', 'order.request.submit']).requests, true);
  assert.equal(storeMember('Petugas', ['catalog.read', 'catalog.manage']).requests, false);
  assert.equal(storeMember('Penyetuju', ['catalog.read', 'order.spend.approve']).requests, false);
  const frame = readFileSync('src/components/store/store-frame.tsx', 'utf8');
  const offered = frame.match(/>Permintaan saya</g) ?? [];
  const guarded = frame.match(/\{requests && <Link href="\/ordering\/requests"[^\n]*?>Permintaan saya</g) ?? [];
  assert.ok(offered.length > 0);
  assert.equal(guarded.length, offered.length, 'every "Permintaan saya" link is guarded by the order.request.* flag');
});

// F-029: a buyer allowed to cancel (order.cancel) reaches "Pembatalan pesanan" on /ordering/reviews from the store, not only by typing the address.
test('a buyer allowed to cancel finds Peninjauan pesanan in the account menu and on the order (F-029)', () => {
  const pemohon = ['catalog.read', 'order.submit', 'order.read', 'order.cancel', 'invoice.read', 'branch.pic', 'order.request.read', 'order.request.submit', 'substitution.accept', 'organization.address.read'];
  assert.ok(storeMember('Pemohon', pemohon).tasks.some(task => task.href === '/ordering/reviews'));
  assert.ok(!storeMember('Pemohon', pemohon.filter(action => action !== 'order.cancel')).tasks.some(task => task.href === '/ordering/reviews'));
  const history = readFileSync('src/modules/ordering/OrderHistoryWorkspace.tsx', 'utf8');
  assert.match(history, /account\.actions\.includes\('order\.cancel'\) && !selected\.progress\.cancelled && <p><Link href="\/ordering\/reviews">/);
});
