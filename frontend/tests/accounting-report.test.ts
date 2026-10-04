import test from 'node:test';
import assert from 'node:assert/strict';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';
import {CatalogReadGate} from '../src/modules/catalog/catalog-editor-state';
import {accountingReportDate, accountingReportLimits, createAccountingReportGateway, createAccountingReportTransport,
  formatAccountingReportMoney, parseAccountingReportBooks, parseAccountingReportSnapshot,
  type AccountingReportBook, type AccountingReportSnapshot, type AccountingReportTransport} from '../src/modules/reporting/accounting-report-client';

const actor = '9007199254740993', at = '2026-10-04T12:00:00.000Z', endDate = '2026-10-04';
const book: AccountingReportBook = {id: '9007199254740995', key: {organizationId: '11', shopId: '101', channelId: '131', currency: 'IDR'},
  organization: {id: '11', code: 'ORG', name: 'Organisasi uji'}, shop: {id: '101', code: 'SHOP', name: 'Toko uji'}, channel: {id: '131', code: 'WEB', name: 'Kanal uji'}};
function snapshot(count = 21): AccountingReportSnapshot {
  return {readId: '9007199254741001', snapshotHash: 'a'.repeat(64), book: structuredClone(book), endDate, capturedAt: at, expiresAt: '2026-10-04T12:15:00.000Z',
    debit: `${count}.00`, credit: `${count}.00`, timeZone: 'Asia/Jakarta', cumulative: true, isApprovedClose: false,
    accounts: [{accountId: '1000', accountCodes: ['Cash', 'HistoricalCash'], debit: `${count}.00`, credit: '0.00', balance: `${count}.00`},
      {accountId: '1001', accountCodes: ['Revenue'], debit: '0.00', credit: `${count}.00`, balance: count === 0 ? '0.00' : `-${count}.00`},
      {accountId: '1002', accountCodes: ['UnusedConfiguredAccount'], debit: '0.00', credit: '0.00', balance: '0.00'}],
    journals: Array.from({length: count}, (_, index) => ({id: (BigInt('9007199254741100') + BigInt(index)).toString(), businessDate: endDate,
      source: {namespace: 'test.fixture', documentId: (BigInt('9007199254741200') + BigInt(index)).toString(), eventKind: 'sale.posted', occurrence: '1'},
      chartId: '401', chartVersion: '2', reversalOf: null, description: index === 0 ? '=Formula-like stored description, "quoted"' : `Jurnal tersimpan ${index + 1}`,
      lines: [{mappingKey: 'sale.cash', accountId: '1000', accountCode: 'Cash', debit: '1.00', credit: '0.00'},
        {mappingKey: 'sale.revenue', accountId: '1001', accountCode: 'Revenue', debit: '0.00', credit: '1.00'}]}))};
}
const invalid = (error: unknown) => error instanceof AccessFailure && error.code === 'accounting_report_response_invalid';
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {status, headers: {'Content-Type': 'application/json'}});
function csvBytes(source = snapshot()): Uint8Array {
  const rows = ['"Unit Toko — neraca saldo dan jurnal tersimpan","Bukan penutupan buku yang disetujui"',
    `"Snapshot","${source.snapshotHash}","Dicatat pada","${at}"`, ...source.journals.map(row => `"'${row.id}","${row.businessDate}",1.00,1.00`)];
  return new Uint8Array([0xef, 0xbb, 0xbf, ...new TextEncoder().encode(rows.join('\r\n') + '\r\n')]);
}
function csvResponse(source = snapshot(), options: {bytes?: Uint8Array; headers?: Record<string, string>} = {}): Response {
  const bytes = options.bytes ?? csvBytes(source);
  return new Response(new Uint8Array(bytes), {headers: {'Content-Type': 'text/csv; charset=utf-8', 'Content-Disposition': 'attachment; filename=unit-toko-report.csv; filename*=UTF-8\'\'unit-toko-report.csv',
    'Content-Length': String(bytes.byteLength), 'X-Read-Id': '9007199254741002', 'X-Snapshot-Read-Id': source.readId,
    'X-Snapshot-Hash': source.snapshotHash, 'Cache-Control': 'private, no-store', 'X-Content-Type-Options': 'nosniff', ...options.headers}});
}
const gateway = (send: AccountingReportTransport['send'], now = () => Date.parse(at)) => createAccountingReportGateway({send}, now);

