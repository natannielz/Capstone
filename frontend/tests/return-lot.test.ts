import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage } from '../src/modules/administration/operation-recovery-navigation';
import { parseCaseView } from '../src/modules/fulfillment/case-client';
import { parseInspectionView } from '../src/modules/fulfillment/inspection-client';
import { createReturnLotGateway, createReturnLotTransport, parseReturnLotResult, parseReturnLotView, prepareReturnLot, returnLotOperation, returnLotPurpose } from '../src/modules/fulfillment/return-lot-client';

// Isolated contract/recovery fixtures; not operational or migration acceptance.
const actorId = '9007199254740993', invoiceId = '9007199254740994', caseId = '9223372036854775807', returnId = '9007199254740996';
const receiptId = '9007199254740997', skuId = '9007199254740998', originalLotId = '9007199254740999';
const key = `cl.${caseId}.${returnId}.original`;
const reported = [{ receiptId, skuId, reportedBaseUnits: '25' }];
const owner = parseCaseView({ id: caseId, revision: '2', requestedAt: '2026-10-01T03:01:00Z', state: 'return_authorized', issue: 'damaged', reportedProblem: 'Kemasan tiba rusak',
  source: { invoiceId, binding: 'a'.repeat(64), number: '100/103/2026/1', buyerName: 'Cabang Tebet', sellerName: 'Omi', issuedAt: '2026-10-01T03:00:00Z',
    lines: [{ orderId: '91', shipmentId: '92', receiptId, skuId, skuCode: 'ATK01', productName: 'Kertas A4', packageLabel: 'Karton', baseUnitsPerPackage: 12, baseUnitCode: 'rim', acceptedBaseUnits: '48' }] },
  lines: reported, decision: { caseId, invoiceId, revision: '2', binding: 'b'.repeat(64), state: 'return_authorized', decidedAt: '2026-10-01T03:02:00Z', publicExplanation: 'Kirim untuk pemeriksaan', lines: reported } });
const inspection = parseInspectionView({ caseId, invoiceId, revision: '1', binding: 'c'.repeat(64),
  receipt: { id: returnId, revision: '1', receivedAt: '2026-10-01T03:03:00Z', publicNote: 'Barang diterima untuk pemeriksaan', lines: [{ receiptId, skuId, receivedBaseUnits: '25', observedCondition: 'uninspected' }] },
  inspection: { inspectedAt: '2026-10-01T03:04:00Z', publicNote: 'Identitas produk sesuai, segel kemasan utuh', lines: [{ receiptId, skuId, inspectedBaseUnits: '25', identity: 'matched', condition: 'sealed' }] } }, owner, returnId);
const preview = { caseId, returnId, revision: '0', binding: 'd'.repeat(64), state: 'belum_diverifikasi', lines: [{ receiptId, skuId, receivedBaseUnits: '25', verifiedBaseUnits: '0', manualBaseUnits: '25',
  state: 'menunggu_verifikasi_fisik', explanation: 'Pilih lot asli setelah label fisik dicocokkan. Barang tetap dalam karantina.', choices: [{ originalLotId, poolId: '121', definitionId: '122', packageLabel: 'Karton', baseUnitCode: 'rim', baseUnitsPerPackage: 12, dispatchedBaseUnits: '24' }] }] };
const savedView = { ...preview, revision: '1', state: 'lot_asli_diverifikasi_tetap_dikarantina', lines: [{ ...preview.lines[0], verifiedBaseUnits: '24', manualBaseUnits: '1', state: 'tetap_dikarantina', choices: [] }] };
const result = { returnId, caseId, invoiceId, revision: '1', verifiedAt: '2026-10-01T03:05:00Z', state: 'verified_original_lot_quarantined', operation: returnLotOperation } as const;
const prepare = (packages = '2', lotId = originalLotId) => prepareReturnLot(actorId, owner, inspection, parseReturnLotView(preview, owner, inspection),
  { receiptId, skuId, originalLotId: lotId, packages }, 'LOT-FISIK-PRIVAT-000012', 'Catatan internal pencocokan fisik');

