import test from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, type OperationRecoveryAdapters,
  type LegacyRecoveryAdapter} from '../src/modules/administration/operation-journal';
import {operationLabel, recoveryPage} from '../src/modules/administration/operation-recovery-navigation';
import {accountContactKey, accountContactKeyActor, accountContactOperation, createAccountContactGateway,
  createAccountContactTransport, createAccountContactWorkflow, parseAccountContactCurrent, parseAccountContactHistory,
  parseAccountContactResult, prepareAccountContact, type AccountContactTransport} from '../src/modules/identity/account-contact-client';

const actor = '9007199254740993', otherActor = '9007199254740995', at = '2026-10-04T12:00:00.000Z';
const uuid = 'd92ecbec-a1cf-4b68-a961-e47bd5e11527', key = accountContactKey(actor, uuid);
const blank = {userId: actor, revision: '0', name: null, phone: null, position: null, address: null, updatedAt: null};
const contact = {name: 'Kontak Cabang', phone: '021-1234', position: 'PIC cabang', address: 'Jakarta'};
const saved = {userId: actor, revision: '1', historyId: '9007199254740997', recordedAt: at};
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {status, headers: {'Content-Type': 'application/json'}});
const command = () => prepareAccountContact(actor, blank, {...contact, phone: contact.phone ?? '', position: contact.position ?? '', address: contact.address ?? ''}, 'Perbarui kontak cabang');
const transport = (send: AccountContactTransport['send']) => createAccountContactGateway({send});
class MemoryStorage {
  values = new Map<string, string>();
  get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
function setup(send: AccountContactTransport['send']) {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage, () => new Date(at));
  const gateway = transport(send), workflow = createAccountContactWorkflow(gateway, journal, () => uuid);
  return {storage, journal, gateway, workflow};
}
const invalidResponse = (error: unknown) => error instanceof AccessFailure && error.code === 'account_contact_response_invalid';

test('contact response keeps lossless identity and separates absent declarations from verified identity', () => {
  assert.deepEqual(parseAccountContactCurrent(blank, actor), blank);
  assert.deepEqual(parseAccountContactCurrent({...blank, revision: '256', ...contact, updatedAt: at}, actor), {...blank, revision: '256', ...contact, updatedAt: at});
  assert.deepEqual(parseAccountContactResult(saved, actor), saved);
  for (const invalid of [{...blank, userId: otherActor}, {...blank, userId: Number(actor)}, {...blank, revision: '00'}, {...blank, revision: '257'},
    {...blank, name: 'Unexpected declaration'}, {...blank, updatedAt: at}, {...blank, revision: '1'}, {...blank, email: 'extra@example.test'},
    {...blank, revision: '1', ...contact, name: 'Not\u200e visible', updatedAt: at}, {...blank, revision: '1', ...contact, updatedAt: '2026-02-30T12:00:00Z'}])
    assert.throws(() => parseAccountContactCurrent(invalid, actor), invalidResponse);
  for (const invalid of [{...saved, revision: '0'}, {...saved, historyId: '01'}, {...saved, userId: otherActor}, {...saved, providerEmail: 'extra@example.test'}])
    assert.throws(() => parseAccountContactResult(invalid, actor), invalidResponse);
});

test('form preparation normalizes NFC, trims optional blanks and counts Unicode runes instead of UTF-16 units', () => {
  const prepared = prepareAccountContact(actor, blank, {name: '  Cafe\u0301  ', phone: ' ', position: '😀'.repeat(160), address: ' Jakarta '}, '  Catatan awal  ');
  assert.deepEqual(prepared, {actorId: actor, expectedRevision: '0', body: {purpose: 'account-contact', name: 'Café', phone: null,
    position: '😀'.repeat(160), address: 'Jakarta', reason: 'Catatan awal'}});
  assert.ok(Object.isFrozen(prepared)); assert.ok(Object.isFrozen(prepared.body));
});

