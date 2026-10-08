import assert from 'node:assert/strict';
import test from 'node:test';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';
import {PaymentHandoverJournal, PaymentHandoverStorageError, parsePaymentHandoverReference, type PaymentHandoverReference} from '../src/modules/billing/payment-handover-journal';
import {createPaymentHandoverGateway, paymentHandoverMessage, parsePaymentHandoverPage, parsePaymentHandoverResult, preparePaymentHandover} from '../src/modules/billing/payment-handover-client';

class Memory {
  private values = new Map<string, string>(); get length() {return this.values.size;}
  key(i: number) {return [...this.values.keys()][i] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
const at = '2026-10-09T00:00:00.000Z';
const ref: PaymentHandoverReference = {actorId: '9007199254740993', declarationId: '9223372036854775800', submissionId: '91', reviewRevision: '5',
  delegationId: '71', delegationRevision: '3', delegationEventId: '83', originalAssigneeId: '8', declarationRevision: '2',
  key: 'ph.9223372036854775800.11111111-1111-1111-1111-111111111111', createdAt: at};
const handover = {delegationId: ref.delegationId, delegationRevision: ref.delegationRevision, delegationEventId: ref.delegationEventId,
  originalAssigneeId: ref.originalAssigneeId, declarationRevision: ref.declarationRevision};
const page = {declarationId: ref.declarationId, declarationRevision: ref.declarationRevision, submissionId: ref.submissionId,
  binding: 'a'.repeat(64), reviewRevision: ref.reviewRevision, assigneeId: ref.originalAssigneeId, open: true, canClaim: true,
  items: [{delegationId: ref.delegationId, delegationRevision: ref.delegationRevision, delegationEventId: ref.delegationEventId, state: 'revoked' as const, expiresAt: at}], nextId: null, observedAt: at};
const saved = {declarationId: ref.declarationId, submissionId: ref.submissionId, reviewRevision: '6', actionId: '101', action: 'claim', state: 'assigned',
  assigneeId: ref.actorId, recordedAt: at, operation: 'billing.payment.review.manage', handover};

test('exact original result binds actor, source task revision, delegation event and prior assignee without unsafe numeric coercion', () => {
  assert.deepEqual(parsePaymentHandoverResult(saved, ref), saved);
  for (const value of [{...saved, assigneeId: '7'}, {...saved, reviewRevision: '7'}, {...saved, declarationId: '44'},
    {...saved, handover: {...handover, delegationEventId: '84'}}, {...saved, handover: {...handover, originalAssigneeId: '9'}}])
    assert.throws(() => parsePaymentHandoverResult(value, ref));
  assert.throws(() => parsePaymentHandoverReference({...ref, reason: 'Private review text'}, ref.actorId));
});
test('only explicit current eligible source selection prepares a claim; revoked history supplies no permission', () => {
  const command = preparePaymentHandover(ref.actorId, page, ref.delegationId, 'Pengganti berwenang telah meninjau sumber');
  assert.deepEqual(command.handover, handover); assert.ok(Object.isFrozen(command.body));
  for (const changed of [{...page, canClaim: false}, {...page, assigneeId: ref.actorId}, {...page, open: false, canClaim: false, items: []},
    {...page, items: []}]) assert.throws(() => preparePaymentHandover(ref.actorId, changed, ref.delegationId, 'Reviewed'), RequestNotSentError);
  assert.throws(() => preparePaymentHandover(ref.actorId, page, '99', 'Reviewed'));
});
test('durable two-tab journals never replace another operation and exact clear survives a fresh journal instance', () => {
  const storage = new Memory(), first = new PaymentHandoverJournal(() => storage), second = new PaymentHandoverJournal(() => storage);
  const other = {...ref, key: ref.key.replace('11111111-', '22222222-')}; first.remember(ref); second.remember(other);
  assert.equal(new PaymentHandoverJournal(() => storage).list(ref.actorId).length, 2);
  assert.throws(() => second.remember({...ref, delegationEventId: '99'})); first.confirmed(ref);
  assert.deepEqual(second.list(ref.actorId), [other]); assert.deepEqual(second.list('2'), []);
});
test('storage refusal stops a new reference before dispatch and malformed references do not disappear silently', () => {
  const memory = new Memory(), journal = new PaymentHandoverJournal(() => memory); memory.setItem = () => {};
  assert.throws(() => journal.remember(ref));
  assert.throws(() => parsePaymentHandoverReference({...ref, key: ref.key.replace('5800.', '5801.')}, ref.actorId));
});
test('declared owner/source mismatch and duplicate or reversed page membership are refused', () => {
  const owner = {id: ref.declarationId, revision: ref.declarationRevision, submission: {id: ref.submissionId}} as Parameters<typeof parsePaymentHandoverPage>[1];
  assert.equal(parsePaymentHandoverPage(page, owner).assigneeId, ref.originalAssigneeId);
  assert.throws(() => parsePaymentHandoverPage({...page, submissionId: '92'}, owner));
  assert.throws(() => parsePaymentHandoverPage({...page, items: [page.items[0], page.items[0]]}, owner));
});
test('after-dispatch stale session and 403/409/unknown recovery keep the original durable reference', async () => {
  const memory = new Memory(), journal = new PaymentHandoverJournal(() => memory); journal.remember(ref);
  const command = preparePaymentHandover(ref.actorId, page, ref.delegationId, 'Reviewed handover'); let current = true, posts = 0;
  const fetcher = (async (url: RequestInfo | URL) => {
    if ((url instanceof Request ? url.url : String(url)).endsWith('/csrf')) return Response.json({requestToken: 'synthetic-csrf'});
    posts++; current = false; return Response.json(saved);
  }) as typeof fetch;
  await assert.rejects(createPaymentHandoverGateway(fetcher, 'https://unit.test/api/v1/identity/csrf').claim(command, ref, {actorId: ref.actorId, isCurrent: () => current}),
    error => error instanceof AccessFailure && !(error instanceof RequestNotSentError));
  assert.equal(posts, 1); assert.deepEqual(journal.list(ref.actorId), [ref]);
  for (const status of [403, 409, 503]) {
    const gateway = createPaymentHandoverGateway((async (url: RequestInfo | URL) => (url instanceof Request ? url.url : String(url)).endsWith('/csrf') ? Response.json({requestToken: 'synthetic-csrf'}) : Response.json({code: 'refused'}, {status})) as typeof fetch, 'https://unit.test/api/v1/identity/csrf');
    await assert.rejects(gateway.recover(ref, {actorId: ref.actorId, isCurrent: () => true})); assert.deepEqual(journal.list(ref.actorId), [ref]);
  }
});
test('not-confirmed never fabricates a result and later successful original-key lookup is exact', async () => {
  const requests: unknown[] = []; let result: unknown = null;
  const gateway = createPaymentHandoverGateway((async (url: RequestInfo | URL, init?: RequestInit) => {
    if ((url instanceof Request ? url.url : String(url)).endsWith('/csrf')) return Response.json({requestToken: 'synthetic-csrf'});
    requests.push(JSON.parse(String(init?.body))); return Response.json({state: result ? 'committed' : 'not_confirmed', result});
  }) as typeof fetch, 'https://unit.test/api/v1/identity/csrf');
  assert.equal(await gateway.recover(ref, {actorId: ref.actorId, isCurrent: () => true}), null); result = saved;
  assert.deepEqual(await gateway.recover(ref, {actorId: ref.actorId, isCurrent: () => true}), saved);
  assert.deepEqual(requests, [{purpose: 'operation-recovery', key: ref.key}, {purpose: 'operation-recovery', key: ref.key}]);
});

test('a lost transport response is uncertain outcome, not storage failure, and keeps the exact original reference', async () => {
  const memory = new Memory(), journal = new PaymentHandoverJournal(() => memory); journal.remember(ref);
  const command = preparePaymentHandover(ref.actorId, page, ref.delegationId, 'Reviewed handover');
  const networkFailure = new TypeError('Failed to fetch'); let posts = 0;
  const gateway = createPaymentHandoverGateway((async (url: RequestInfo | URL) => {
    if ((url instanceof Request ? url.url : String(url)).endsWith('/csrf')) return Response.json({requestToken: 'synthetic-csrf'});
    posts++; throw networkFailure;
  }) as typeof fetch, 'https://unit.test/api/v1/identity/csrf');
  await assert.rejects(gateway.claim(command, ref, {actorId: ref.actorId, isCurrent: () => true}), error => {
    assert.equal(error, networkFailure); assert.match(paymentHandoverMessage(error), /Koneksi terputus.*Hasil belum dapat dipastikan/);
    assert.doesNotMatch(paymentHandoverMessage(error), /penyimpanan|disimpan|dibaca/i); return true;
  });
  assert.equal(posts, 1); assert.deepEqual(journal.list(ref.actorId), [ref]);
  assert.match(paymentHandoverMessage(new AccessFailure(403, 'scope_denied')), /Akses untuk tindakan ini tidak tersedia/);
  assert.match(paymentHandoverMessage(new RequestNotSentError(409, 'payment_handover_stale')), /Permintaan belum dikirim/);
});

test('actual journal read failure identifies storage and leaves the saved original reference intact', () => {
  const memory = new Memory(), journal = new PaymentHandoverJournal(() => memory); journal.remember(ref);
  const get = memory.getItem.bind(memory); memory.getItem = () => {throw new Error('Private browser diagnostic');};
  assert.throws(() => journal.list(ref.actorId), error => {
    assert.ok(error instanceof PaymentHandoverStorageError); assert.equal(error.operation, 'read');
    assert.match(paymentHandoverMessage(error), /Referensi pemulihan belum dapat dibaca/);
    assert.doesNotMatch(paymentHandoverMessage(error), /Private browser diagnostic|Koneksi terputus/); return true;
  });
  memory.getItem = get; assert.deepEqual(journal.list(ref.actorId), [ref]);
});

test('actual journal write refusal has a distinct unsent message and does not remove another tab reference', () => {
  const memory = new Memory(), journal = new PaymentHandoverJournal(() => memory); journal.remember(ref);
  const other = {...ref, key: ref.key.replace('11111111-', '22222222-')}; memory.setItem = () => {};
  assert.throws(() => journal.remember(other), error => {
    assert.ok(error instanceof PaymentHandoverStorageError); assert.equal(error.operation, 'write');
    assert.match(paymentHandoverMessage(error), /Referensi pemulihan belum dapat disimpan.*Permintaan baru belum dikirim/); return true;
  });
  assert.deepEqual(journal.list(ref.actorId), [ref]);
});

test('actual journal cleanup refusal preserves the original key without diagnosing a lost network response', () => {
  const memory = new Memory(), journal = new PaymentHandoverJournal(() => memory); journal.remember(ref); memory.removeItem = () => {};
  assert.throws(() => journal.confirmed(ref), error => {
    assert.ok(error instanceof PaymentHandoverStorageError); assert.equal(error.operation, 'remove');
    assert.match(paymentHandoverMessage(error), /Referensi pemulihan belum dapat dibersihkan/);
    assert.doesNotMatch(paymentHandoverMessage(error), /Koneksi terputus|Permintaan baru belum dikirim/); return true;
  });
  assert.deepEqual(journal.list(ref.actorId), [ref]);
});
