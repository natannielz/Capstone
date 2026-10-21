import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AccessFailure } from '../src/modules/administration/workflow';
import { createCartGateway, CartOutcomeUnknown } from '../src/modules/ordering/cart-client';
import { addToCart } from '../src/modules/ordering/cart-model';
import type { DraftCommand, OrderDraft } from '../src/modules/ordering/order-draft-client';

const context = { organizationId: '1', branchId: '2', divisionId: null };
const line = (skuId: string, packages: number) => ({ skuId, definitionId: '7' + skuId, definitionVersion: '1', priceId: '9' + skuId, priceVersion: '1', packages });
const saved = (id: string, revision: string, selections = [line('11', 1)], updatedAt = '2026-10-21T08:00:00Z'): OrderDraft =>
  ({ id, revision, createdAt: '2026-10-20T08:00:00Z', updatedAt, input: { context, selections, delivery: null, purchaseReference: 'PO-7' } });
const rule = { minimum: 1, increment: 1 };

function fake(lists: OrderDraft[][], answers: Array<(command: DraftCommand, key: string) => unknown>) {
  const sent: Array<{ command: DraftCommand; key: string }> = [], discarded: Array<{ id: string; revision: string; key: string }> = [];
  let keys = 0;
  const gateway = createCartGateway({
    drafts: {
      list: async () => ({ items: lists.length > 1 ? lists.shift()! : lists[0], hasMore: false }),
      submit: async (command: DraftCommand, key: string) => { sent.push({ command, key }); const answer = answers.shift()!(command, key); if (answer instanceof Error) throw answer; return answer; },
      discard: async (_actor: string, id: string, revision: string, _reason: string, key: string) => { discarded.push({ id, revision, key }); return { draftId: id, revision, discardedAt: '', restorableUntil: '' }; },
    } as never,
    newKey: () => 'cart-key-' + ++keys,
  });
  return { gateway, sent, discarded };
}
const ok = (draftId: string, revision: string) => () => ({ draftId, versionId: '5', revision, recordedAt: '2026-10-21T09:00:00Z' });

test('the first package creates the branch cart, and later ones update the same draft at its revision', async () => {
  const empty = fake([[]], [ok('41', '1')]);
  assert.equal((await empty.gateway.apply('7', context, current => addToCart(current, line('11', 2), rule), 'add')).draftId, '41');
  assert.equal(empty.sent[0].command.draftId, null); assert.equal(empty.sent[0].command.expectedRevision, '0');

  const existing = fake([[saved('41', '3')]], [ok('41', '4')]);
  await existing.gateway.apply('7', context, current => addToCart(current, line('12', 1), rule), 'add');
  assert.equal(existing.sent[0].command.draftId, '41'); assert.equal(existing.sent[0].command.expectedRevision, '3');
  assert.deepEqual(existing.sent[0].command.input.selections.map(item => item.skuId), ['11', '12']);
  assert.equal(existing.sent[0].command.input.purchaseReference, 'PO-7');
});

test('a cart changed in another tab is read again and the same change is applied on top, once', async () => {
  const raced = fake([[saved('41', '3')], [saved('41', '4', [line('11', 5)])]],
    [() => new AccessFailure(409, 'order_draft_revision_changed'), ok('41', '5')]);
  await raced.gateway.apply('7', context, current => addToCart(current, line('11', 1), rule), 'add');
  assert.equal(raced.sent.length, 2); assert.equal(raced.sent[1].command.expectedRevision, '4');
  assert.equal(raced.sent[1].command.input.selections[0].packages, 6);
  assert.notEqual(raced.sent[0].key, raced.sent[1].key);
});

test('a lost answer is sent again with the same key, so the server replays instead of adding twice', async () => {
  const lost = fake([[saved('41', '3')]], [() => new TypeError('fetch failed'), ok('41', '4')]);
  await lost.gateway.apply('7', context, current => addToCart(current, line('11', 1), rule), 'add');
  assert.equal(lost.sent.length, 2); assert.equal(lost.sent[0].key, lost.sent[1].key);
  assert.deepEqual(lost.sent[0].command, lost.sent[1].command);

  const silent = fake([[saved('41', '3')]], [() => new TypeError('fetch failed'), () => new AccessFailure(503, 'commit_outcome_unknown')]);
  await assert.rejects(() => silent.gateway.apply('7', context, current => addToCart(current, line('11', 1), rule), 'add'),
    (error: unknown) => error instanceof CartOutcomeUnknown && error.key === 'cart-key-1');
});

test('a changed price is reported, not retried, and an emptied cart is discarded instead of saved empty', async () => {
  const stale = fake([[saved('41', '3')]], [() => new AccessFailure(409, 'order_selection_changed')]);
  await assert.rejects(() => stale.gateway.apply('7', context, current => current, 'add'), (error: unknown) => (error as AccessFailure).code === 'order_selection_changed');
  assert.equal(stale.sent.length, 1);

  const emptied = fake([[saved('41', '3')]], []);
  assert.equal((await emptied.gateway.apply('7', context, () => [], 'remove')).draftId, null);
  assert.deepEqual(emptied.discarded, [{ id: '41', revision: '3', key: 'cart-key-1' }]);
});

test('delivery details are saved into the same draft at its current revision, keeping its packages', async () => {
  const sent: DraftCommand[] = [];
  let reads = 0;
  const gateway = createCartGateway({ newKey: () => 'details-key', drafts: {
    read: async () => (++reads === 1 ? saved('41', '3') : saved('41', '4')),
    submit: async (command: DraftCommand) => { sent.push(command); if (sent.length === 1) throw new AccessFailure(409, 'order_draft_revision_changed'); return ok('41', '5')(); },
  } as never });
  const delivery = { recipientName: 'Rina', phone: '0812-3456-7890', address: 'Jl. Sudirman 1', city: 'Jakarta', postalCode: null, instructions: null };
  const result = await gateway.saveDetails('7', '41', delivery, 'PO-8');
  assert.equal(result.revision, '5');
  assert.equal(sent[1].expectedRevision, '4'); assert.deepEqual(sent[1].input.delivery, delivery); assert.equal(sent[1].input.purchaseReference, 'PO-8');
  assert.deepEqual(sent[1].input.selections, saved('41', '4').input.selections);
});

test('with a tracked submitter the cart leaves recovery to the journal: one send, no silent replay', async () => {
  const sent: DraftCommand[] = [];
  const lost = new TypeError('fetch failed');
  const gateway = createCartGateway({ drafts: { list: async () => ({ items: [saved('41', '3')], hasMore: false }) } as never,
    submit: async (command: DraftCommand) => { sent.push(command); throw lost; } });
  await assert.rejects(() => gateway.apply('7', context, current => addToCart(current, line('11', 1), rule), 'add'), (error: unknown) => error === lost);
  assert.equal(sent.length, 1); assert.equal(sent[0].expectedRevision, '3');
});