test('report choices validate lossless identities, exact book labels, explicit currency and duplicate scopes', () => {
  assert.deepEqual(parseAccountingReportBooks({readId: '81', items: [book]}), {readId: '81', items: [book]});
  assert.deepEqual(parseAccountingReportBooks({readId: '81', items: []}), {readId: '81', items: []});
  for (const value of [{readId: 81, items: [book]}, {readId: '81', items: [book, book]}, {readId: '81', items: [{...book, id: Number(book.id)}]},
    {readId: '81', items: [{...book, key: {...book.key, currency: 'idr'}}]}, {readId: '81', items: [{...book, shop: {...book.shop, id: '999'}}]},
    {readId: '81', items: [book], role: 'admin'}, {readId: '81', items: [{...book, organization: {...book.organization, name: 'Hidden\u202econtrol'}}]}])
    assert.throws(() => parseAccountingReportBooks(value), invalid);
});

test('calendar input validates real inclusive dates independently of browser UTC truncation', () => {
  assert.equal(accountingReportDate('2024-02-29'), '2024-02-29'); assert.equal(accountingReportDate('9999-12-31'), '9999-12-31');
  for (const value of ['2026-02-29', '2026-04-31', '2026-1-01', '0000-01-01', '0001-01-01', '2026-10-04\n', '2026-10-04T00:00:00Z'])
    assert.throws(() => accountingReportDate(value), RequestNotSentError);
});

test('snapshot retains original historical chart codes, zero chart accounts and all 21 journal details', () => {
  const source = snapshot(); const result = parseAccountingReportSnapshot(source, book, endDate);
  assert.deepEqual(result, source); assert.equal(result.journals.length, 21); assert.equal(result.accounts[2].balance, '0.00');
  assert.deepEqual(result.accounts[0].accountCodes, ['Cash', 'HistoricalCash']);
  assert.equal(result.journals[0].description, '=Formula-like stored description, "quoted"');
  assert.deepEqual(parseAccountingReportSnapshot(snapshot(0), book, endDate), snapshot(0));
  const repeated = snapshot(1); repeated.journals[0].lines.push(...structuredClone(repeated.journals[0].lines));
  repeated.debit = repeated.credit = '2.00'; repeated.accounts[0].debit = repeated.accounts[0].balance = '2.00';
  repeated.accounts[1].credit = '2.00'; repeated.accounts[1].balance = '-2.00';
  assert.deepEqual(parseAccountingReportSnapshot(repeated, book, endDate), repeated);
  const longReason = snapshot(1); longReason.journals[0].description = 'x'.repeat(2000);
  assert.equal(parseAccountingReportSnapshot(longReason, book, endDate).journals[0].description.length, 2000);
  const historicalReason = snapshot(1); historicalReason.journals[0].description = 'Alasan cafe\u0301 asli';
  assert.equal(parseAccountingReportSnapshot(historicalReason, book, endDate).journals[0].description, historicalReason.journals[0].description);
});

test('snapshot rejects changed book/date, unsafe totals, wrong reporting semantics and unexpected private fields', () => {
  const source = snapshot();
  for (const value of [{...source, book: {...book, id: '999'}}, {...source, endDate: '2026-10-03'}, {...source, debit: 21},
    {...source, credit: '20.00'}, {...source, debit: '021.00'}, {...source, debit: '21.000'}, {...source, timeZone: 'UTC'},
    {...source, cumulative: false}, {...source, isApprovedClose: true}, {...source, snapshotHash: 'A'.repeat(64)},
    {...source, expiresAt: '2026-10-04T12:16:00.000Z'}, {...source, privateBankAccount: 'unexpected'}])
    assert.throws(() => parseAccountingReportSnapshot(value, book, endDate), invalid);
});

