import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient } from '../src/api/client';
import { createBankStatementGateway, parseStatementResult, prepareStatement, statementFile } from '../src/modules/billing/bank-statement-client';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';

const actorId = '9007199254740993', key = 'bs.stage.0.11111111-1111-4111-8111-111111111111';
const selection = { profileId: '910', profileRevision: '1', accountId: '41', accountVersionId: '71' };
const preview = { binding: 'a'.repeat(64), preview: { sourceSha256: 'b'.repeat(64), rows: [{ number: 2, transactionId: '0001', bookedDate: '2026-09-27', direction: 'credit' as const, amount: '1000.25', payerName: 'Synthetic buyer', reference: 'REFERENCE', identityCandidate: 'c'.repeat(64) }], issues: [], credits: '1000.25', debits: '0.00', canStage: true } };
const saved = { importId: '9007199254740994', operation: 'billing.bank-statement.stage' as const, rowCount: 1, recordedAt: '2026-09-28T00:00:00Z' };
test('statement preparation keeps source bytes and explicit account identity and rejects unreviewable imports', async () => {
  const base64 = await statementFile(new File(['original\r\n'], 'synthetic.csv'));
  assert.equal(atob(base64), 'original\r\n');
  assert.deepEqual(prepareStatement(actorId, selection, base64, 'synthetic.csv', preview, 'Approved source', true, 'Review').body.selection, selection);
  assert.throws(() => prepareStatement(actorId, selection, base64, 'synthetic.csv', { ...preview, preview: { ...preview.preview, canStage: false } }, 'source', true, 'Review'));
  assert.throws(() => prepareStatement(actorId, selection, base64, 'synthetic.csv', preview, 'source', false, 'Review'));
  assert.throws(() => prepareStatement(actorId, selection, base64, '../file.csv', preview, 'source', true, 'Review'));
  await assert.rejects(() => statementFile(new File([new Uint8Array(262145)], 'too-large.csv')));
  assert.throws(() => parseStatementResult({ ...saved, importId: Number(saved.importId) }, key));
  assert.throws(() => parseStatementResult({ ...saved, paid: true }, key));
});
test('statement gateway sends actor CSRF and create preconditions and never sends a stale command', async () => {
  let writes = 0;
  const command = prepareStatement(actorId, selection, btoa('original'), 'synthetic.csv', preview, 'Approved source', true, 'Review');
  const gateway = createBankStatementGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); const path = new URL(request.url).pathname;
    if (path === '/api/v1/identity/csrf') return Response.json({ requestToken: 'fixture-csrf' });
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'fixture-csrf'); assert.equal(request.cache, 'no-store');
    if (path.endsWith('/operations/query')) return Response.json({ status: 'committed', result: saved });
    writes++; assert.equal(path, '/api/v1/billing/bank-statements/imports'); assert.equal(request.headers.get('If-Match'), '"0"'); assert.equal(request.headers.get('Idempotency-Key'), key);
    assert.deepEqual(await request.json(), command.body); return Response.json(saved);
  } }));
  await assert.rejects(() => gateway.send(command, key, () => false)); assert.equal(writes, 0);
  assert.deepEqual(await gateway.send(command, key, () => true), saved); assert.equal(writes, 1);
  assert.deepEqual((await gateway.recover(actorId, key))?.result, saved); assert.equal(writes, 1);
});
test('statement recovery retains only a reference and cannot close an unknown import or accept a wrong operation', async () => {
  const values = new Map<string, string>(); const journal = new OperationJournal(() => ({ get length() { return values.size; }, key: index => [...values.keys()][index] ?? null,
    getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } }));
  const entry = journal.remember(actorId, saved.operation, key); assert.deepEqual(Object.keys(entry).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.equal(recoveryPage(entry.operation).href, '/billing/bank-statements'); assert.equal(operationRecoveryCapabilities(entry.operation).canClose, false);
  const legacy = { lookup: async () => { throw new Error('Wrong owner'); }, close: async () => { throw new Error('Cannot close'); } };
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.bank-statement': { lookup: async () => null } })).state, 'unconfirmed'); assert.equal(values.size, 1);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'billing.bank-statement': { lookup: async () => ({ state: 'committed', result: { ...saved, operation: 'other' as typeof saved.operation } }) } })); assert.equal(values.size, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, { 'billing.bank-statement': { lookup: async () => ({ state: 'committed', result: saved }) } })).state, 'committed'); assert.equal(values.size, 0);
});
