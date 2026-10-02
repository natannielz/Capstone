import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure, MutationLedger } from '../src/modules/administration/workflow';
import { OperationJournal, operationRecoveryCapabilities, reconcileOperationRecovery, submitTrackedRequest } from '../src/modules/administration/operation-journal';
import { recoveryPage, operationLabel } from '../src/modules/administration/operation-recovery-navigation';
import { identityDestinations, identityReturnPath, loginFields, parseIdentitySession, stepUpFields } from '../src/modules/identity/identity-workflow';
import { accountingBookInput, accountingBookKeyOwner, createAccountingBookGateway, parseAccountingBookCurrent, parseAccountingBookProposal,
  parseAccountingBookResult, parseAccountingBookTargets, prepareAccountingBook, prepareAccountingBookDecision } from '../src/modules/accounting/accounting-book-client';
import { SourceMemoryStorage, sourceSession } from './stock-source-fixtures';

const actor = '9007199254740993', proposalId = '9007199254740994';
const key = { organizationId: '100', shopId: '101', channelId: '103', currency: 'IDR' as const };
const organization = { id: '100', code: 'TOKO', name: 'Koperasi', scopeVersion: '2', profileVersion: '3' };
const shop = { ...organization, id: '101', code: 'SHOP', name: 'Toko' }, channel = { ...organization, id: '103', code: 'RETAIL', name: 'Ritel' };
const selected = { key, organization, shop, channel, configured: false };
const input = { key, initialMonth: '2026-10', mappings: [{ mappingKey: 'inventory', accountCode: 'ASTOCK' }, { mappingKey: 'payable', accountCode: 'LAP' }] };
const preview = { input, binding: 'a'.repeat(64), observedAt: '2026-10-02T01:00:00Z', organization, shop, channel };
const proposal = { id: proposalId, version: '1' as const, key, organization, shop, channel, initialMonth: input.initialMonth, mappings: input.mappings,
  proposedAt: '2026-10-02T01:01:00Z', state: 'pending' as const, decision: null };
const decisionPreview = { proposal, decision: 'approve' as const, binding: 'b'.repeat(64), observedAt: '2026-10-02T01:02:00Z' };
const proposed = { proposalId, proposalVersion: '1' as const, decisionId: null, bookId: null, chartId: null, periodId: null, status: 'pending' as const };
const approved = { ...proposed, decisionId: '9007199254740995', bookId: '9007199254740996', chartId: '9007199254740997', periodId: '9007199254740998', status: 'approved' as const };
const approvedProposal = { ...proposal, state: 'approved' as const, decision: { id: approved.decisionId, decision: 'approve' as const, decidedAt: '2026-10-02T01:03:00Z',
  bookId: approved.bookId, chartId: approved.chartId, periodId: approved.periodId, accounts: input.mappings.map((value, index) => ({ ...value, accountId: String(200 + index), active: true as const })) } };
const uuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', proposeKey = `ab.100.101.103.IDR.${uuid}`, approveKey = `abd.approve.${proposalId}.${uuid}`;
const command = () => prepareAccountingBook(actor, selected, input, preview, 'Pemetaan dan periode pertama telah ditinjau');
const decisionCommand = () => prepareAccountingBookDecision(actor, proposal, decisionPreview, 'Pemeriksaan independen buku dan akun');

test('book input requires explicit month and exact reviewed codes, preserving aliases without inferred mappings', () => {
  assert.deepEqual(accountingBookInput({ ...input, mappings: [...input.mappings].reverse() }), input);
  assert.equal(accountingBookInput({ ...input, initialMonth: '0001-01' }).initialMonth, '0001-01');
  const aliases = [{ mappingKey: 'Inventory', accountCode: 'A.stock_1-x' }, { mappingKey: 'inventory', accountCode: 'A.stock_1-x' }];
  assert.deepEqual(accountingBookInput({ ...input, mappings: aliases }).mappings, aliases);
  for (const changed of [{ ...input, initialMonth: '' }, { ...input, initialMonth: '0000-01' }, { ...input, initialMonth: '2026-13' },
    { ...input, initialMonth: '2026-1' }, { ...input, mappings: [] }, { ...input, mappings: [input.mappings[0], input.mappings[0]] },
    ...['1001', ' ASTOCK', 'ASTOCK ', 'ＡSTOCK', 'ASTOCK\n', 'A'.repeat(65)].map(accountCode => ({ ...input, mappings: [{ mappingKey: 'inventory', accountCode }] }))])
    assert.throws(() => accountingBookInput(changed), RequestNotSentError);
  assert.deepEqual(parseAccountingBookTargets({ items: [selected] }).items, [selected]);
  for (const changed of [{ ...selected, shop: { ...shop, id: '201' } }, { ...selected, organization: { ...organization, scopeVersion: 2 } },
    { ...selected, key: { ...key, currency: 'USD' } }, { ...selected, privateApproval: 'hidden' }]) assert.throws(() => parseAccountingBookTargets({ items: [changed] }));
  assert.throws(() => parseAccountingBookTargets({ items: [selected, selected] }));
});

