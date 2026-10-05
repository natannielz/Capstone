import {test} from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '@/api/client';
import {AccessFailure} from '@/modules/administration/workflow';
import {OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, type LegacyRecoveryAdapter} from '../src/modules/administration/operation-journal';
import {operationLabel, recoveryPage} from '../src/modules/administration/operation-recovery-navigation';
import {createPaymentSupplementGateway, parseSupplementRecoveryResult, supplementBinding, type PaymentSupplement, type PaymentSupplementOwner} from '../src/modules/billing/payment-pdf-supplement-client';
import {SupplementJournal, sendSupplementOnce, supplementKey, supplementOperations} from '../src/modules/billing/payment-pdf-supplement-recovery';
import {paymentSupplementRecoveryAdapter, paymentSupplementReloadRecoveryAdapter} from '../src/modules/billing/payment-pdf-supplement-recovery-adapter';

const owner: PaymentSupplementOwner = {actorId: '9007199254740993', declarationId: '9007199254740995', declarationRevision: '2', submissionId: '9007199254740997',
  primaryEvidence: {id: '9007199254740999', version: '1', sha256: 'a'.repeat(64)}};
const draft: PaymentSupplement = {declarationId: owner.declarationId, declarationRevision: owner.declarationRevision, submissionId: owner.submissionId,
  primaryEvidence: owner.primaryEvidence, supplementId: '9007199254741001', supplementRevision: '1', state: 'draft', reservationId: null, pdfEvidence: null,
  attachedAt: null, reason: 'Dokumen tambahan dengan foto utama tetap wajib'};
const reserved = {reservationId: '9007199254741003', revision: '1' as const, expiresAt: '2026-10-05T10:15:00Z'};
const attached: PaymentSupplement = {...draft, state: 'attached', supplementRevision: '2', reservationId: reserved.reservationId,
  pdfEvidence: {id: '9007199254741005', version: '1', sha256: 'b'.repeat(64)}, attachedAt: '2026-10-05T10:00:00Z'};
const uuid = '13ef8768-755a-40d4-8e06-204a94f6e9e2';
const keyFor = (operation: typeof supplementOperations[keyof typeof supplementOperations]) =>
  supplementKey(operation, supplementBinding(owner, operation === supplementOperations.create ? null : draft), uuid);
class MemoryStorage {
  readonly values = new Map<string, string>(); get length() {return this.values.size;} key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;} setItem(key: string, value: string) {this.values.set(key, value);} removeItem(key: string) {this.values.delete(key);}
}
const neverLegacy: LegacyRecoveryAdapter = {lookup: async () => assert.fail('No legacy lookup for supplementary PDF'), close: async () => assert.fail('No close or retry')};
const json = (value: unknown) => new Response(JSON.stringify(value), {headers: {'Content-Type': 'application/json'}});
function transport(reply: (request: Request) => Response | Promise<Response>) {
  const requests: Request[] = []; let count = 0;
  const fetcher = (async (input: RequestInfo | URL) => {const request = input instanceof Request ? input : new Request(input); requests.push(request);
    return new URL(request.url).pathname === '/api/v1/identity/csrf' ? json({requestToken: `fresh-${++count}`}) : reply(request);}) as typeof fetch;
  return {requests, gateway: createPaymentSupplementGateway({baseUrl: 'https://unit-toko.test', fetcher})};
}

test('all supplementary actions share existing storage, recovery discovery and actor isolation', () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), wrapper = new SupplementJournal(() => storage);
  for (const operation of Object.values(supplementOperations)) wrapper.remember(owner.actorId, operation, keyFor(operation));
  assert.equal(journal.list(owner.actorId).length, 3); assert.equal(wrapper.list(owner.actorId).length, 3); assert.deepEqual(journal.list('41'), []);
  for (const [key, body] of storage.values) {assert.ok(key.startsWith(`unit-toko:pending:v1:${owner.actorId}:`));
    assert.deepEqual(Object.keys(JSON.parse(body)).sort(), ['actorId', 'createdAt', 'key', 'operation']);}
  for (const operation of Object.values(supplementOperations)) {assert.deepEqual(recoveryPage(operation), {href: '/billing/payments', label: 'halaman lampiran PDF tambahan'});
    assert.match(operationLabel(operation), /tambahan/); assert.deepEqual(operationRecoveryCapabilities(operation), {canCheck: false, canClose: false, unavailable: 'adapter'});}
  assert.throws(() => journal.remember('41', supplementOperations.create, keyFor(supplementOperations.create)), {code: 'recovery_storage_unavailable'});
  assert.throws(() => journal.remember(owner.actorId, supplementOperations.attach, keyFor(supplementOperations.reserve)), {code: 'recovery_storage_unavailable'});
});