test('invalid mandatory data, Unicode controls, oversized fields and exhausted revisions stop before sending', () => {
  const fields = {name: 'Kontak', phone: '', position: '', address: ''};
  for (const change of [{name: ''}, {name: '😀'.repeat(161)}, {phone: '1'.repeat(41)}, {position: 'x'.repeat(161)},
    {address: 'x'.repeat(1001)}, {name: '\ud800'}, {phone: '123\u0000'}, {address: 'A\nB'}, {position: 'PIC\u202e'}])
    assert.throws(() => prepareAccountContact(actor, blank, {...fields, ...change}, 'Perubahan'), RequestNotSentError);
  for (const reason of ['', 'x'.repeat(1001), 'A\tB', 'A\u200eB']) assert.throws(() => prepareAccountContact(actor, blank, fields, reason), RequestNotSentError);
  assert.throws(() => prepareAccountContact(actor, {...blank, revision: '256', ...contact, updatedAt: at}, fields, 'Perubahan'), RequestNotSentError);
  assert.throws(() => prepareAccountContact(otherActor, blank, fields, 'Perubahan'), invalidResponse);
});

test('history validates descending immutable snapshots and the exact last-row pagination cursor', () => {
  const items = [{historyId: '102', revision: '2', contact, recordedAt: at, reason: 'Koreksi kontak'}, {historyId: '101', revision: '1', contact, recordedAt: at, reason: 'Kontak pertama'}];
  assert.deepEqual(parseAccountContactHistory({items, nextBeforeHistoryId: '101'}, null, 2), {items, nextBeforeHistoryId: '101'});
  assert.deepEqual(parseAccountContactHistory({items: [], nextBeforeHistoryId: null}, '101', 10), {items: [], nextBeforeHistoryId: null});
  for (const value of [{items, nextBeforeHistoryId: '102'}, {items, nextBeforeHistoryId: '101', actorId: actor},
    {items: [items[1], items[0]], nextBeforeHistoryId: null}, {items: [items[0], items[0]], nextBeforeHistoryId: null},
    {items: [{...items[0], revision: '0'}], nextBeforeHistoryId: null}, {items: [{...items[0], contact: {...contact, role: 'admin'}}], nextBeforeHistoryId: null}])
    assert.throws(() => parseAccountContactHistory(value, null, 2), invalidResponse);
  assert.throws(() => parseAccountContactHistory({items, nextBeforeHistoryId: null}, '102', 2), invalidResponse);
  assert.throws(() => parseAccountContactHistory({items, nextBeforeHistoryId: null}, null, 1), invalidResponse);
});

test('contact keys and journal bind the canonical actor and reject injected request or provider data', () => {
  const {storage, journal} = setup(async () => {assert.fail('No network call expected');});
  assert.equal(accountContactKeyActor(key), actor);
  for (const invalid of [key.replace(actor, '01'), key.replace(actor, '9223372036854775808'), key.replace(uuid, uuid.toUpperCase()),
    key + '\n', key + '.extra', key.replace('contact.', 'profile.'), key.replace('-4b68-', '-1b68-')])
    assert.throws(() => journal.remember(actor, accountContactOperation, invalid), /recovery_storage_unavailable/);
  assert.throws(() => journal.remember(otherActor, accountContactOperation, key), /recovery_storage_unavailable/);
  const entry = journal.remember(actor, accountContactOperation, key), storedKey = storage.key(0)!;
  storage.setItem(storedKey, JSON.stringify({...entry, contact})); assert.throws(() => journal.list(actor), /recovery_storage_unavailable/);
  assert.deepEqual(journal.list(otherActor), []);
});

test('all contact requests use same-origin POST, fresh CSRF and an actor header without caller-selected account fields', async () => {
  const requests: Request[] = [], fetcher: typeof fetch = async input => {
    const request = input as Request; requests.push(request);
    if (request.url.endsWith('/csrf')) return json({requestToken: 'csrf-memory-only'});
    if (request.url.endsWith('/history/query')) return json({items: [], nextBeforeHistoryId: null});
    if (request.url.endsWith('/operations/query')) return json({result: saved});
    if (request.url.endsWith('/query')) return json(blank);
    return json(saved);
  };
  const gateway = createAccountContactGateway(createAccountContactTransport({fetch: fetcher, origin: 'https://portal.example.test'}));
  await gateway.current(actor, () => true); await gateway.history(actor, null, 10, () => true);
  await gateway.save(command(), key, () => true);
  await gateway.recover(actor, {actorId: actor, operation: accountContactOperation, key, createdAt: at}, () => true);
  const posts = requests.filter(request => request.method === 'POST');
  assert.equal(posts.length, 4); assert.equal(requests.filter(request => request.url.endsWith('/csrf')).length, 4);
  for (const request of posts) {
    assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');
    assert.equal(request.headers.get('X-CSRF-Token'), 'csrf-memory-only'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actor);
    const body = await request.json() as Record<string, unknown>;
    assert.equal(body.purpose, 'account-contact'); assert.equal('userId' in body, false); assert.equal('actorId' in body, false);
  }
  assert.equal(posts[2].headers.get('If-Match'), '"0"'); assert.equal(posts[2].headers.get('Idempotency-Key'), key);
});

