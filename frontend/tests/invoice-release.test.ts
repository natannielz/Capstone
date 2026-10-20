import test from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {createInvoiceReleaseGateway, invoiceReleaseWib, parseInvoiceReleaseEligibility, parseInvoiceReleaseInput, parseInvoiceReleasePreview,
  parseInvoiceReleaseResult, parseInvoiceReleaseView, prepareInvoiceRelease} from '../src/modules/invoice-releases/invoice-release-client';
import {InvoiceReleaseJournal, type InvoiceReleaseReference} from '../src/modules/invoice-releases/invoice-release-journal';

const hash = 'a'.repeat(64), otherHash = 'b'.repeat(64);
const receipts = [{shipmentId: '10', receiptId: '20'}];
const input = {receipts, expectedReleaseRevision: '0', periodFrom: '2026-10-01', periodTo: '2026-10-08', validFrom: '2026-10-08T00:00:00.000Z', validUntil: '2026-10-09T00:00:00.000Z',
  templateApprovalReference: 'Reviewed template document', numberingApprovalReference: 'Reviewed numbering document', eligibilityReviewReference: 'Reviewed acceptance document',
  mappings: {receivable: 'invoice-receivable', revenue: 'invoice-revenue', tax: 'invoice-tax', charges: 'invoice-charges'}, allocations: [{receiptId: '20', skuId: '30', lineRevision: '0', tax: '1.00', charges: '2.00'}]};
const facts = {buyerName: 'Synthetic buyer', sellerName: 'Synthetic seller', grouping: 'receipt', termOrigin: 'issue_date', termDays: 30, dayRule: 'working_days', rounding: 'half_even',
  taxDocumentProcess: 'Reviewed tax document', numberingProcedure: 'Reviewed sequence', reissueProcedure: 'Reviewed reissue procedure', workingDays: [1, 2, 3, 4, 5], holidays: ['2026-10-10'],
  accountingPeriod: '2026-10', accounts: Object.entries(input.mappings).map(([role, mappingKey], index) => ({role, mappingKey, accountId: String(50 + index), accountCode: 'ACC-' + index})),
  lines: [{orderId: '5', shipmentId: '10', receiptId: '20', skuId: '30', lineRevision: '0', skuCode: 'TEST-SKU', productName: 'Synthetic package', packageLabel: 'Kotak', acceptedBaseUnits: '2', unitPrice: '10.00', net: '20.00', tax: '1.00', charges: '2.00'}],
  budgets: [{orderId: '5', approvedTax: '3.00', approvedCharges: '4.00', postedTax: '0.00', postedCharges: '0.00'}]};