test('other unresolved operations block supplementary dispatch and all actions share the established 32 limit', async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), wrapper = new SupplementJournal(() => storage); let writes = 0;
  for (let index = 0; index < 32; index++) journal.remember(owner.actorId, 'billing.payment.draft.save', `existing.${index}`);
  assert.deepEqual(wrapper.list(owner.actorId), []);
  assert.throws(() => wrapper.remember(owner.actorId, supplementOperations.create, keyFor(supplementOperations.create)), {code: 'recovery_storage_unavailable'});
  await assert.rejects(sendSupplementOnce(wrapper, {actorId: owner.actorId, operation: supplementOperations.create, key: keyFor(supplementOperations.create)}, async () => {writes++; return draft;}), {code: 'resolve_previous_attempt'});
  assert.equal(writes, 0); assert.equal(journal.list(owner.actorId).length, 32);
});

test('lost attachment reply reloads into existing recovery and checks its original key without replay', async () => {
  const storage = new MemoryStorage(), wrapper = new SupplementJournal(() => storage); let writes = 0;
  const key = keyFor(supplementOperations.attach);
  await assert.rejects(sendSupplementOnce(wrapper, {actorId: owner.actorId, operation: supplementOperations.attach, key}, async () => {writes++; throw new AccessFailure(503, 'commit_outcome_unknown');}));
  const journal = new OperationJournal(() => storage), [entry] = journal.list(owner.actorId), requests = transport(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/attachment/operations/query')) return json({status: 'committed', result: attached});
    if (path.endsWith(`/${draft.supplementId}/query`)) return json(attached); return assert.fail('Only original detail and recovery allowed');
  });
  const adapter = paymentSupplementRecoveryAdapter(requests.gateway, owner, () => true);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, neverLegacy, {'billing.payment.supplement': adapter}), {state: 'committed', result: {id: attached.supplementId, version: '2'}});
  assert.equal(writes, 1); assert.equal(journal.list(owner.actorId).length, 0);
  const posted = requests.requests.filter(request => request.method === 'POST'); assert.equal(posted.length, 2);
  assert.equal(posted[0].headers.get('If-Match'), '"2"'); assert.equal(posted[1].headers.get('If-Match'), '"2"');
  assert.deepEqual(await posted[1].json(), {purpose: 'operation-recovery', key});
});

test('null, stale, invalid receipt and forbidden close retain the original journal reference', async () => {
  for (const mode of ['null', 'stale', 'wrong_owner', 'extra', 'close']) {
    const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(owner.actorId, supplementOperations.attach, keyFor(supplementOperations.attach));
    let current = true, queries = 0;
    const adapters = {'billing.payment.supplement': {lookup: async () => {queries++; if (mode === 'null') return null;
      if (mode === 'stale') current = false; return mode === 'extra' ? {state: 'committed' as const, result: attached, extra: true} :
        {state: 'committed' as const, result: mode === 'wrong_owner' ? {...attached, submissionId: '41'} : attached};}}};
    const result = reconcileOperationRecovery(entry, journal, neverLegacy, adapters, mode === 'close' ? 'close' : undefined, () => current);
    if (mode === 'null') assert.deepEqual(await result, {state: 'unconfirmed'}); else await assert.rejects(result);
    assert.equal(journal.list(owner.actorId).length, 1); if (mode === 'close') assert.equal(queries, 0);
  }
});