test('actor change during CSRF preparation prevents dispatch and leaves no uncertain submission', async () => {
  let active = true, posts = 0;
  const fetcher: typeof fetch = async input => {
    const request = input as Request;
    if (request.method === 'POST') {posts++; return json(saved);}
    active = false; return json({requestToken: 'fresh'});
  };
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage, () => new Date(at));
  const gateway = createAccountContactGateway(createAccountContactTransport({fetch: fetcher, origin: 'https://portal.example.test'}));
  const workflow = createAccountContactWorkflow(gateway, journal, () => uuid);
  await assert.rejects(workflow.save(command(), () => active), RequestNotSentError);
  assert.equal(posts, 0); assert.deepEqual(journal.list(actor), []);
});

test('lost committed reply persists only the original recovery reference and reload recovers without a second write', async () => {
  let writes = 0, lookups = 0;
  const {storage, journal, gateway, workflow} = setup(async request => {
    if (request.path === '/api/v1/identity/contact') {writes++; throw new TypeError('Reply disconnected after commit');}
    assert.equal(request.path, '/api/v1/identity/contact/operations/query'); lookups++;
    assert.deepEqual(request.body, {purpose: 'account-contact', key}); return json({result: saved});
  });
  await assert.rejects(workflow.save(command(), () => true));
  const entry = journal.list(actor)[0]; assert.deepEqual(entry, {actorId: actor, operation: accountContactOperation, key, createdAt: at});
  assert.doesNotMatch([...storage.values.values()].join(''), /name|phone|address|reason|csrf|Perbarui|Kontak Cabang/);
  const reloadedJournal = new OperationJournal(() => storage), reloaded = createAccountContactWorkflow(gateway, reloadedJournal);
  assert.deepEqual(await reloaded.recover(actor, entry, () => true), saved);
  assert.deepEqual(reloadedJournal.list(actor), []); assert.equal(writes, 1); assert.equal(lookups, 1);
});

test('missing receipt stays unresolved and blocks new contact changes without auto-resend or close', async () => {
  let sends = 0;
  const {journal, workflow} = setup(async request => {sends++; assert.equal(request.path, '/api/v1/identity/contact/operations/query'); return json({result: null});});
  const entry = journal.remember(actor, accountContactOperation, key);
  assert.equal(await workflow.recover(actor, entry, () => true), null);
  await assert.rejects(workflow.save(command(), () => true), error => error instanceof AccessFailure && error.code === 'resolve_previous_attempt');
  assert.deepEqual(journal.list(actor), [entry]); assert.equal(sends, 1);
});

test('synchronous journal reservation prevents duplicate submission while the first response is pending', async () => {
  let writes = 0, reply!: (response: Response) => void;
  const response = new Promise<Response>(resolve => {reply = resolve;});
  const {workflow, journal} = setup(async () => {writes++; return response;});
  const first = workflow.save(command(), () => true);
  await assert.rejects(workflow.save(command(), () => true), /resolve_previous_attempt/);
  assert.equal(writes, 1); assert.equal(journal.list(actor).length, 1);
  reply(json(saved)); assert.deepEqual(await first, saved); assert.deepEqual(journal.list(actor), []);
});

test('malformed and foreign mutation replies remain unresolved instead of allowing another write', async () => {
  for (const result of [{...saved, userId: otherActor}, {...saved, revision: '2'}, {...saved, historyId: 42}, {...saved, providerEmail: 'extra@example.test'}]) {
    const {workflow, journal} = setup(async () => json(result));
    await assert.rejects(workflow.save(command(), () => true), invalidResponse);
    assert.equal(journal.list(actor).length, 1); await assert.rejects(workflow.save(command(), () => true), /resolve_previous_attempt/);
  }
});

test('view changes after dispatch retain lookup-only recovery, while known rejection clears the attempt', async () => {
  let active = true;
  const uncertain = setup(async () => {active = false; return json(saved);});
  await assert.rejects(uncertain.workflow.save(command(), () => active), /commit_outcome_unknown/);
  assert.equal(uncertain.journal.list(actor).length, 1);
  const rejected = setup(async () => json({code: 'account_contact_changed'}, 409));
  await assert.rejects(rejected.workflow.save(command(), () => true), /account_contact_changed/);
  assert.deepEqual(rejected.journal.list(actor), []);
});

