// Basis-path (white box) cases for the report's section 4.3.1: one test per independent path of each flow graph.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createUnitTokoClient } from '../src/api/client';
import { orderQuantity } from '../src/modules/ordering/order-quote-client';
import { signInRedirect } from '../src/modules/identity/route-canonical';
import { createBranchRequestGateway, parseBranchRequestDecision } from '../src/modules/ordering/branch-request-client';

const rejects = (code: string) => (error: unknown) => (error as { code?: string }).code === code;

test('orderQuantity J1: text that is not a positive whole number is refused', () => {
  for (const value of ['0', '1.5', '-5', '', '01']) assert.throws(() => orderQuantity(value, 10, 5), rejects('order_package_quantity_invalid'), value);
});
test('orderQuantity J2: more than 1,000,000 packages is refused', () => {
  assert.throws(() => orderQuantity('1000001', 1, 1), rejects('order_package_quantity_invalid'));
});
test('orderQuantity J3: below the minimum order is refused', () => {
  assert.throws(() => orderQuantity('5', 10, 5), rejects('order_package_quantity_invalid'));
});
test('orderQuantity J4: not a multiple of the package step is refused', () => {
  assert.throws(() => orderQuantity('12', 10, 5), rejects('order_package_quantity_invalid'));
});
test('orderQuantity J5: a valid quantity is returned as a number', () => {
  assert.equal(orderQuantity('15', 10, 5), 15);
});

test('signInRedirect J1: a visitor with a session is not redirected', () => {
  assert.equal(signInRedirect('/billing/invoices', '', true), null);
});
test('signInRedirect J2: a guest page stays open without a session', () => {
  assert.equal(signInRedirect('/katalog', '', false), null);
});
test('signInRedirect J3: a path that cannot be decoded goes to sign-in without a return path', () => {
  assert.equal(signInRedirect('/billing/%ZZ', '', false), '/identity/login');
});
test('signInRedirect J4: an allowed page and query become the return path', () => {
  assert.equal(signInRedirect('/ordering/checkout', '?draft=7', false), '/identity/login?returnPath=' + encodeURIComponent('/ordering/checkout?draft=7'));
});
test('signInRedirect J5: a refused query is dropped and only the page is kept', () => {
  assert.equal(signInRedirect('/billing/invoices', '?next=https://outside.test', false), '/identity/login?returnPath=' + encodeURIComponent('/billing/invoices'));
});
test('signInRedirect J6: a signed-in page that is not an allowed return path goes to sign-in without one', () => {
  assert.equal(signInRedirect('/billing/' + 'a'.repeat(250), '', false), '/identity/login');
});

// Wire-only fixtures, same shapes as tests/branch-requests.test.ts.
const context = { organizationId: '10', branchId: '11', divisionId: null };
const owner = { kind: 'fulfillmentWarehouse' as const, organizationId: '100', branchId: null, divisionId: null, shopId: '101', warehouseId: '102', channelId: '103' };
const input = { context, lines: [{ skuId: '51', definitionId: '52', definitionVersion: '1', packages: 2 }], note: 'Kebutuhan rapat cabang' };
const labels = { buyingOrganization: 'Organisasi SINTETIS', branch: 'Cabang SINTETIS', division: null, fulfillmentOrganization: 'Koperasi SINTETIS', shop: 'Toko SINTETIS', warehouse: 'Gudang SINTETIS', channel: 'Omi SINTETIS' };
const lines = [{ ...input.lines[0], skuCode: 'SYN-BOX', familyName: 'ATK SINTETIS', packageLabel: 'Karton isi 12', packageKind: 'sealed', baseUnitCode: 'buah', baseUnitsPerPackage: 12, owner }];
const observedAt = '2026-10-04T02:00:00Z';
const view = { id: '88', number: 'PT-00000088', version: '1', state: 'pending', input, lines, owner, labels, createdBy: '1001', recordedAt: observedAt, decision: null };
const submitted = { requestId: '88', decisionId: null, number: view.number, version: '1', state: 'pending', recordedAt: observedAt };
const accepted = { ...submitted, version: '2', state: 'accepted', decisionId: '89' };
const rejected = { ...submitted, version: '2', state: 'rejected', decisionId: '90' };
const preview = (action: 'accept' | 'reject', canDecide = true) =>
  parseBranchRequestDecision({ request: view, decision: action, canDecide, binding: 'a'.repeat(64), observedAt }, '88', action);
function gateway(reply: unknown) {
  const calls = { sent: 0 };
  const client = createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    if (new URL((request as Request).url).pathname === '/api/v1/identity/csrf') return Response.json({ requestToken: 'csrf' });
    calls.sent++; return Response.json(reply);
  } });
  return { gateway: createBranchRequestGateway(client), calls };
}

test('decide J1: a request that cannot be decided, or a stale view, is never sent', async () => {
  for (const [value, current] of [[preview('accept', false), true], [preview('accept'), false]] as const) {
    const { gateway: g, calls } = gateway(accepted);
    await assert.rejects(() => g.decide('1001', value, 'Kemasan tersedia untuk cabang', 'k1', () => current), rejects('branch_request_view_stale'));
    assert.equal(calls.sent, 0);
  }
});
test('decide J2: a rejection without a clean reason is never sent', async () => {
  for (const reason of ['', ' alasan ', 'a'.repeat(1001), 'baris\nbaru']) {
    const { gateway: g, calls } = gateway(rejected);
    await assert.rejects(() => g.decide('1001', preview('reject'), reason, 'k2', () => true));
    assert.equal(calls.sent, 0, JSON.stringify(reason));
  }
});
test('decide J3: a page that changed after sending reports an unknown outcome and does not resend', async () => {
  const { gateway: g, calls } = gateway(accepted); let checks = 0;
  await assert.rejects(() => g.decide('1001', preview('accept'), 'Kemasan tersedia untuk cabang', 'k3', () => ++checks === 1), rejects('commit_outcome_unknown'));
  assert.equal(calls.sent, 1);
});
test('decide J4: a server answer that does not match the decision is refused', async () => {
  const { gateway: g } = gateway(rejected);
  await assert.rejects(() => g.decide('1001', preview('accept'), 'Kemasan tersedia untuk cabang', 'k4', () => true), rejects('branch_request_response_invalid'));
});
test('decide J5: an accepted or rejected decision with its reason returns the recorded state', async () => {
  assert.equal((await gateway(accepted).gateway.decide('1001', preview('accept'), 'Kemasan tersedia untuk cabang', 'k5', () => true)).state, 'accepted');
  assert.equal((await gateway(rejected).gateway.decide('1001', preview('reject'), 'Kemasan ini sedang tidak dijual', 'k6', () => true)).state, 'rejected');
});