test('recovery adapters reject another actor/source before querying and verify the full immutable tuple', async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(owner.actorId, supplementOperations.attach, keyFor(supplementOperations.attach));
  const requests = transport(() => json({...attached, primaryEvidence: {...attached.primaryEvidence, sha256: 'c'.repeat(64)}}));
  await assert.rejects(paymentSupplementRecoveryAdapter(requests.gateway, {...owner, declarationId: '41'}, () => true).lookup(entry)); assert.equal(requests.requests.length, 0);
  await assert.rejects(paymentSupplementRecoveryAdapter(requests.gateway, {...owner, actorId: '41'}, () => true).lookup(entry)); assert.equal(requests.requests.length, 0);
  await assert.rejects(paymentSupplementRecoveryAdapter(requests.gateway, owner, () => true).lookup(entry), {code: 'payment_supplement_response_invalid'});
  assert.equal(journal.list(owner.actorId).length, 1);
});

test('reserve/create/attach receipts retain distinct result and key bindings including large string IDs', () => {
  for (const [operation, value, expected] of [[supplementOperations.create, draft, {id: draft.supplementId, version: '1'}],
    [supplementOperations.reserve, reserved, {id: reserved.reservationId, version: '1'}], [supplementOperations.attach, attached, {id: attached.supplementId, version: '2'}]] as const)
    assert.deepEqual(parseSupplementRecoveryResult(value, operation, keyFor(operation), owner.actorId), expected);
  assert.throws(() => parseSupplementRecoveryResult(attached, supplementOperations.create, keyFor(supplementOperations.create), owner.actorId));
  assert.throws(() => parseSupplementRecoveryResult({...attached, supplementId: '41'}, supplementOperations.attach, keyFor(supplementOperations.attach), owner.actorId));
  assert.throws(() => parseSupplementRecoveryResult({...reserved, reservationId: 9007199254741003}, supplementOperations.reserve, keyFor(supplementOperations.reserve), owner.actorId));
});

test('requester create/admission/attach carry exact additive payloads and parent/own revision guards', async () => {
  const ready = {...reserved, state: 'quarantined' as const, reference: attached.pdfEvidence, scanStatus: 'clean' as const, decoderStatus: 'decoded' as const, safeFailure: null};
  const requests = transport(request => {const path = new URL(request.url).pathname;
    return path.endsWith('/pdf-evidence') ? json(reserved) : path.endsWith('/attach') ? json({operation: supplementOperations.attach, result: attached}) :
      json({operation: supplementOperations.create, result: draft});});
  await requests.gateway.create(owner, keyFor(supplementOperations.create), draft.reason, () => true);
  await requests.gateway.reserve(owner, draft, keyFor(supplementOperations.reserve), () => true);
  await requests.gateway.attach(owner, draft, ready, draft.reason, keyFor(supplementOperations.attach), () => true);
  const posts = requests.requests.filter(request => request.method === 'POST'); assert.equal(posts.length, 3);
  assert.deepEqual(await posts[0].json(), {submissionId: owner.submissionId, primaryEvidence: owner.primaryEvidence, reason: draft.reason});
  assert.deepEqual(Object.keys(await posts[1].json()), ['reason']);
  assert.deepEqual(await posts[2].json(), {reservationId: reserved.reservationId, evidence: attached.pdfEvidence, reason: draft.reason});
  for (const [index, post] of posts.entries()) {assert.ok(new URL(post.url).pathname.includes('/supplements'));
    assert.equal(post.headers.get('X-Expected-Actor-Id'), owner.actorId); assert.equal(post.headers.get('If-Match'), index === 0 ? '"2"' : '"1"');
    assert.equal(post.headers.get('X-CSRF-Token'), `fresh-${index + 1}`); assert.equal(post.credentials, 'same-origin'); assert.equal(post.redirect, 'error'); assert.equal(post.cache, 'no-store');}
});