test('snapshot rejects duplicate journals, unsupported truncation, changed order and unknown account/chart labels', () => {
  const source = snapshot(2), first = source.journals[0];
  const values: unknown[] = [{...source, journals: [first, first]}, {...source, journals: [...source.journals].reverse()},
    {...source, journals: [{...first, businessDate: '2026-10-05'}]}, {...source, journals: [{...first, lines: [{...first.lines[0], accountId: '999'}, first.lines[1]]}]},
    {...source, journals: [{...first, lines: [{...first.lines[0], accountCode: 'Unmapped'}, first.lines[1]]}]},
    {...source, journals: [{...first, lines: [{...first.lines[0], credit: '1.00'}, first.lines[1]]}]},
    {...source, journals: [{...first, reversalOf: '42'}]}, {...source, accounts: [...source.accounts].reverse()},
    {...source, accounts: [{...source.accounts[0], accountCodes: ['HistoricalCash', 'Cash']}, ...source.accounts.slice(1)]},
    {...source, accounts: [{...source.accounts[0], balance: '-0.00'}, ...source.accounts.slice(1)]}, snapshot(501)];
  for (const value of values) assert.throws(() => parseAccountingReportSnapshot(value, book, endDate), invalid);
});

test('money display preserves every minor-unit digit and signed balances without Number conversion', () => {
  assert.equal(formatAccountingReportMoney('999999999999999.99', 'IDR'), 'Rp 999.999.999.999.999,99');
  assert.equal(formatAccountingReportMoney('-90071992547409.93', 'IDR'), 'Rp -90.071.992.547.409,93');
  assert.equal(formatAccountingReportMoney('0.00', 'USD'), 'USD 0,00');
  for (const value of ['1e3', '1.001', '-0.00', '9999999999999999.99']) assert.throws(() => formatAccountingReportMoney(value, 'IDR'), invalid);
});

test('book, snapshot and CSV use fresh same-origin CSRF POST reads with the original actor and no mutation headers', async () => {
  const requests: Request[] = [], source = snapshot();
  const fetcher: typeof fetch = async input => {
    const request = input as Request; requests.push(request);
    if (request.url.endsWith('/csrf')) return json({requestToken: 'fresh-request-only'});
    if (request.url.endsWith('/books/query')) return json({readId: '81', items: [book]});
    if (request.url.endsWith('/export')) return csvResponse(source); return json(source);
  };
  const api = createAccountingReportGateway(createAccountingReportTransport({fetch: fetcher, origin: 'https://portal.example.test'}), () => Date.parse(at));
  await api.books(actor, () => true); const shown = await api.read(actor, book, endDate, () => true); await api.export(actor, shown, () => true);
  const posts = requests.filter(request => request.method === 'POST'); assert.equal(posts.length, 3); assert.equal(requests.filter(request => request.url.endsWith('/csrf')).length, 3);
  for (const request of posts) {
    assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actor); assert.equal(request.headers.get('X-CSRF-Token'), 'fresh-request-only');
    assert.equal(request.headers.get('Idempotency-Key'), null); assert.equal(request.headers.get('If-Match'), null);
  }
  assert.deepEqual(await posts[1].json(), {purpose: 'accounting-reporting', key: book.key, endDate});
  assert.deepEqual(await posts[2].json(), {purpose: 'accounting-reporting', readId: source.readId, snapshotHash: source.snapshotHash});
});

test('source or actor changes before CSRF or during response never publish a stale report', async () => {
  let active = true, posts = 0;
  const fetcher: typeof fetch = async input => {const request = input as Request; if (request.method === 'POST') posts++; active = false; return json({requestToken: 'csrf'});};
  const api = createAccountingReportGateway(createAccountingReportTransport({fetch: fetcher, origin: 'https://portal.example.test'}));
  await assert.rejects(api.books(actor, () => active), RequestNotSentError); assert.equal(posts, 0);
  active = true; const changed = gateway(async () => {active = false; return json(snapshot());});
  await assert.rejects(changed.read(actor, book, endDate, () => active), /accounting_report_view_stale/);
  const gate = new CatalogReadGate(), earlier = gate.begin(actor, 'book:date-a'), current = gate.begin(actor, 'book:date-b');
  assert.ok(earlier.signal.aborted); assert.equal(earlier.current(), false); assert.equal(current.current(), true); gate.cancel(); assert.equal(current.current(), false);
});

test('CSV request exports the whole original snapshot and returned bytes include journals beyond the visible page', async () => {
  const source = snapshot(), sent: unknown[] = [], expectedBytes = csvBytes(source);
  const api = gateway(async request => {sent.push(request.body); return csvResponse(source, {bytes: expectedBytes});});
  const result = await api.export(actor, source, () => true);
  assert.deepEqual(sent, [{purpose: 'accounting-reporting', readId: source.readId, snapshotHash: source.snapshotHash}]);
  assert.deepEqual(result.bytes, expectedBytes); assert.equal(result.fileName, 'unit-toko-report.csv');
  assert.equal(result.snapshotReadId, source.readId); assert.equal(result.snapshotHash, source.snapshotHash);
  assert.match(new TextDecoder().decode(result.bytes), new RegExp(source.journals[20].id));
});

