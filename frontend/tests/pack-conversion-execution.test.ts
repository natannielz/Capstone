import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RequestNotSentError } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { reconcileOperationRecovery, operationRecoveryCapabilities } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { identityReturnPath } from '../src/modules/identity/identity-workflow';
import { parsePackRule } from '../src/modules/inventory/pack-conversion-workflow';
import { stockLocationKey } from '../src/modules/inventory/stock-count-workflow';
import { createPackExecutionGateway, createConversionTransport, type PackExecutionGateway, type ConversionRequest } from '../src/modules/inventory/pack-conversion-execution-client';
import { PackExecutionEditor } from '../src/modules/inventory/pack-conversion-execution-state';
import { parseConversionInput, parseConversionSources, parseConversionPreview, prepareConversionInput, prepareConversion, conversionTransferredValue,
  conversionOperation, conversionValuePolicy, conversionOutcome, parseConversionResult, parseConversionHistory, type ConversionPreview, type ConversionResult } from '../src/modules/inventory/pack-conversion-execution-workflow';
import { actorId, location, warehouse, source, destination, rule as rawRule, session, journalFixture } from './pack-conversion-fixtures';

const rule = parsePackRule(rawRule), balanceId = '9007199254741013', lotId = '9007199254741015';
const actualSource = { sourceBalanceId: balanceId, sourceBalanceRevision: '7', ruleId: rule.id, ruleVersion: rule.version, source, destination,
  receiptReference: 'Terima-10', lotReference: 'Lot-10', lotId, expiresOn: '2027-01-31', availableSourcePackages: '2', remainingPackages: '3',
  remainingValue: '100.00', maximumSteps: '2', policy: conversionValuePolicy };
const input = prepareConversionInput(location, rule, actualSource, '1');
const stockBefore = { balanceId, poolId: '9007199254741017', lotId, onHand: '3', reserved: '1', held: '0', expired: '0', revision: '7' };
const preview = parseConversionPreview({ input, expected: { sourceBalanceRevision: '7', poolRevision: '4', poolFence: '0', lotValueRevision: '3' }, source, destination,
  stockBefore, stockAfter: { ...stockBefore, onHand: '2', revision: '8' }, remainingPackages: '3', remainingValue: '100.00', sourcePackages: '1',
  destinationPackages: '12', baseUnits: '12', policy: conversionValuePolicy, binding: 'c'.repeat(64), observedAt: '2026-10-05T01:00:00Z' }, input, rule, actualSource);
const result = parseConversionResult({ conversionId: '9007199254741019', revision: '1', status: 'converted', sourcePackages: '1', destinationPackages: '12', baseUnits: '12',
  destinationBalanceId: '9007199254741021', destinationLotId: '9007199254741023', movementId: '9007199254741025', valueTransitionId: '9007199254741027', transferredValue: '33.33' });
const detail = { id: result.conversionId, revision: '1', status: 'converted', input, source, destination, receiptReference: actualSource.receiptReference,
  sourceLotReference: actualSource.lotReference, stockBefore, stockAfter: preview.stockAfter,
  destinationAfter: { ...stockBefore, balanceId: result.destinationBalanceId, lotId: result.destinationLotId, onHand: '12', reserved: '0', revision: '2' },
  sourcePackages: '1', destinationPackages: '12', baseUnits: '12', policy: conversionValuePolicy, transferredValue: '33.33', recordedAt: '2026-10-05T01:00:01Z', reason: 'Buka satu dus' };
const legacy = { lookup: async () => { assert.fail('Execution recovery must use owning endpoint'); }, close: async () => { assert.fail('Execution recovery cannot close or retry'); } };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
function setup(overrides: Partial<PackExecutionGateway> = {}) {
  const { journal, values } = journalFixture(); let now = 0, keys = 0;
  const gateway: PackExecutionGateway = { warehouses: async () => [warehouse], rules: async () => ({ items: [rule], next: null }),
    sources: async () => ({ items: [actualSource], unavailableCount: '0', next: null }), preview: async () => preview, history: async () => ({ items: [], next: null }),
    detail: async () => parseConversionHistory({ items: [detail], nextConversionId: null }, location).items[0], submit: async () => result, receipt: async () => null, ...overrides };
  const editor = new PackExecutionEditor({ actorId, actions: ['stock.convert'], session, observedAt: 0, now: () => now,
    gateway, journal, newKey: () => 'physical-' + ++keys });
  return { editor, gateway, journal, values, advance: (value: number) => { now = value; } };
}
async function select(editor: PackExecutionEditor) {
  await editor.start(); await editor.chooseWarehouse(stockLocationKey(location)); await editor.chooseRule(rule.id); editor.chooseSource(balanceId);
  editor.change('steps', '1'); editor.change('reason', 'Buka satu dus');
}
async function review(editor: PackExecutionEditor) { await select(editor); await editor.preview(); editor.review(); }