test('reviewer list uses dedicated read purpose and policy-unavailable readiness remains honest', async () => {
  const requests = transport(() => json({items: [], nextSupplementId: null, observedAt: '2026-10-05T10:00:00Z', readiness: 'policy_unavailable'}));
  const shown = await requests.gateway.list('reviewer', owner, null, () => true); assert.equal(shown.readiness, 'policy_unavailable');
  const post = requests.requests.find(request => request.method === 'POST')!;
  assert.equal(new URL(post.url).pathname, `/api/v1/billing/payment-reviews/${owner.declarationId}/supplements/query`);
  assert.deepEqual(await post.json(), {purpose: 'payment-review-supplements-list', beforeId: null});
  assert.equal(post.headers.get('If-Match'), '"2"'); assert.equal(post.headers.get('Idempotency-Key'), null);
});

test('cleanup failure after a valid recovery keeps the original reference and reports no replay permission', async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => ({get length() {return storage.length;}, key: storage.key.bind(storage),
    getItem: storage.getItem.bind(storage), setItem: storage.setItem.bind(storage), removeItem() {throw new Error('cleanup denied');}}));
  const entry = journal.remember(owner.actorId, supplementOperations.create, keyFor(supplementOperations.create));
  await assert.rejects(reconcileOperationRecovery(entry, journal, neverLegacy, {'billing.payment.supplement': {lookup: async () => ({state: 'committed', result: draft})}}), {code: 'recovery_cleanup_failed'});
  assert.equal(journal.list(owner.actorId).length, 1);
});

test('permission denial before commit clears a locally retained command, while ambiguous failures remain', async () => {
  const storage = new MemoryStorage(), wrapper = new SupplementJournal(() => storage), entry = {actorId: owner.actorId, operation: supplementOperations.create, key: keyFor(supplementOperations.create)};
  await assert.rejects(sendSupplementOnce(wrapper, entry, async () => {throw new RequestNotSentError(403, 'permission_denied');})); assert.equal(storage.length, 0);
  await assert.rejects(sendSupplementOnce(wrapper, entry, async () => {throw new AccessFailure(503, 'commit_outcome_unknown');})); assert.equal(storage.length, 1);
});

test('refresh recovery resolves a fresh authorized primary before any supplement lookup without requiring a selected screen', async () => {
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(owner.actorId, supplementOperations.create, keyFor(supplementOperations.create));
  let primaryReads = 0; const requests = transport(() => json({status: 'committed', result: draft}));
  const adapter = paymentSupplementReloadRecoveryAdapter(requests.gateway, owner.actorId, async declarationId => {
    primaryReads++; assert.equal(declarationId, owner.declarationId); return owner;
  }, () => true);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, neverLegacy, {'billing.payment.supplement': adapter}), {state: 'committed', result: {id: draft.supplementId, version: '1'}});
  assert.equal(primaryReads, 1); assert.equal(journal.list(owner.actorId).length, 0);
  const post = requests.requests.find(request => request.method === 'POST')!;
  assert.equal(new URL(post.url).pathname, `/api/v1/billing/payment-declarations/${owner.declarationId}/supplements/operations/query`);
  assert.deepEqual(await post.json(), {purpose: 'operation-recovery', key: entry.key});
});

test('fresh-primary recovery retains ambiguous references on another source or session change before supplement access', async () => {
  for (const mode of ['source', 'session', 'actor']) {
    const storage = new MemoryStorage(), journal = new OperationJournal(() => storage), entry = journal.remember(owner.actorId, supplementOperations.create, keyFor(supplementOperations.create));
    let current = true; const requests = transport(() => assert.fail('No supplementary read with stale or foreign primary'));
    const adapter = paymentSupplementReloadRecoveryAdapter(requests.gateway, owner.actorId, async () => {
      if (mode === 'session') current = false;
      return mode === 'source' ? {...owner, submissionId: '41'} : mode === 'actor' ? {...owner, actorId: '41'} : owner;
    }, () => current);
    await assert.rejects(reconcileOperationRecovery(entry, journal, neverLegacy, {'billing.payment.supplement': adapter}));
    assert.equal(requests.requests.length, 0); assert.equal(journal.list(owner.actorId).length, 1);
  }
});