test('return lot manual finding uses genuine selected original lot and exact whole packages; leaves loose remainder unresolved', () => {
  const command = prepare();
  assert.equal(command.actorId, actorId); assert.equal(command.caseId, caseId); assert.equal(command.body.lines[0].originalLotId, originalLotId);
  assert.equal(command.body.lines[0].verifiedBaseUnits, '24'); assert.equal(command.body.confirmPhysicalOriginalLotMatch, true);
  assert.ok(Object.isFrozen(command)); assert.ok(Object.isFrozen(command.body)); assert.ok(Object.isFrozen(command.body.lines)); assert.ok(Object.isFrozen(command.body.lines[0]));
  assert.deepEqual(parseReturnLotView(savedView, owner, inspection), savedView);
  for (const packages of ['0', '01', '1.5', '-1', '3', '1000001', '9223372036854775807']) assert.throws(() => prepare(packages));
  assert.throws(() => prepare('1', '123')); // Never substitute a plausible but undispatched original lot.
  const receivedBound = { ...inspection, receipt: { ...inspection.receipt, lines: [{ ...inspection.receipt.lines[0], receivedBaseUnits: '13' }] },
    inspection: { ...inspection.inspection!, lines: [{ ...inspection.inspection!.lines[0], inspectedBaseUnits: '13' }] } };
  const receivedPreview = { ...preview, lines: [{ ...preview.lines[0], receivedBaseUnits: '13', manualBaseUnits: '13' }] };
  assert.throws(() => prepareReturnLot(actorId, owner, receivedBound, parseReturnLotView(receivedPreview, owner, receivedBound), { receiptId, skuId, originalLotId, packages: '2' }, 'LOT-12', 'Periksa fisik'));
  const dispatchedBound = { ...preview, lines: [{ ...preview.lines[0], choices: [{ ...preview.lines[0].choices[0], dispatchedBaseUnits: '12' }] }] };
  assert.throws(() => prepareReturnLot(actorId, owner, inspection, parseReturnLotView(dispatchedBound, owner, inspection), { receiptId, skuId, originalLotId, packages: '2' }, 'LOT-12', 'Periksa fisik'));
  assert.throws(() => prepareReturnLot(actorId, owner, inspection, parseReturnLotView(savedView, owner, inspection), { receiptId, skuId, originalLotId, packages: '1' }, 'LOT-12', 'Periksa fisik'));
});

test('public return lot view binds exact intake and packaging, rejects private or financial fields and preserves manual cases', () => {
  for (const changed of [
    { ...preview, caseId: '1' }, { ...preview, returnId: '1' }, { ...preview, released: true }, { ...preview, observedLotLabel: 'Privat' },
    { ...preview, lines: [...preview.lines, ...preview.lines] }, { ...preview, lines: [{ ...preview.lines[0], receivedBaseUnits: '24' }] },
    { ...preview, lines: [{ ...preview.lines[0], verifiedBaseUnits: '12' }] }, { ...preview, lines: [{ ...preview.lines[0], manualBaseUnits: '24' }] },
    { ...preview, lines: [{ ...preview.lines[0], physicalDestinationPoolId: '123' }] }, { ...preview, refundAmount: '1' },
    { ...preview, lines: [{ ...preview.lines[0], choices: [...preview.lines[0].choices, ...preview.lines[0].choices] }] },
    { ...preview, lines: [{ ...preview.lines[0], choices: [{ ...preview.lines[0].choices[0], baseUnitsPerPackage: 6 }] }] },
    { ...preview, lines: [{ ...preview.lines[0], choices: [{ ...preview.lines[0].choices[0], packageLabel: 'Satuan' }] }] },
    { ...preview, lines: [{ ...preview.lines[0], choices: [{ ...preview.lines[0].choices[0], dispatchedBaseUnits: '25' }] }] },
    { ...savedView, lines: [{ ...savedView.lines[0], choices: preview.lines[0].choices }] },
    { ...savedView, lines: [{ ...savedView.lines[0], verifiedBaseUnits: '13', manualBaseUnits: '12' }] }
  ]) assert.throws(() => parseReturnLotView(changed, owner, inspection));
  for (const assessment of [{ identity: 'matched' as const, condition: 'opened' as const }, { identity: 'different' as const, condition: 'sealed' as const }]) {
    const assessed = { ...inspection, inspection: { ...inspection.inspection!, lines: [{ ...inspection.inspection!.lines[0], ...assessment }] } };
    assert.throws(() => parseReturnLotView(preview, owner, assessed));
    const manual = { ...preview, lines: [{ ...preview.lines[0], state: 'penanganan_manual', choices: [] }] };
    const read = parseReturnLotView(manual, owner, assessed); assert.equal(read.lines[0].manualBaseUnits, '25');
    assert.throws(() => prepareReturnLot(actorId, owner, assessed, read, { receiptId, skuId, originalLotId, packages: '1' }, 'LOT-12', 'Periksa fisik'));
  }
  const loose = { ...inspection, receipt: { ...inspection.receipt, lines: [{ ...inspection.receipt.lines[0], receivedBaseUnits: '1' }] },
    inspection: { ...inspection.inspection!, lines: [{ ...inspection.inspection!.lines[0], inspectedBaseUnits: '1' }] } };
  assert.throws(() => parseReturnLotView({ ...preview, lines: [{ ...preview.lines[0], receivedBaseUnits: '1', manualBaseUnits: '1' }] }, owner, loose));
  for (const changed of [{ ...result, returnId: '1' }, { ...result, caseId: '1' }, { ...result, operation: 'fulfillment.case.return_inspect' }, { ...result, state: 'available' }, { ...result, physicalDestinationPoolId: '1' }])
    assert.throws(() => parseReturnLotResult(changed, key));
});