test('unavailable recovery storage prevents writes and cleanup failure retains the original key', async () => {
  let writes = 0;
  const gateway = transport(async () => {writes++; return json(saved);});
  const blocked = createAccountContactWorkflow(gateway, new OperationJournal(() => {throw new Error('Storage denied');}), () => uuid);
  await assert.rejects(blocked.save(command(), () => true), /recovery_storage_unavailable/); assert.equal(writes, 0);
  const storage = new MemoryStorage(), journal = new OperationJournal(() => ({...storage, get length() {return storage.length;}, key: storage.key.bind(storage),
    getItem: storage.getItem.bind(storage), setItem: storage.setItem.bind(storage), removeItem() {throw new Error('Cleanup denied');}}), () => new Date(at));
  const workflow = createAccountContactWorkflow(gateway, journal, () => uuid);
  assert.deepEqual(await workflow.save(command(), () => true), saved);
  assert.equal(journal.list(actor).length, 1); await assert.rejects(workflow.save(command(), () => true), /resolve_previous_attempt/); assert.equal(writes, 1);
});

test('recovery is scoped to the original actor and separates historical receipt from latest contact', async () => {
  let lookups = 0;
  const {journal, workflow, gateway} = setup(async request => {
    if (request.path.endsWith('/operations/query')) {lookups++; return json({result: saved});}
    assert.equal(request.path, '/api/v1/identity/contact/query'); return json({...blank, revision: '2', ...contact, name: 'Kontak berikutnya', updatedAt: at});
  });
  const entry = journal.remember(actor, accountContactOperation, key);
  await assert.rejects(workflow.recover(otherActor, entry, () => true), /actor_changed/); assert.equal(lookups, 0);
  assert.deepEqual(await workflow.recover(actor, entry, () => true), saved);
  assert.equal((await gateway.current(actor, () => true)).revision, '2'); assert.equal(lookups, 1);
});

const legacy: LegacyRecoveryAdapter = {async lookup() {assert.fail('Contact recovery must never use access receipts');}, async close() {assert.fail('No contact close command exists');}};
test('global recovery routes contact work to its owning page, requires its adapter and disallows close', async () => {
  const {journal} = setup(async () => {assert.fail('No default network');}); const entry = journal.remember(actor, accountContactOperation, key);
  assert.deepEqual(operationRecoveryCapabilities(accountContactOperation), {canCheck: false, canClose: false, unavailable: 'adapter'});
  const adapters: OperationRecoveryAdapters = {'account.contact': {lookup: async () => ({state: 'committed', result: saved})}};
  assert.deepEqual(operationRecoveryCapabilities(accountContactOperation, adapters), {canCheck: true, canClose: false, unavailable: null});
  assert.deepEqual(recoveryPage(accountContactOperation), {href: '/identity/account#account-contact', label: 'halaman kontak pribadi'});
  assert.equal(operationLabel(accountContactOperation), 'Perubahan kontak pribadi');
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy), /recovery_adapter_unavailable/);
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, 'Close request'), /recovery_close_unsupported/);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'committed', result: {id: saved.historyId, version: saved.revision}});
  assert.deepEqual(journal.list(actor), []);
});

test('global recovery preserves unresolved references for foreign, malformed, missing and stale replies', async () => {
  for (const receipt of [{state: 'committed', result: {...saved, userId: otherActor}}, {state: 'committed', result: saved, contact}, {state: 'closed', result: null}, null]) {
    const {journal} = setup(async () => {assert.fail('No network');}); const entry = journal.remember(actor, accountContactOperation, key);
    const adapters = {'account.contact': {lookup: async () => receipt}} as unknown as OperationRecoveryAdapters;
    if (receipt === null) assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'unconfirmed'});
    else await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters));
    assert.deepEqual(journal.list(actor), [entry]);
  }
  let active = true, reads = 0;
  const {journal} = setup(async () => {assert.fail('No network');}); const entry = journal.remember(actor, accountContactOperation, key);
  const adapters: OperationRecoveryAdapters = {'account.contact': {lookup: async () => {reads++; active = false; return {state: 'committed', result: saved};}}};
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, () => active), /recovery_request_stale/);
  assert.equal(reads, 1); assert.deepEqual(journal.list(actor), [entry]);
});