const preview = {binding: hash, selectionKey: hash, revision: '0', input, facts};
const result = {releaseId: '60', recordId: '61', proposalId: '61', revision: '1', action: 'propose', selectionKey: hash};
const record = {result, actorId: '7', recordedAt: '2026-10-08T00:00:00.000Z', reason: 'Reviewed source documents', input, facts, recordHash: otherHash};
const reference: InvoiceReleaseReference = {actorId: '7', action: 'propose', key: 'release.original', revision: '0', proposalId: null, selectionKey: hash};
class StorageFixture {
  readonly values = new Map<string, string>();
  get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
test('exact accepted receipts, line revisions, account roles and commercial facts remain bound to the reviewed input', () => {
  const parsed = parseInvoiceReleasePreview(preview, parseInvoiceReleaseInput(input), hash, '0'); assert.deepEqual(parsed.input, input);
  assert.equal(parseInvoiceReleaseView(record).result.recordId, '61');
  assert.throws(() => parseInvoiceReleasePreview({...preview, selectionKey: otherHash}, parsed.input, hash, '0'));
  assert.throws(() => parseInvoiceReleasePreview({...preview, input: {...input, allocations: [{...input.allocations[0], lineRevision: '2'}]}}, parsed.input, hash, '0'));
  assert.throws(() => parseInvoiceReleasePreview({...preview, facts: {...facts, accounts: facts.accounts.map((item, index) => index === 0 ? {...item, mappingKey: 'different-approved-source'} : item)}}, parsed.input, hash, '0'));
  assert.throws(() => parseInvoiceReleasePreview({...preview, facts: {...facts, lines: [...facts.lines, facts.lines[0]]}}, parsed.input, hash, '0'));
  assert.throws(() => parseInvoiceReleaseView({...record, result: {...result, revision: '2'}}));
});
test('eligibility supports explicit unknown financial allocation and never invents defaults', () => {
  const initialFacts = {...facts, accountingPeriod: null, accounts: [], lines: facts.lines.map(line => ({...line, tax: null, charges: null}))};
  assert.equal(parseInvoiceReleaseEligibility({selectionKey: hash, revision: '0', releaseId: null, state: null, facts: initialFacts}, receipts).facts.lines[0].tax, null);
  assert.throws(() => parseInvoiceReleaseEligibility({selectionKey: hash, revision: '0', releaseId: null, state: null, facts: initialFacts}, [{shipmentId: '11', receiptId: '20'}]));
  assert.throws(() => parseInvoiceReleaseInput({...input, allocations: [{...input.allocations[0], tax: ''}]}), RequestNotSentError);
  assert.throws(() => parseInvoiceReleaseInput({...input, mappings: {...input.mappings, tax: input.mappings.revenue}}), RequestNotSentError);
  assert.throws(() => parseInvoiceReleaseInput({...input, allocations: [{...input.allocations[0], lineRevision: '1'}]}), RequestNotSentError);
  assert.equal(invoiceReleaseWib('2026-10-09T07:30'), '2026-10-09T00:30:00.000Z'); assert.throws(() => invoiceReleaseWib('2026-02-30T10:00'), RequestNotSentError);
});
test('original result requires exact operation, selection, proposal and incremented revision', () => {
  assert.equal(parseInvoiceReleaseResult(result, reference).recordId, '61');
  for (const wrong of [{...result, selectionKey: otherHash}, {...result, revision: '2'}, {...result, action: 'approve'}]) assert.throws(() => parseInvoiceReleaseResult(wrong, reference));
  const check: InvoiceReleaseReference = {...reference, action: 'approve', revision: '1', proposalId: '61'};
  const approved = {...result, action: 'approve', recordId: '62', revision: '2'};
  assert.equal(parseInvoiceReleaseResult(approved, check).proposalId, '61'); assert.throws(() => parseInvoiceReleaseResult({...approved, proposalId: '63'}, check));
});
test('durable actor and operation references survive restart and simultaneous tabs without financial payload', () => {
  const storage = new StorageFixture(), first = new InvoiceReleaseJournal(() => storage), second = new InvoiceReleaseJournal(() => storage);
  const another = {...reference, key: 'release.other'}; first.remember(reference); second.remember(another);
  assert.deepEqual(new InvoiceReleaseJournal(() => storage).list('7'), [reference, another].sort((a, b) => a.key.localeCompare(b.key)));
  assert.throws(() => second.remember({...reference, selectionKey: otherHash}));
  assert.throws(() => first.remember({...reference, input} as InvoiceReleaseReference));
  first.confirmed(reference); assert.deepEqual(second.list('7'), [another]); assert.deepEqual(second.list('8'), []);
  assert.deepEqual(Object.keys(another).sort(), ['action', 'actorId', 'key', 'proposalId', 'revision', 'selectionKey']);
});
test('denied, conflict, unknown and not-confirmed recoveries retain the original key and never resend a command', async () => {
  const storage = new StorageFixture(), journal = new InvoiceReleaseJournal(() => storage); journal.remember(reference); const seen: {path: string; body: unknown}[] = [];
  for (const status of [403, 409, 503, 200]) {
    const fetcher: typeof fetch = async (request, init) => {
      if (request instanceof Request) return Response.json({requestToken: 'actual-synthetic-csrf'});
      seen.push({path: String(request), body: JSON.parse(init!.body as string)});
      return Response.json(status === 200 ? {status: 'not_confirmed', result: null} : {code: 'unavailable'}, {status});
    };
    const gateway = createInvoiceReleaseGateway(fetcher, 'https://portal.unit-toko.co.id/api/v1/identity/csrf');
    if (status === 200) assert.equal(await gateway.recover('7', reference), null); else await assert.rejects(gateway.recover('7', reference), AccessFailure);
    assert.deepEqual(journal.list('7'), [reference]);
  }
  assert.equal(seen.length, 4); for (const call of seen) {assert.equal(call.path, '/api/v1/billing/invoice-releases/operations/query'); assert.deepEqual(call.body, {purpose: 'operation-recovery', action: 'propose', key: 'release.original'});}
});
test('current account checked before dispatch and after the complete response body without clearing an unknown write', async () => {
  const checked = parseInvoiceReleasePreview(preview, parseInvoiceReleaseInput(input), hash, '0'), command = prepareInvoiceRelease('7', checked, 'propose', null, 'Checked exact documents');
  let current = true, writes = 0;
  const fetcher: typeof fetch = async request => {
    if (request instanceof Request) return Response.json({requestToken: 'synthetic-csrf'}); writes++;
    const response = Response.json(result); const json = response.json.bind(response); response.json = async () => {const value = await json(); current = false; return value;}; return response;
  };
  const gateway = createInvoiceReleaseGateway(fetcher, 'https://portal.unit-toko.co.id/api/v1/identity/csrf');
  await assert.rejects(gateway.change(command, reference.key, () => false), RequestNotSentError); assert.equal(writes, 0);
  await assert.rejects(gateway.change(command, reference.key, () => current), error => error instanceof AccessFailure && error.code === 'commit_outcome_unknown'); assert.equal(writes, 1);
});
