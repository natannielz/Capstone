import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createInvoiceGateway, parseInvoice, prepareInvoice } from '../src/modules/billing/invoice-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';

const actor = '9007199254740993', receipt = { shipmentId: '9007199254740994', receiptId: '9007199254740995' };
const detail = { review: 'a'.repeat(64), buyerName: 'Cabang Tebet', sellerName: 'Omi', issueDate: '2026-09-30', dueDate: '2026-10-30', grouping: 'buying_org_period_channel' as const,
  periodFrom: '2026-09-01', periodTo: '2026-09-30', termOrigin: 'issue_date' as const, termDays: 30, dayRule: 'calendar_days' as const, taxDocumentProcess: 'Proses pajak fixture',
  net: '100.00', tax: '10.00', charges: '2.00', total: '112.00', lines: [{ orderId: '91', ...receipt, skuId: '92', skuCode: 'ATK01', productName: 'Kertas', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '6', net: '100.00', tax: '10.00', charges: '2.00' }] };
const invoice = { id: '9007199254740996', numberEntity: '100', numberChannel: '103', numberYear: 2026, number: '1', issuedAt: '2026-09-30T03:00:00Z', detail, documentHash: 'b'.repeat(64), documentLength: 1000 };
test('invoice freezes reviewed receipts and exact totals; malformed money and duplicate accepted lines are refused', () => {
  assert.deepEqual(parseInvoice(invoice), invoice); const command = prepareInvoice(actor, [receipt], detail, 'Penerimaan telah diperiksa');
  assert.ok(Object.isFrozen(command.body.receipts)); assert.ok(Object.isFrozen(command.detail.lines[0]));
  for (const value of [{ ...invoice, id: 9007199254740996 }, { ...invoice, detail: { ...detail, total: '112.01' } }, { ...invoice, detail: { ...detail, lines: [detail.lines[0], detail.lines[0]] } }, { ...invoice, documentHash: 'invalid' }, { ...invoice, scope: 'secret' }]) assert.throws(() => parseInvoice(value));
  assert.throws(() => prepareInvoice(actor, [{ ...receipt, receiptId: '999' }], detail, 'Periksa'));
});
test('invoice sends one frozen actor/CSRF/version request and retains uncertain mismatched result', async () => {
  const requests: Request[] = []; let wrong = false;
  const gateway = createInvoiceGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' }); requests.push(request);
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'csrf'); assert.equal(request.headers.get('If-Match'), '"0"');
    assert.equal(request.headers.get('Idempotency-Key'), 'invoice-key'); return Response.json({ ...invoice, detail: { ...detail, review: wrong ? 'c'.repeat(64) : detail.review } });
  } }));
  const command = prepareInvoice(actor, [receipt], detail, 'Penerimaan telah diperiksa');
  await assert.rejects(() => gateway.issue(command, 'invoice-key', () => false)); assert.equal(requests.length, 0);
  assert.deepEqual(await gateway.issue(command, 'invoice-key', () => true), invoice); assert.equal(requests.length, 1);
  assert.deepEqual(await requests[0].clone().json(), command.body);
  wrong = true; await assert.rejects(() => gateway.issue(command, 'invoice-key', () => true)); assert.equal(requests.length, 2);
});
test('invoice recovery uses its owner without close, resend, fallback or deletion of an unknown outcome', async () => {
  const values = new Map<string, string>(), storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const journal = new OperationJournal(() => storage), entry = journal.remember(actor, 'billing.invoice.issue', 'invoice-key');
  const legacy = { lookup: async () => { throw new Error('wrong owner'); }, close: async () => { throw new Error('must not close'); } };
  const absent = { 'billing.invoice': { lookup: async () => null } };
  assert.equal(operationRecoveryCapabilities(entry.operation, absent).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close'));
  await reconcileOperationRecovery(entry, journal, legacy, absent); assert.equal(journal.list(actor).length, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.invoice': { lookup: async () => ({ state: 'committed' as const, result: { ...invoice, number: '0' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  await reconcileOperationRecovery(entry, journal, legacy, { 'billing.invoice': { lookup: async () => ({ state: 'committed' as const, result: invoice }) } }); assert.equal(journal.list(actor).length, 0);
});
