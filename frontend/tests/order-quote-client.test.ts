import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createOrderQuoteGateway, orderQuantity, parseOrderQuote } from '../src/modules/ordering/order-quote-client';
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