test('source selection is bound to approved rule, exact packages and real lot availability including reservations', () => {
  assert.deepEqual(parseConversionSources({ items: [actualSource], unavailableCount: '0', nextSourceBalanceId: null }, rule).items, [actualSource]);
  for (const changed of [{ ruleId: '2' }, { maximumSteps: '3' }, { availableSourcePackages: '4' }, { expiresOn: '2027-02-29' },
    { remainingValue: '100' }, { remainingValue: '0100.00' }, { source: { ...source, definitionId: '2' } }, { sourceBalanceRevision: 7 }, { privateProof: 'hidden' }])
    assert.throws(() => parseConversionSources({ items: [{ ...actualSource, ...changed }], unavailableCount: '0', nextSourceBalanceId: null }, rule));
  for (const steps of ['', '0', '01', '1.0', '3', '1000001', '1\n']) assert.throws(() => prepareConversionInput(location, rule, actualSource, steps));
  assert.throws(() => prepareConversionInput({ ...location, warehouseId: '2' }, rule, actualSource, '1'));
  assert.throws(() => parseConversionInput({ ...input, steps: '1' }));
});
test('preview pins source revision above1 and zero pool fence; quantities and stock transition conserve exact base units', () => {
  assert.equal(preview.expected.sourceBalanceRevision, '7'); assert.equal(preview.expected.poolFence, '0'); assert.equal(preview.stockAfter.reserved, '1');
  for (const changed of [{ expected: { ...preview.expected, poolFence: 0 } }, { expected: { ...preview.expected, sourceBalanceRevision: '1' } },
    { baseUnits: '11' }, { destinationPackages: '11' }, { stockAfter: { ...preview.stockAfter, reserved: '0' } },
    { stockAfter: { ...preview.stockAfter, revision: '9' } }, { binding: 'c'.repeat(64) + '\n' }, { input: { ...input, sourceBalanceId: '2' } }])
    assert.throws(() => parseConversionPreview({ ...preview, ...changed }, input, rule, actualSource));
  const command = prepareConversion(actorId, preview, 'Buka satu dus'); assert.ok(Object.isFrozen(command.body.expected));
  assert.deepEqual(conversionOutcome(result, command), result); assert.throws(() => conversionOutcome({ ...result, transferredValue: '33.34' }, command));
});
test('value allocation floors exact minor units and consumes final residual without floating point', () => {
  assert.equal(conversionTransferredValue(preview), '33.33');
  assert.equal(conversionTransferredValue({ ...preview, remainingValue: '0.02' }), '0.00');
  assert.equal(conversionTransferredValue({ ...preview, sourcePackages: '3', remainingValue: '100.00' }), '100.00');
  assert.equal(conversionTransferredValue({ ...preview, remainingValue: '999999999999999.99' }), '333333333333333.33');
});
test('real history rejects scope drift, malformed or mismatched balances and duplicate cursor rows', () => {
  const history = parseConversionHistory({ items: [detail], nextConversionId: null }, location);
  assert.equal(history.items[0].id, result.conversionId);
  // The destination starts empty at revision1; the genuine ConvertIn effect advances it to2.
  assert.equal(history.items[0].destinationAfter.revision, '2');
  for (const revision of ['1', '3'])
    assert.throws(() => parseConversionHistory({ items: [{ ...detail, destinationAfter: { ...detail.destinationAfter, revision } }], nextConversionId: null }, location));
  assert.throws(() => parseConversionHistory({ items: [detail], nextConversionId: null }, { ...location, channelId: '2' }));
  assert.throws(() => parseConversionHistory({ items: [detail, detail], nextConversionId: null }, location));
  assert.throws(() => parseConversionHistory({ items: [{ ...detail, destinationAfter: { ...detail.destinationAfter, onHand: '11' } }], nextConversionId: null }, location));
});
test('dedicated transport sends cookie/CSRF, original key and exact quoted released source revision', async () => {
  const sent: Request[] = []; const transport = createConversionTransport({ origin: 'https://unit.test', fetch: async request => {
    const r = request as Request; sent.push(r);
    return Response.json(new URL(r.url).pathname.endsWith('/csrf') ? { requestToken: 'fresh-token' } : result);
  } });
  const gateway = createPackExecutionGateway(transport), command = prepareConversion(actorId, preview, 'Buka satu dus');
  await gateway.submit(command, 'original-key', () => true);
  const request = sent[1], body = await request.json();
  assert.equal(sent.length, 2); assert.equal(new URL(request.url).pathname, '/api/v1/inventory/pack-conversions/executions');
  assert.equal(request.headers.get('X-CSRF-Token'), 'fresh-token'); assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId);
  assert.equal(request.headers.get('Idempotency-Key'), 'original-key'); assert.equal(request.headers.get('If-Match'), '"7"');
  assert.deepEqual(body, command.body);
  assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');
});
test('actor/view change during CSRF prevents dispatch; late mutation result remains uncertain', async () => {
  let current = true, business = 0;
  const transport = createConversionTransport({ origin: 'https://unit.test', fetch: async request => {
    if (new URL((request as Request).url).pathname.endsWith('/csrf')) { current = false; return Response.json({ requestToken: 'token' }); }
    business++; return Response.json(result);
  } });
  await assert.rejects(createPackExecutionGateway(transport).submit(prepareConversion(actorId, preview, 'Buka satu dus'), 'key', () => current), RequestNotSentError);
  assert.equal(business, 0);
  current = true;
  const late = createPackExecutionGateway({ send: async () => { current = false; return result; } });
  await assert.rejects(late.submit(prepareConversion(actorId, preview, 'Buka satu dus'), 'key', () => current), error => error instanceof AccessFailure && error.code === 'commit_outcome_unknown');
});
test('uncertain write is never automatically retried, changed intent cannot reuse its original key', async () => {
  const sent: ConversionRequest[] = [], gateway = createPackExecutionGateway({ send: async request => { sent.push(request); throw new TypeError('Lost response'); } });
  const command = prepareConversion(actorId, preview, 'Buka satu dus'); await assert.rejects(gateway.submit(command, 'original-key', () => true));
  assert.equal(sent.length, 1);
  await assert.rejects(gateway.submit(prepareConversion(actorId, preview, 'Alasan berubah'), 'original-key', () => true), RequestNotSentError);
  assert.equal(sent.length, 1);
});
test('wizard requires explicit warehouse/rule/lot choices; changing intent discards preview and late responses', async () => {
  const pending = deferred<ConversionPreview>(), data = setup({ preview: async () => pending.promise }); await select(data.editor);
  assert.equal(data.editor.snapshot().preview, null); const loading = data.editor.preview(); data.editor.change('steps', '2');
  pending.resolve(preview); await loading; assert.equal(data.editor.snapshot().preview, null); assert.equal(data.editor.snapshot().loadingPreview, false);
  data.editor.chooseSource('2'); assert.equal(data.editor.snapshot().source?.sourceBalanceId, balanceId);
  data.editor.chooseSource(''); assert.equal(data.editor.snapshot().source, null);
  assert.throws(() => new PackExecutionEditor({ actorId, actions: ['stock.count'], session, observedAt: 0, now: () => 0, gateway: data.gateway, journal: data.journal }));
});
test('explicit confirmation freezes intent, duplicate clicks send once, and result requires real history reload', async () => {
  const pending = deferred<ConversionResult>(), sent: { key: string; reason: string }[] = [];
  const data = setup({ submit: async (command, key) => { sent.push({ key, reason: command.body.reason }); return pending.promise; } }); await review(data.editor);
  data.editor.change('reason', 'Changed after review'); assert.equal(data.editor.snapshot().review?.body.reason, 'Buka satu dus');
  await data.editor.execute(false); assert.equal(sent.length, 0);
  const first = data.editor.execute(true); await data.editor.execute(true); assert.deepEqual(sent, [{ key: 'physical-1', reason: 'Buka satu dus' }]);
  pending.resolve(result); await first; assert.deepEqual(data.editor.snapshot().result, result); assert.equal(data.editor.snapshot().source, null);
  assert.equal(data.editor.snapshot().preview, null); assert.equal(data.journal.list(actorId).length, 0);
});
test('lost response journals only metadata; reload and owning recovery keep original actor/key without writes', async () => {
  let writes = 0; const data = setup({ submit: async () => { writes++; throw new TypeError('Lost response'); } }); await review(data.editor); await data.editor.execute(true);
  const [entry] = data.journal.list(actorId); assert.equal(entry.key, 'physical-1'); assert.equal(writes, 1); assert.equal(data.editor.locked, true);
  assert.deepEqual(Object.keys(JSON.parse([...data.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.ok(![...data.values.values()][0].includes(preview.binding)); await data.editor.execute(true); assert.equal(writes, 1);
  const sent: ConversionRequest[] = [], gateway = createPackExecutionGateway({ send: async request => { sent.push(request); return { state: 'committed', result }; } });
  await assert.rejects(gateway.receipt(entry, '2'), /recovery_request_stale/); assert.equal(sent.length, 0);
  const recovered = await reconcileOperationRecovery(entry, data.journal, legacy, { [conversionOperation]: { lookup: ref => gateway.receipt(ref, actorId) } });
  assert.deepEqual(recovered, { state: 'committed', result: { id: result.conversionId, version: '1' } });
  assert.equal(sent[0].path, '/operations/query'); assert.deepEqual(sent[0].body, { purpose: 'operation-recovery', operation: conversionOperation, key: 'physical-1' });
  assert.equal(sent[0].actorId, actorId); assert.equal(data.journal.list(actorId).length, 0); assert.equal(writes, 1);
});
test('unconfirmed and stale recovery retain the reference; expired session conceals source and preview', async () => {
  const data = setup(); const entry = data.journal.remember(actorId, conversionOperation, 'pending'); await data.editor.start(); assert.equal(data.editor.snapshot().warehouses.length, 0);
  const reply = await reconcileOperationRecovery(entry, data.journal, legacy, { [conversionOperation]: { lookup: async () => null } });
  assert.equal(reply.state, 'unconfirmed'); assert.equal(data.journal.list(actorId).length, 1);
  let current = true;
  await assert.rejects(reconcileOperationRecovery(entry, data.journal, legacy, { [conversionOperation]: { lookup: async () => { current = false; return { state: 'committed', result }; } } }, undefined, () => current), /recovery_request_stale/);
  assert.equal(data.journal.list(actorId).length, 1);
  data.journal.confirmed(entry); data.editor.reconciled(); await new Promise(resolve => setImmediate(resolve)); await select(data.editor); await data.editor.preview();
  data.advance(600001); data.editor.checkSession(); assert.equal(data.editor.snapshot().expired, true); assert.equal(data.editor.snapshot().source, null); assert.equal(data.editor.snapshot().preview, null);
});
test('reload recovery restores warehouse choices only after busy completes and preserves owning result', async () => {
  let loads = 0;
  const data = setup({ warehouses: async () => { loads++; return [warehouse]; }, receipt: async () => ({ state: 'committed', result }) });
  const entry = data.journal.remember(actorId, conversionOperation, 'reloaded-original'); await data.editor.start(); assert.equal(loads, 0);
  data.editor.setRecovering(true); await data.editor.receipt(entry); data.journal.confirmed(entry); data.editor.reconciled(); assert.equal(loads, 0);
  data.editor.setRecovering(false); await new Promise(resolve => setImmediate(resolve));
  assert.equal(loads, 1); assert.deepEqual(data.editor.snapshot().warehouses, [warehouse]); assert.deepEqual(data.editor.snapshot().result, result);
  assert.equal(data.editor.snapshot().warehouse, null);
});
test('execution navigation and recovery own the physical journey while rule approval remains separate', () => {
  assert.equal(identityReturnPath('/inventory/pack-conversion-executions'), '/inventory/pack-conversion-executions');
  assert.equal(recoveryPage(conversionOperation).href, '/inventory/pack-conversion-executions');
  assert.equal(operationRecoveryCapabilities(conversionOperation).canCheck, false);
  assert.deepEqual(operationRecoveryCapabilities(conversionOperation, { [conversionOperation]: { lookup: async () => null } }), { canCheck: true, canClose: false, unavailable: null });
});