test('gateway sends same-origin CSRF requests with exact current actor, original key and immutable case/intake preconditions', async () => {
  const requests: Request[] = [];
  const gateway = createReturnLotGateway(createReturnLotTransport({ origin: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'fresh-token' });
    requests.push(input); assert.equal(input.method, 'POST'); assert.equal(input.headers.get('X-CSRF-Token'), 'fresh-token'); assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId);
    assert.ok(input.url.startsWith(`https://unit-toko.test/api/v1/fulfillment/cases/${caseId}/returns/${returnId}/lot-verification`));
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.clone().json(), { purpose: 'operation-recovery', key }); return Response.json({ status: 'committed', result }); }
    if (input.url.endsWith('/query')) { assert.equal(input.headers.get('If-Match'), '"2"'); assert.deepEqual(await input.clone().json(), { purpose: returnLotPurpose }); return Response.json(preview); }
    assert.equal(input.headers.get('If-Match'), '"0"'); assert.equal(input.headers.get('Idempotency-Key'), key); return Response.json(result);
  } }));
  assert.deepEqual(await gateway.read(actorId, owner, inspection, () => true), preview);
  const command = prepare(); assert.deepEqual(await gateway.verify(command, key, () => true), result); assert.deepEqual(await requests[1].clone().json(), command.body);
  assert.deepEqual(await gateway.recover(actorId, key, () => true), { state: 'committed', result }); assert.equal(requests.length, 3);
  await assert.rejects(() => gateway.verify(command, `cl.1.${returnId}.other`, () => true)); assert.equal(requests.length, 3);
  await assert.rejects(() => gateway.recover(actorId, `cl.${caseId}.9223372036854775808.other`, () => true)); assert.equal(requests.length, 3);
  await assert.rejects(() => gateway.verify({ ...command, body: { ...command.body, confirmPhysicalOriginalLotMatch: false } } as never, key, () => true)); assert.equal(requests.length, 3);
  await assert.rejects(() => gateway.verify({ ...command, body: { ...command.body, physicalDestinationPoolId: '1' } } as never, key, () => true)); assert.equal(requests.length, 3);
});