test('review freezes the complete named book, month and mappings and rejects stale source or independent decision substitution', () => {
  const frozen = command(); assert.ok(Object.isFrozen(frozen.body.input.mappings[0])); assert.ok(Object.isFrozen(frozen.body.input.key));
  for (const changed of [{ ...preview, shop: { ...shop, name: 'Toko lain' } }, { ...preview, shop: { ...shop, scopeVersion: '3' } },
    { ...preview, channel: { ...channel, profileVersion: '4' } }, { ...preview, input: { ...input, initialMonth: '2026-11' } },
    { ...preview, input: { ...input, mappings: [{ ...input.mappings[0], accountCode: 'AOTHER' }, input.mappings[1]] } }])
    assert.throws(() => prepareAccountingBook(actor, selected, input, changed, 'Alasan'));
  assert.throws(() => prepareAccountingBook(actor, { ...selected, configured: true }, input, preview, 'Alasan'), RequestNotSentError);
  assert.throws(() => prepareAccountingBook(actor, selected, input, preview, ''), RequestNotSentError);
  assert.ok(Object.isFrozen(decisionCommand().body));
  assert.throws(() => prepareAccountingBookDecision(actor, proposal, { ...decisionPreview, proposal: { ...proposal, id: '8' } }, 'Alasan'));
  assert.throws(() => prepareAccountingBookDecision(actor, proposal, { ...decisionPreview, proposal: { ...proposal, initialMonth: '2026-11' } }, 'Alasan'));
  assert.throws(() => prepareAccountingBookDecision(actor, proposal, { ...decisionPreview, observedAt: '2026-10-02T00:00:00Z' }, 'Alasan'));
});

test('current configuration and approved account mappings must have coherent scope, decision and allocated identities', () => {
  const missing = { key, configured: false, setup: null };
  assert.deepEqual(parseAccountingBookCurrent(missing, key), missing);
  assert.deepEqual(parseAccountingBookCurrent({ key, configured: true, setup: approvedProposal }, key).setup, approvedProposal);
  assert.throws(() => parseAccountingBookCurrent({ key, configured: false, setup: proposal }, key));
  assert.throws(() => parseAccountingBookCurrent({ key, configured: true, setup: null }, key));
  assert.throws(() => parseAccountingBookCurrent(missing, { ...key, shopId: '201' }));
  for (const changed of [{ ...proposal, actorId: actor }, { ...proposal, decision: approvedProposal.decision },
    { ...approvedProposal, decision: { ...approvedProposal.decision, accounts: [] } },
    { ...approvedProposal, decision: { ...approvedProposal.decision, accounts: approvedProposal.decision.accounts.map(value => ({ ...value, accountId: '200' })) } },
    { ...approvedProposal, decision: { ...approvedProposal.decision, accounts: approvedProposal.decision.accounts.map(value => ({ ...value, accountCode: 'OTHER' })) } }])
    assert.throws(() => parseAccountingBookProposal(changed, proposalId));
  const aliases = input.mappings.map(value => ({ ...value, accountCode: 'SHARED' }));
  const aliasProposal = { ...approvedProposal, mappings: aliases, decision: { ...approvedProposal.decision, accounts: aliases.map(value => ({ ...value, accountId: '200', active: true as const })) } };
  assert.deepEqual(parseAccountingBookProposal(aliasProposal), aliasProposal);
  assert.throws(() => parseAccountingBookResult({ ...approved, proposalId: '8' }, approveKey, 'accounting.book.approve'));
  assert.throws(() => parseAccountingBookResult({ ...approved, periodId: null }, approveKey, 'accounting.book.approve'));
  assert.throws(() => accountingBookKeyOwner(approveKey, 'accounting.book.reject'));
});

test('book transport retains actor, CSRF, exact reviewed payload and immutable revision with no denied or uncertain retries', async () => {
  let calls = 0; const gateway = createAccountingBookGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf-memory' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('X-CSRF-Token'), 'csrf-memory');
    assert.equal(input.credentials, 'same-origin'); assert.equal(input.cache, 'no-store'); assert.equal(input.redirect, 'error');
    if (input.url.endsWith('/proposals')) { assert.equal(input.headers.get('Idempotency-Key'), proposeKey); assert.deepEqual(await input.json(), command().body); return Response.json(proposed); }
    assert.equal(input.headers.get('Idempotency-Key'), approveKey); assert.equal(input.headers.get('If-Match'), '"1"'); assert.deepEqual(await input.json(), decisionCommand().body);
    return calls === 3 ? Response.json({ code: 'step_up_required' }, { status: 403 }) : Response.json(approved);
  } }));
  await assert.rejects(gateway.propose(command(), proposeKey, () => false), RequestNotSentError); assert.equal(calls, 0);
  assert.deepEqual(await gateway.propose(command(), proposeKey, () => true), proposed);
  assert.deepEqual(await gateway.decide(decisionCommand(), approveKey, () => true), approved);
  await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => true), { code: 'step_up_required' }); assert.equal(calls, 3);
  let actorChecks = 0; await assert.rejects(gateway.decide(decisionCommand(), approveKey, () => ++actorChecks === 1), { code: 'commit_outcome_unknown' }); assert.equal(calls, 4);
});

