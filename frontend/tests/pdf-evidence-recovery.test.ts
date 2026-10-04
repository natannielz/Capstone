import test from 'node:test';
import assert from 'node:assert/strict';
import {AccessFailure, MutationLedger} from '../src/modules/administration/workflow';
import {OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest,
  type LegacyRecoveryAdapter, type OperationRecoveryAdapters} from '../src/modules/administration/operation-journal';
import {operationLabel, recoveryPage} from '../src/modules/administration/operation-recovery-navigation';
import {pdfReservationKey, pdfReserveOperation, type PdfEvidenceTarget} from '../src/modules/documents/pdf-evidence-client';

const actor = '9007199254740993', at = '2026-10-05T10:00:00.000Z';
const owner: PdfEvidenceTarget = {actorId: actor, sourceId: '9007199254740995', sourceRevision: '2', sourceKind: 'Payment'};
const uuid = '13ef8768-755a-40d4-8e06-204a94f6e9e2';
const result = {reservationId: '9007199254740997', revision: '1' as const, expiresAt: '2026-10-05T10:15:00Z'};
class MemoryStorage {
  values = new Map<string, string>();
  get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
const legacy: LegacyRecoveryAdapter = {async lookup() {assert.fail('PDF never uses legacy receipts');}, async close() {assert.fail('PDF cannot close an uncertain reservation');}};
function setup(sourceKind: PdfEvidenceTarget['sourceKind'] = 'Payment') {
  const target = {...owner, sourceKind}, key = pdfReservationKey(target, uuid), operation = pdfReserveOperation(target);
  const storage = new MemoryStorage(), journal = new OperationJournal(() => storage, () => new Date(at));
  return {target, key, operation, storage, journal, entry: journal.remember(actor, operation, key)};
}
const adaptersFor = (kind: PdfEvidenceTarget['sourceKind'], lookup: NonNullable<OperationRecoveryAdapters['evidence.payment-pdf']>['lookup']): OperationRecoveryAdapters =>
  ({[kind === 'Payment' ? 'evidence.payment-pdf' : 'evidence.supplier-document-pdf']: {lookup}});

test('PDF recovery journal persists only original actor/action/key/time across reload and rejects foreign or image keys', () => {
  for (const kind of ['Payment', 'SupplierInvoice'] as const) {
    const {storage, journal, entry, key, operation} = setup(kind);
    assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.deepEqual(new OperationJournal(() => storage).list(actor), [entry]);
    assert.deepEqual(journal.list('41'), []);
    assert.throws(() => journal.remember('41', operation, key));
    assert.throws(() => journal.remember(actor, operation, `p.${owner.sourceId}.${uuid}`));
    assert.throws(() => journal.remember(actor, kind === 'Payment' ? 'evidence.supplier-document-pdf.reserve' : 'evidence.payment-pdf.reserve', key));
    assert.throws(() => journal.remember(actor, 'evidence.payment-pdf.attach', key));
    assert.deepEqual(journal.list(actor), [entry]);
  }
});

test('PDF recovery exposes only its owning lookup and page, with no legacy or close command', async () => {
  for (const kind of ['Payment', 'SupplierInvoice'] as const) {
    const {entry, journal, operation} = setup(kind);
    const adapters = adaptersFor(kind, async () => ({state: 'committed', result}));
    assert.deepEqual(operationRecoveryCapabilities(operation), {canCheck: false, canClose: false, unavailable: 'adapter'});
    assert.deepEqual(operationRecoveryCapabilities(operation, adapters), {canCheck: true, canClose: false, unavailable: null});
    assert.equal(recoveryPage(operation)?.href, kind === 'Payment' ? '/billing/payments' : '/procurement/supplier-documents');
    assert.match(operationLabel(operation), /PDF/);
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy), /recovery_adapter_unavailable/);
    await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, 'Tutup'), /recovery_close_unsupported/);
    assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'committed', result: {id: result.reservationId, version: '1'}});
    assert.deepEqual(journal.list(actor), []);
  }
});

test('missing, malformed and stale PDF receipts retain the original key without command replay', async () => {
  for (const receipt of [null, {state: 'closed', result: null}, {state: 'committed', result, extra: true},
    {state: 'committed', result: {...result, revision: '2'}}, {state: 'committed', result: {...result, reservationId: 42}},
    {state: 'committed', result: {...result, expiresAt: '2026-02-30T10:15:00Z'}}]) {
    const {entry, journal} = setup();
    const adapters = adaptersFor('Payment', async () => receipt as never);
    if (receipt === null) assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, adapters), {state: 'unconfirmed'});
    else await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters));
    assert.deepEqual(journal.list(actor), [entry]);
  }
  const {entry, journal} = setup(); let active = false, reads = 0;
  const adapters = adaptersFor('Payment', async () => {reads++; active = false; return {state: 'committed', result};});
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, () => active), /recovery_request_stale/);
  assert.equal(reads, 0); active = true;
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, adapters, undefined, () => active), /recovery_request_stale/);
  assert.equal(reads, 1); assert.deepEqual(journal.list(actor), [entry]);
});

test('lost PDF reservation reply retains the actor key for lookup-only recovery after reload', async () => {
  const {key, operation, storage, journal} = setup(); const ledger = new MutationLedger(() => key); let writes = 0, reads = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, operation, operation, {sourceId: owner.sourceId, sourceRevision: owner.sourceRevision}, async selected => {
    writes++; assert.equal(selected, key); throw new AccessFailure(503, 'commit_outcome_unknown');
  }), /commit_outcome_unknown/);
  assert.equal(ledger.isUncertain(operation), true);
  const reloaded = new OperationJournal(() => storage), [entry] = reloaded.list(actor);
  const adapters = adaptersFor('Payment', async selected => {reads++; assert.deepEqual(selected, entry); return {state: 'committed', result};});
  await reconcileOperationRecovery(entry, reloaded, legacy, adapters); assert.equal(writes, 1); assert.equal(reads, 1); assert.deepEqual(reloaded.list(actor), []);
});

test('unavailable journal storage stops PDF reservation before dispatch; cleanup failure preserves confirmed reference', async () => {
  const {key, operation, storage, entry} = setup(); let writes = 0;
  const blocked = new OperationJournal(() => {throw new Error('denied');});
  await assert.rejects(submitTrackedRequest(new MutationLedger(() => key), blocked, actor, operation, operation, {}, async () => {writes++; return result;}), /recovery_storage_unavailable/);
  assert.equal(writes, 0);
  const retained = new OperationJournal(() => ({get length() {return storage.length;}, key: storage.key.bind(storage), getItem: storage.getItem.bind(storage),
    setItem: storage.setItem.bind(storage), removeItem() {throw new Error('cleanup denied');}}));
  await assert.rejects(reconcileOperationRecovery(entry, retained, legacy, adaptersFor('Payment', async () => ({state: 'committed', result}))), /recovery_cleanup_failed/);
  assert.deepEqual(retained.list(actor), [entry]);
});
