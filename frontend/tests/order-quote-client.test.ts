import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderQuoteGateway, orderQuantity, parseOrderAddresses, parseOrderQuote } from '../src/modules/ordering/order-quote-client';
import { actorId, family, sku, definition } from './catalog-fixtures';
const context = { organizationId: '10', branchId: '11', divisionId: null };
const selected = { skuId: sku.id, definitionId: definition.id, definitionVersion: definition.version, priceId: '9007199254740999', priceVersion: '2', packages: 2 };
const price = { id: selected.priceId, skuId: sku.id, version: '2', currency: 'IDR', unitPrice: '12000.25', effectiveAt: definition.effectiveAt };
const quote = { context, calculatedAt: definition.effectiveAt, currency: 'IDR', catalogSubtotal: '24000.50', lines: [{ familyId: family.id, familyName: family.name,
  publicId: sku.publicId, skuCode: sku.code, definition, price, packages: 2, baseUnits: '24', catalogSubtotal: '24000.50' }] };
test('branch quote client checks exact selections, package totals and decimal amounts without precision loss', () => {
  assert.equal(parseOrderQuote(quote, context, [selected]).lines[0].price.id, '9007199254740999');
  for (const changed of [ { ...quote, context: { ...context, branchId: '12' } }, { ...quote, catalogSubtotal: '1.00' },
    { ...quote, lines: [{ ...quote.lines[0], baseUnits: '2' }] }, { ...quote, lines: [{ ...quote.lines[0], price: { ...price, id: '7' } }] },
    { ...quote, lines: [{ ...quote.lines[0], definition: { ...definition, version: '99' } }] }, { ...quote, lines: [{ ...quote.lines[0], packages: 3 }] } ])
    assert.throws(() => parseOrderQuote(changed, context, [selected]));
  const zero = { ...quote, catalogSubtotal: '0.00', lines: [{ ...quote.lines[0], price: { ...price, unitPrice: '0.00' }, catalogSubtotal: '0.00' }] };
  assert.equal(parseOrderQuote(zero, context, [selected]).catalogSubtotal, '0.00');
  assert.equal(orderQuantity('6', 4, 2), 6);
  for (const input of ['', '0', '-1', '1.5', '1e3', '1000001', '3', '04', '4\n']) assert.throws(() => orderQuantity(input, 4, 2));
});
test('quote transport supplies reviewed actor and branch, no browser prices or mutation keys, and does not retry', async () => {
  let calls = 0;
  const gateway = createOrderQuoteGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    calls++; assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    assert.equal(input.headers.get('Idempotency-Key'), null); assert.equal(input.credentials, 'same-origin');
    assert.deepEqual(await input.json(), { purpose: 'order-preparation', context, selections: [selected] });
    return calls === 1 ? Response.json(quote) : Response.json({ code: 'order_selection_changed' }, { status: 409 });
  } }));
  assert.equal((await gateway.preview(actorId, context, [selected])).catalogSubtotal, '24000.50');
  await assert.rejects(() => gateway.preview(actorId, context, [selected]), { code: 'order_selection_changed' }); assert.equal(calls, 2);
});
test('ordering context choices never infer membership from a name or accept duplicate branch entries', async () => {
  const item = { context, organizationName: null, branchName: 'Cabang fixture', divisionName: null };
  const gateway = (items: unknown[]) => createOrderQuoteGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); return input.url.endsWith('/identity/csrf') ? Response.json({ requestToken: 'csrf' }) : Response.json({ items });
  } }));
  assert.deepEqual(await gateway([item]).contexts(actorId), [item]);
  await assert.rejects(() => gateway([item, item]).contexts(actorId));
  await assert.rejects(() => gateway([{ ...item, context: { ...context, organizationId: 10 } }]).contexts(actorId));
});

const postal = { line1: 'Jalan Cabang 11', line2: null, city: 'Jakarta Selatan', region: 'DKI Jakarta', postalCode: '12810', countryCode: 'ID' };
const approvedAddress = { target: { kind: 'buyingOrganization', id: '10' }, purpose: 'legal_billing', addressId: '9007199254740999', versionId: '9007199254741000', version: '2',
  sourceScopeVersion: '3', profileId: '10', profileVersion: '4', code: 'BNI', name: 'Organisasi pembeli', sourceHash: 'a'.repeat(64), snapshotHash: 'b'.repeat(64),
  effectiveAt: '2026-09-29T00:00:00Z', address: postal };