test('book recovery stores only original actor references, remains lookup-only and retains unconfirmed or foreign decisions', async () => {
  const storage = new SourceMemoryStorage(), journal = new OperationJournal(() => storage), ledger = new MutationLedger(() => approveKey); let attempts = 0;
  await assert.rejects(submitTrackedRequest(ledger, journal, actor, 'book-decision', 'accounting.book.approve', decisionCommand().body, async () => { attempts++; throw new AccessFailure(503, 'commit_outcome_unknown'); }));
  const entry = journal.list(actor)[0]; assert.equal(journal.list('99').length, 0);
  const serialized = [...storage.values.values()][0]; assert.deepEqual(Object.keys(JSON.parse(serialized)).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  for (const privateValue of [decisionCommand().body.reason, decisionPreview.binding, 'ASTOCK', 'inventory']) assert.equal(serialized.includes(privateValue), false);
  const legacy = { lookup: async () => { throw new Error('No generic lookup'); }, close: async () => { throw new Error('No generic close'); } };
  assert.deepEqual(operationRecoveryCapabilities('accounting.book.approve'), { canCheck: false, canClose: false, unavailable: 'adapter' });
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'accounting.book': { lookup: async () => null } }), { state: 'unconfirmed' });
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'accounting.book': { lookup: async () => ({ state: 'committed', result: approved }) } }, 'Close'));
  await assert.rejects(reconcileOperationRecovery(entry, journal, legacy, { 'accounting.book': { lookup: async () => ({ state: 'committed', result: { ...approved, proposalId: '8' } }) } }));
  assert.equal(journal.list(actor).length, 1);
  assert.deepEqual(await reconcileOperationRecovery(entry, journal, legacy, { 'accounting.book': { lookup: async () => ({ state: 'committed', result: approved }) } }), { state: 'committed', result: { id: proposalId, version: '1' } });
  assert.equal(journal.list(actor).length, 0); assert.equal(attempts, 1);
});

test('proposal recovery checks actual book owner and exact navigation grants neither unrelated accounting actions nor return redirects', async () => {
  let mismatch = false, calls = 0; const gateway = createAccountingBookGateway(createUnitTokoClient({ baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); if (input.url.endsWith('/csrf')) return Response.json({ requestToken: 'csrf' }); calls++;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor); assert.equal(input.headers.get('Idempotency-Key'), null);
    if (input.url.endsWith('/operations/query')) { assert.deepEqual(await input.json(), { purpose: 'operation-recovery', operation: 'accounting.book.propose', key: proposeKey }); return Response.json({ status: 'confirmed', result: proposed }); }
    return Response.json(mismatch ? { ...proposal, key: { ...key, shopId: '201' }, shop: { ...shop, id: '201' } } : proposal);
  } }));
  assert.deepEqual(await gateway.recover(actor, 'accounting.book.propose', proposeKey), { state: 'committed', result: proposed }); mismatch = true;
  await assert.rejects(gateway.recover(actor, 'accounting.book.propose', proposeKey)); assert.equal(calls, 4);
  const path = '/accounting/books', permission = 'accounting.book.approve';
  assert.equal(identityReturnPath(path), path); assert.deepEqual(loginFields(path, '', ''), { returnPath: path });
  for (const action of ['read', 'propose', 'approve']) assert.equal(identityDestinations(['accounting.book.' + action]).some(item => item.href === path), true);
  assert.equal(identityDestinations(['accounting.close.approve']).some(item => item.href === path), false);
  assert.equal(recoveryPage('accounting.book.propose').href, path); assert.equal(operationLabel('accounting.book.approve'), 'Persetujuan buku akuntansi');
  const session = parseIdentitySession({ ...sourceSession, stepUpOperations: [permission] });
  assert.deepEqual(stepUpFields(session, permission, path), { expectedActorId: session.account!.userId, operation: permission, returnPath: path });
  assert.throws(() => stepUpFields({ ...session, stepUpOperations: [] }, permission, path));
  for (const invalid of [path + '/', path + '?return=https://elsewhere.test', '//accounting/books']) assert.throws(() => loginFields(invalid, '', ''));
});