test('stale session or expired step-up before dispatch and during CSRF sends no write; late committed response remains uncertain', async () => {
  let current = false, csrf = 0, writes = 0;
  const gateway = createReturnLotGateway(createReturnLotTransport({ origin: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/identity/csrf')) { csrf++; current = false; return Response.json({ requestToken: 'fresh-token' }); }
    writes++; return Response.json(result);
  } }));
  await assert.rejects(() => gateway.verify(prepare(), key, () => current), error => error instanceof RequestNotSentError); assert.equal(csrf, 0);
  current = true; await assert.rejects(() => gateway.verify(prepare(), key, () => current), error => error instanceof RequestNotSentError); assert.equal(csrf, 1); assert.equal(writes, 0);
  const signal = AbortSignal.abort(); current = true; await assert.rejects(() => gateway.verify(prepare(), key, () => current, signal)); assert.equal(csrf, 1);
  const late = createReturnLotGateway(createReturnLotTransport({ origin: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'fresh-token' });
    writes++; current = false; return Response.json(result);
  } }));
  const storage = memory(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => key);
  current = true; await assert.rejects(() => submitTrackedRequest(ledger, journal, actorId, returnLotOperation, returnLotOperation, prepare(),
    originalKey => late.verify(prepare(), originalKey, () => current)), error => error instanceof AccessFailure && error.code === 'commit_outcome_unknown');
  assert.equal(writes, 1); assert.equal(ledger.isUncertain(returnLotOperation), true); assert.equal(journal.list(actorId)[0].key, key);
});

test('contradictory recovery, foreign original owner, incorrect invoice or private leaked response cannot confirm an operation', async () => {
  let response: unknown = { status: 'not_confirmed', result: null };
  const gateway = createReturnLotGateway({ send: async () => Response.json(response) });
  assert.equal(await gateway.recover(actorId, key, () => true), null);
  for (const changed of [{ status: 'not_confirmed', result }, { status: 'committed', result: null },
    { status: 'committed', result: { ...result, returnId: '1' } }, { status: 'committed', result: { ...result, operation: 'fulfillment.case.return_receive' } },
    { status: 'committed', result: { ...result, observedLotLabel: 'Privat' } }]) {
    response = changed; await assert.rejects(() => gateway.recover(actorId, key, () => true));
  }
  response = { ...result, invoiceId: '1' }; await assert.rejects(() => gateway.verify(prepare(), key, () => true));
  const stale = createReturnLotGateway({ send: async () => { current = false; return Response.json({ status: 'committed', result }); } }); let current = true;
  await assert.rejects(() => stale.recover(actorId, key, () => current), error => error instanceof AccessFailure && error.code === 'return_lot_view_changed');
});

function memory() {
  const values = new Map<string, string>();
  return { values, get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
}
test('lookup-only return-lot journal stores metadata, preserves failed confirmation and foreign actor entries; never falls back or closes', async () => {
  const storage = memory(), journal = new OperationJournal(() => storage), entry = journal.remember(actorId, returnLotOperation, key);
  const other = journal.remember('17', returnLotOperation, `cl.${caseId}.${returnId}.other-actor`);
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.doesNotMatch([...storage.values.values()].join(''), /LOT-FISIK|observedLotLabel|reason|verifiedBaseUnits|bank|refund/i);
  const legacy = { lookup: async () => { throw new Error('legacy fallback forbidden'); }, close: async () => { throw new Error('manual close forbidden'); } };
  assert.equal(recoveryPage(returnLotOperation)?.href, '/fulfillment/cases');
  const absent = { 'fulfillment.case': { lookup: async () => null } }; assert.equal(operationRecoveryCapabilities(returnLotOperation, absent).canClose, false);
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy));
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, absent, 'close')); assert.equal(journal.list(actorId).length, 1);
  assert.equal((await reconcileOperationRecovery(entry, journal, legacy, absent)).state, 'unconfirmed');
  await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result: { ...result, operation: 'fulfillment.case.return_receive' } as never }) } }));
  let current = true; await assert.rejects(() => reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => { current = false; return { state: 'committed', result }; } } }, undefined, () => current));
  assert.equal(journal.list(actorId).length, 1); assert.equal(journal.list(other.actorId).length, 1);
  const confirmed = await reconcileOperationRecovery(entry, journal, legacy, { 'fulfillment.case': { lookup: async () => ({ state: 'committed', result }) } });
  assert.deepEqual(confirmed, { state: 'committed', result: { id: returnId, version: '1' } }); assert.equal(journal.list(actorId).length, 0); assert.equal(journal.list(other.actorId).length, 1);
});