const approvedAddresses = { context, observedAt: '2026-10-02T08:00:00+00:00', legalBilling: approvedAddress,
  delivery: { ...approvedAddress, addressId: '9007199254741002', versionId: '9007199254741003', profileId: '11', target: { kind: 'branch', id: '11' }, purpose: 'delivery', code: 'CAB-11', name: 'Cabang sebelas' }, binding: 'c'.repeat(64) };
test('approved postal preparation projects complete address provenance for only the exact buyer and delivery scope', () => {
  const checked = parseOrderAddresses({ ...approvedAddresses, privateApproval: 'omit', legalBilling: { ...approvedAddress, makerEmail: 'omit' } }, context);
  assert.deepEqual(checked, approvedAddresses); assert.equal(checked.legalBilling.addressId, '9007199254740999');
  assert.equal(checked.delivery.address.region, 'DKI Jakarta'); assert.equal(checked.delivery.address.countryCode, 'ID');
  assert.ok(!JSON.stringify(checked).includes('omit'));
  const division = { ...context, divisionId: '12' }, divisionAddresses = { ...approvedAddresses, context: division,
    delivery: { ...approvedAddresses.delivery, profileId: '12', target: { kind: 'division', id: '12' } } };
  assert.deepEqual(parseOrderAddresses(divisionAddresses, division), divisionAddresses);
  assert.throws(() => parseOrderAddresses({ ...divisionAddresses, delivery: approvedAddresses.delivery }, division));
});
test('approved postal preparation rejects malformed, future, missing and cross-scope authority without substituting an address', () => {
  for (const changed of [
    { ...approvedAddresses, context: { ...context, organizationId: '20' } },
    { ...approvedAddresses, context: { ...context, branchId: 11 } },
    { ...approvedAddresses, legalBilling: null }, { ...approvedAddresses, delivery: null },
    { ...approvedAddresses, observedAt: 'tomorrow' }, { ...approvedAddresses, observedAt: '2026-10-02' },
    { ...approvedAddresses, observedAt: '2026-02-30T00:00:00Z' }, { ...approvedAddresses, binding: 'C'.repeat(64) },
    ...[
      { target: { kind: 'branch', id: '21' } }, { purpose: 'legal_billing' }, { version: '0' }, { addressId: 123 },
      { sourceScopeVersion: '0' }, { profileId: '21' }, { profileVersion: 1 }, { sourceHash: 'a'.repeat(63) }, { snapshotHash: 'b'.repeat(64) + '\n' },
      { effectiveAt: '2026-10-03T00:00:00Z' }, { effectiveAt: '2026-02-30T00:00:00Z' },
      { address: { ...postal, region: '' } }, { address: { ...postal, countryCode: 'id' } },
      { address: { ...postal, line1: '\u200bJalan' } }, { address: { ...postal, recipientName: 'unverified' } },
    ].map(change => ({ ...approvedAddresses, delivery: { ...approvedAddresses.delivery, ...change } })),
  ]) assert.throws(() => parseOrderAddresses(changed, context));
});
test('postal preparation reads with the exact actor, context and CSRF, propagates cancellation and never sends a draft or retries', async () => {
  let calls = 0; const controller = new AbortController();
  const gateway = createOrderQuoteGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf-memory' });
    calls++; assert.ok(input.url.endsWith('/api/v1/ordering/addresses/query'));
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    assert.equal(input.headers.get('Idempotency-Key'), null); assert.equal(input.credentials, 'same-origin');
    assert.deepEqual(await input.json(), { purpose: 'order-preparation', context });
    if (calls === 1) return Response.json(approvedAddresses);
    if (calls === 2) return Response.json({ code: 'organization_address_changed' }, { status: 409 });
    controller.abort(); assert.equal(input.signal.aborted, true); throw new DOMException('Cancelled', 'AbortError');
  } }));
  assert.deepEqual(await gateway.addresses(actorId, context), approvedAddresses);
  await assert.rejects(() => gateway.addresses(actorId, context), { code: 'organization_address_changed' });
  await assert.rejects(() => gateway.addresses(actorId, context, controller.signal), { name: 'AbortError' }); assert.equal(calls, 3);
});
