import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { taskFrame } from '../src/components/store/task-frame-rule';

const pemohon = ['catalog.read', 'order.read', 'order.submit', 'order.substitute', 'order.cancel', 'shipment.read', 'receipt.accept', 'invoice.read', 'case.read',
  'case.request', 'payment.evidence', 'order.request.read', 'order.request.submit', 'substitution.accept', 'organization.address.read'];
const stafPembeli = ['catalog.read', 'order.read', 'stock.read', 'shipment.read', 'shipment.prepare', 'shipment.dispatch', 'procurement.read', 'order.submit', 'invoice.read'];
const buyerTaskRoutes = ['/billing/invoices', '/billing/payments', '/fulfillment/receive', '/fulfillment/cases', '/fulfillment/shipments', '/ordering/reviews', '/ordering/substitutions'];

test("a non-staff buyer's own tasks open inside the storefront frame; staff keep the back office (G-037, R2)", () => {
  for (const route of buyerTaskRoutes) {
    assert.equal(taskFrame(route, pemohon), 'store', route);
    assert.equal(taskFrame(route, stafPembeli), 'workspace', route + ' for staff');
    assert.equal(taskFrame(route, null), 'pending', route + ' while the viewer is unknown');
  }
  // Staff-only pages never wait and never switch.
  for (const route of ['/billing/payment-reviews', '/fulfillment/dispatches', '/procurement/requests', '/ordering/requests/incoming'])
    assert.equal(taskFrame(route, null), 'workspace', route);
});

test('the area layouts route through the one task frame instead of forking the pages (G-037)', () => {
  for (const layout of ['billing/layout.tsx', 'fulfillment/layout.tsx', 'administration/layout.tsx'])
    assert.match(readFileSync(new URL('../src/app/' + layout, import.meta.url), 'utf8'), /<TaskFrame>/, layout);
  assert.match(readFileSync(new URL('../src/components/store/ordering-frame.tsx', import.meta.url), 'utf8'), /<TaskFrame>/);
});


test('when the viewer cannot be read the page keeps the workspace frame, which shows its own lost-session or unavailable state (round 1)', () => {
  assert.equal(taskFrame('/billing/invoices', 'unknown'), 'workspace');
  assert.equal(taskFrame('/billing/invoices', []), 'store', 'no session: the shared ended-session state, in the store frame');
});