test('CSV rejects mismatched owner/binding, invalid download names, unsafe type/cache and declared oversize', async () => {
  const source = snapshot();
  const mutations: Record<string, string>[] = [
    {'X-Snapshot-Read-Id': '999'}, {'X-Snapshot-Hash': 'b'.repeat(64)}, {'X-Read-Id': source.readId}, {'X-Read-Id': '01'},
    {'Content-Disposition': 'inline; filename=report.csv'}, {'Content-Disposition': 'attachment; filename=../report.csv'},
    {'Content-Disposition': 'attachment; filename=CON.csv'}, {'Content-Disposition': 'attachment; filename=report.csv; filename*=UTF-8\'\'other.csv'},
    {'Content-Type': 'text/html; charset=utf-8'}, {'Cache-Control': 'public, max-age=60'}, {'X-Content-Type-Options': 'unsafe'},
    {'Content-Length': String(accountingReportLimits.csvBytes + 1)}, {'Content-Length': '01'}, {'Content-Length': '5'},
  ];
  for (const headers of mutations) await assert.rejects(gateway(async () => csvResponse(source, {headers})).export(actor, source, () => true), invalid);
});

test('CSV requires complete UTF-8 BOM, CRLF output and the original snapshot row', async () => {
  const source = snapshot(), good = csvBytes(source);
  const variants = [good.slice(3), good.slice(0, -2), new Uint8Array([0xef, 0xbb, 0xbf, 0xff, 0x0d, 0x0a]),
    new Uint8Array([0xef, 0xbb, 0xbf, ...new TextEncoder().encode(`"Snapshot","${source.snapshotHash}"\n`)]),
    csvBytes({...source, snapshotHash: 'b'.repeat(64)}), new Uint8Array([0xef, 0xbb, 0xbf, ...new TextEncoder().encode(`"Snapshot","${source.snapshotHash}"\u0000\r\n`)])];
  for (const bytes of variants) await assert.rejects(gateway(async () => csvResponse(source, {bytes})).export(actor, source, () => true), invalid);
});

test('streamed CSV ceiling and changed-view cancellation prevent a private download even without content length', async () => {
  const source = snapshot(); let cancelled = false;
  const headers = Object.fromEntries(csvResponse(source).headers); delete headers['content-length'];
  const tooLarge = new Response(new ReadableStream<Uint8Array>({start(controller) {controller.enqueue(new Uint8Array(accountingReportLimits.csvBytes + 1));}, cancel() {cancelled = true;}}), {headers});
  await assert.rejects(gateway(async () => tooLarge).export(actor, source, () => true), invalid); assert.equal(cancelled, true);
  let active = true, stopped = false;
  const interrupted = new Response(new ReadableStream<Uint8Array>({pull(controller) {controller.enqueue(csvBytes(source)); active = false;}, cancel() {stopped = true;}}), {headers});
  await assert.rejects(gateway(async () => interrupted).export(actor, source, () => active), /accounting_report_view_stale/); assert.equal(stopped, true);
});

test('expired snapshot blocks export before sending, expiry during a response requires a new read and failures are not retried', async () => {
  const source = snapshot(); let sends = 0, clock = Date.parse(source.expiresAt);
  const expired = gateway(async () => {sends++; return csvResponse(source);}, () => clock);
  await assert.rejects(expired.export(actor, source, () => true), /request_not_sent/); assert.equal(sends, 0);
  clock = Date.parse(at);
  const deadline = gateway(async () => {sends++; clock = Date.parse(source.expiresAt); return csvResponse(source);}, () => clock);
  await assert.rejects(deadline.export(actor, source, () => true), /accounting_report_snapshot_stale/); assert.equal(sends, 1);
  const failed = gateway(async () => {sends++; return json({code: 'accounting_report_snapshot_stale'}, 409);});
  await assert.rejects(failed.export(actor, source, () => true), /accounting_report_snapshot_stale/); assert.equal(sends, 2);
});
