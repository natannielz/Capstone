import test from 'node:test';
import assert from 'node:assert/strict';
import {createOrderViewGateway, orderHistoryCursor, orderHistorySharePath, parseOrderHistoryShare, parseOrderHistoryCursor,
  parseOrderViewResult, prepareOrderViewCommand} from '../src/modules/ordering/order-view-client';
import {OrderViewJournal, type OrderViewReference} from '../src/modules/ordering/order-view-journal';
import {AccessFailure} from '../src/modules/administration/workflow';

const chosen = {context: {organizationId: '10', branchId: '11', divisionId: null}, sort: 'oldest' as const};
const recordedAt = '2026-10-08T00:00:00.000Z';
class MemoryStorage {
  values = new Map<string, string>(); get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
test('shared query preserves only the supported context and direction; malformed or widened links are refused', () => {
  const path = orderHistorySharePath(chosen); assert.deepEqual(parseOrderHistoryShare(path.slice(path.indexOf('?'))), chosen);
  assert.deepEqual(parseOrderHistoryShare(''), {context: null, sort: 'newest'});
  for (const value of ['?ov=1&sort=price', '?ov=1&sort=newest&actor=42', '?ov=1&sort=newest&branch=11', '?ov=1&sort=newest&sort=oldest', '?ov=1&sort=newest&organization=01&branch=11', '?ov=1&sort=newest&organization=10&branch=11&division=0']) assert.throws(() => parseOrderHistoryShare(value));
  assert.equal(path.includes('actor'), false); assert.equal(path.includes('viewId'), false);
});
test('stable-ID cursor binds exact context and direction above safe JS integer range', () => {
  const cursor = orderHistoryCursor(chosen, '9007199254740993'); assert.equal(parseOrderHistoryCursor(chosen, cursor), '9007199254740993');
  assert.throws(() => parseOrderHistoryCursor({...chosen, sort: 'newest'}, cursor));
  assert.throws(() => parseOrderHistoryCursor({...chosen, context: null}, cursor));
  assert.throws(() => parseOrderHistoryCursor(chosen, cursor + '\n'));
});
test('journal survives new instances and two tabs retain independent original keys without names or filter data', () => {
  const storage = new MemoryStorage(), first = new OrderViewJournal(() => storage), second = new OrderViewJournal(() => storage);
  const a: OrderViewReference = {actorId: '42', action: 'create', viewId: null, revision: '0', key: 'first', createdAt: recordedAt};
  const b: OrderViewReference = {...a, action: 'rename', viewId: '100', revision: '1', key: 'second'};
  first.remember(a); second.remember(b); assert.equal(first.list('42').length, 2);
  assert.throws(() => first.remember({...a, action: 'remove', viewId: '100', revision: '1'}));
  new OrderViewJournal(() => storage).confirmed(a); assert.deepEqual(second.list('42'), [b]); assert.deepEqual(first.list('43'), []);
  for (const serialized of storage.values.values()) {
    assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['action', 'actorId', 'createdAt', 'key', 'revision', 'viewId']);
  }
  assert.throws(() => second.confirmed({...b, revision: '2'})); assert.deepEqual(second.list('42'), [b]);
});
test('blocked or corrupted persistence refuses a command reference instead of pretending restart recovery exists', () => {
  const storage = new MemoryStorage(), journal = new OrderViewJournal(() => storage);
  storage.setItem = () => {}; assert.throws(() => journal.remember({actorId: '42', action: 'create', viewId: null, revision: '0', key: 'one', createdAt: recordedAt}));
  storage.values.set('unit-toko.order-view.pending.42.bad', JSON.stringify({actorId: '42', action: 'create', viewId: null, revision: '0', key: 'other', createdAt: recordedAt}));
  assert.throws(() => journal.list('42'));
});
test('original result recovery is action/view/revision bound, never latest-view inference', () => {
  const result = {viewId: '100', versionId: '200', revision: '2', action: 'rename', recordedAt};
  assert.deepEqual(parseOrderViewResult(result, {action: 'rename', viewId: '100', revision: '1'}), result);
  for (const invalid of [{...result, action: 'remove'}, {...result, viewId: '101'}, {...result, revision: '3'}]) assert.throws(() => parseOrderViewResult(invalid, {action: 'rename', viewId: '100', revision: '1'}));
});
test('post-dispatch account change retains unknown outcome and dispatches exactly once', async () => {
  let current = true, writes = 0;
  const gateway = createOrderViewGateway(async input => {
    if (String(input) === '/api/v1/identity/csrf' || input instanceof Request && new URL(input.url).pathname.endsWith('/csrf')) return Response.json({requestToken: 'csrf'});
    writes++; current = false; return Response.json({viewId: '100', versionId: '200', revision: '1', action: 'create', recordedAt});
  }, 'https://test.invalid/api/v1/identity/csrf');
  const command = prepareOrderViewCommand('42', 'create', null, 'Cabang', chosen);
  await assert.rejects(gateway.change(command, 'original', () => current), error => error instanceof AccessFailure && error.status === 503);
  assert.equal(writes, 1);
});
test('repeat denied/conflict/unknown recovery never clears the retained reference or sends a replacement command', async () => {
  const storage = new MemoryStorage(), journal = new OrderViewJournal(() => storage), ref: OrderViewReference = {actorId: '42', action: 'create', viewId: null, revision: '0', key: 'original', createdAt: recordedAt};
  journal.remember(ref); const calls: string[] = []; let status = 403;
  const gateway = createOrderViewGateway(async input => {
    if (input instanceof Request && new URL(input.url).pathname.endsWith('/csrf')) return Response.json({requestToken: 'csrf'});
    calls.push(String(input)); return status === 200 ? Response.json({status: 'not_confirmed', result: null}) : Response.json({code: 'unavailable'}, {status});
  }, 'https://test.invalid/api/v1/identity/csrf');
  for (status of [403, 409, 503]) {await assert.rejects(gateway.recover('42', ref.key)); assert.deepEqual(journal.list('42'), [ref]);}
  status = 200; assert.equal(await gateway.recover('42', ref.key), null); assert.deepEqual(journal.list('42'), [ref]);
  assert.equal(calls.length, 4); assert.ok(calls.every(path => path.endsWith('/operations/query')));
});
