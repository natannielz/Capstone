import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { DiscardReview, DiscardedDraftList, discardedNotice } from '../src/modules/ordering/DraftDiscard';

const input = { context: { organizationId: '10', branchId: '11', divisionId: null }, selections: [
  { skuId: '100', definitionId: '200', definitionVersion: '1', priceId: '300', priceVersion: '1', packages: 2 } ], delivery: null, purchaseReference: 'PO 7' };

test('the discard review states what happens and when it can no longer be restored', () => {
  const html = renderToStaticMarkup(createElement(DiscardReview, { label: 'PO 7', now: Date.parse('2026-10-20T03:00:00Z'), busy: false, onConfirm() {}, onCancel() {} }));
  assert.match(html, /Buang draf ini\?/);
  assert.match(html, /Dapat dipulihkan sampai/);
  assert.match(html, /19 November 2026/);
  assert.match(html, /dihapus permanen 30 hari setelah dibuang/);
  assert.match(html, /data-tone="danger"[^>]*>Buang draf</);
});

test('discarded drafts list their restore deadline and a named restore action', () => {
  const html = renderToStaticMarkup(createElement(DiscardedDraftList, { items: [{ id: '9', revision: '2', discardedAt: '2026-10-20T03:00:00Z',
    restorableUntil: '2026-11-19T03:00:00Z', input }], locked: false, onRestore() {} }));
  assert.match(html, /Draf yang dibuang/);
  assert.match(html, /PO 7/);
  assert.match(html, /Dapat dipulihkan sampai 19 November 2026/);
  assert.match(html, /aria-label="Pulihkan draf PO 7"/);
  assert.match(renderToStaticMarkup(createElement(DiscardedDraftList, { items: [], locked: false, onRestore() {} })), /Tidak ada draf yang dibuang/);
});

test('the notice after discarding names the restore deadline', () => {
  assert.equal(discardedNotice('2026-11-19T03:00:00Z'), 'Draf dibuang. Anda dapat memulihkannya sampai 19 November 2026.');
});
