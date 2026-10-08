import {test} from 'node:test';
import assert from 'node:assert/strict';
import {RequestNotSentError} from '../src/api/client';
import {AccessFailure} from '../src/modules/administration/workflow';
import {canonicalIdentifier, createCatalogIdentifierGateway, identifierCommand, identifierNamespace, identifierReference,
  IdentifierJournal, parseIdentifierResult, parseIdentifierView, type IdentifierReference} from '../src/modules/catalog/catalog-identifier-client';
import {parseCatalogSku, ownerKey} from '../src/modules/catalog/catalog-workflow';
import {actorId, sku as rawSku} from './catalog-fixtures';

const sku = parseCatalogSku(rawSku), now = '2026-10-08T01:00:00Z';
const primary = {id: '101', organizationId: sku.organizationId, skuId: sku.id, kind: 'skuCode', namespace: 'internal', rawValue: sku.code,
  canonicalValue: sku.code, isPrimary: true, effectiveAt: now, retiredAt: null, revision: '1', provenanceReference: 'Existing catalog source', definitionId: null};
const alias = {...primary, id: '102', rawValue: 'PENA-LAMA', canonicalValue: 'PENA-LAMA', isPrimary: false, definitionId: sku.currentDefinitionId};
const source = {catalog: rawSku, identifiers: [primary, alias]}, view = parseIdentifierView(source, sku.id, ownerKey(sku.owner));
const details = {provenanceReference: 'Checked supplier packaging document', reason: 'Staff reviewed this exact package'};
const command = identifierCommand(view, 'assign', {...details, kind: 'gtin', rawValue: '00012345600012'});
const ref = identifierReference(actorId, command, 'original-key', now);
const result = {skuId: sku.id, skuRevision: String(BigInt(sku.version) + BigInt(1)), identifierId: '103', identifierRevision: '1', recordedAt: now};
class MemoryStorage {
  readonly values = new Map<string, string>();
  get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
function transport(handler: (request: Request) => Promise<Response>) {
  const calls: Request[] = [];
  const fetcher: typeof fetch = async (input, options) => {
    const request = input instanceof Request ? input : new Request(new URL(String(input), 'https://unit-toko.test'), options);
    if (request.url.endsWith('/identity/csrf')) return Response.json({requestToken: 'memory-only-csrf'});
    calls.push(request); assert.equal(request.credentials, 'same-origin'); assert.equal(request.cache, 'no-store'); assert.equal(request.redirect, 'error');
    assert.equal(request.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(request.headers.get('X-CSRF-Token'), 'memory-only-csrf');
    return handler(request);
  };
  return {calls, gateway: createCatalogIdentifierGateway(fetcher, 'https://unit-toko.test/api/v1/identity/csrf')};
}
const context = {actorId, isCurrent: () => true};

test('supplied GTIN bytes preserve leading zeros and checksum validity does not fabricate a code', () => {
  assert.equal(canonicalIdentifier('gtin', '00012345600012'), '00012345600012');
  assert.equal(canonicalIdentifier('gtin', '96385074'), '00000096385074');
  assert.equal(canonicalIdentifier('skuCode', 'pena-12'), 'PENA-12');
  for (const raw of ['00012345600013', '00012345600012\n', ' 96385074', '９６３８５０７４', '', '9638507']) assert.throws(() => canonicalIdentifier('gtin', raw), RequestNotSentError);
  assert.throws(() => canonicalIdentifier('skuCode', 'K-12'), RequestNotSentError);
});
test('identifier reads bind actual catalog owner, SKU, primary code and GTIN namespace', () => {
  const gtin = {...alias, id: '103', kind: 'gtin', namespace: identifierNamespace(sku, 'gtin'), rawValue: '96385074', canonicalValue: '00000096385074'};
  assert.equal(parseIdentifierView({...source, identifiers: [primary, gtin]}, sku.id).identifiers[1].rawValue, '96385074');
  for (const identifiers of [[primary, {...gtin, namespace: 'catalog:other'}], [primary, {...alias, skuId: '99'}], [primary, {...alias, isPrimary: true}],
    [primary, {...gtin, canonicalValue: '96385074'}], [{...primary, canonicalValue: 'WRONG'}], [primary, primary], [{...primary, revision: 1}]])
    assert.throws(() => parseIdentifierView({...source, identifiers}, sku.id), AccessFailure);
  assert.throws(() => parseIdentifierView(source, sku.id, 'different-owner'), AccessFailure);
  const corrected = parseIdentifierView({...source, identifiers: [{...primary, isPrimary: false, revision: '2'},
    {...alias, id: '103', rawValue: 'PENA-BARU', canonicalValue: 'PENA-BARU', isPrimary: true}]}, sku.id);
  assert.equal(corrected.catalog.code, sku.code);
  assert.equal(corrected.identifiers.find(row => row.isPrimary)?.canonicalValue, 'PENA-BARU');
  assert.throws(() => parseIdentifierView({...source, identifiers: [{...alias, isPrimary: true}]}, sku.id), AccessFailure);
});
test('preview requires provenance and exact package and never offers primary retirement', () => {
  assert.equal(command.operation, 'catalog.identifier.assign');
  if (command.operation === 'catalog.identifier.assign') {assert.equal(command.definitionId, sku.currentDefinitionId); assert.equal(command.rawValue, '00012345600012');}
  assert.throws(() => identifierCommand(view, 'retire', {...details, identifierId: primary.id}), RequestNotSentError);
  assert.throws(() => identifierCommand(view, 'correct', {...details, identifierId: alias.id, rawValue: 'NEW'}), RequestNotSentError);
  assert.throws(() => identifierCommand(view, 'correct', {...details, identifierId: primary.id, rawValue: sku.code}), RequestNotSentError);
  assert.throws(() => identifierCommand(view, 'assign', {...details, kind: 'gtin', rawValue: '96385074', provenanceReference: ''}), RequestNotSentError);
});
test('all three commands send exact approved body and original key once with current SKU revision', async () => {
  const commands = [command, identifierCommand(view, 'retire', {...details, identifierId: alias.id}),
    identifierCommand(view, 'correct', {...details, identifierId: primary.id, rawValue: 'PENA-BARU'})];
  for (const attempt of commands) {
    const original = identifierReference(actorId, attempt, 'original-' + attempt.operation, now);
    const expected = attempt.operation === 'catalog.identifier.retire' ? {...result, identifierId: alias.id, identifierRevision: '2'} : result;
    const {calls, gateway} = transport(async request => {
      assert.equal(request.headers.get('If-Match'), '"' + sku.version + '"'); assert.equal(request.headers.get('Idempotency-Key'), original.key);
      const body = await request.json() as Record<string, unknown>; assert.equal(body.reason, details.reason); assert.equal(body.provenanceReference, details.provenanceReference);
      if (attempt.operation === 'catalog.identifier.assign') {assert.ok(request.url.endsWith('/identifiers')); assert.equal(body.sourceAccount, null); assert.equal(body.definitionId, sku.currentDefinitionId); assert.equal(body.rawValue, '00012345600012');}
      if (attempt.operation === 'catalog.identifier.retire') {assert.ok(request.url.endsWith('/identifiers/' + alias.id + '/retire')); assert.equal(body.expectedIdentifierRevision, '1');}
      if (attempt.operation === 'catalog.identifier.correct-sku') {assert.ok(request.url.endsWith('/code')); assert.equal(body.primaryIdentifierId, primary.id); assert.equal(body.newCode, 'PENA-BARU');}
      return Response.json(expected);
    });
    assert.deepEqual(await gateway.submit(attempt, original, context), expected); assert.equal(calls.length, 1);
  }
});
test('lost reply and session change after dispatch remain unknown instead of claiming request not sent', async () => {
  let current = true;
  const late = transport(async () => {current = false; return Response.json(result);});
  await assert.rejects(late.gateway.submit(command, ref, {actorId, isCurrent: () => current}), error => error instanceof AccessFailure && error.code === 'catalog_identifier_outcome_unknown');
  assert.equal(late.calls.length, 1);
  const lost = transport(async () => {throw new TypeError('synthetic committed reply lost');});
  await assert.rejects(lost.gateway.submit(command, ref, context), TypeError); assert.equal(lost.calls.length, 1);
  const before = transport(async () => Response.json(result));
  await assert.rejects(before.gateway.submit(command, ref, {actorId, isCurrent: () => false}), RequestNotSentError); assert.equal(before.calls.length, 0);
});
test('durable journal reopens with separate tab references and only removes exact actor operation', () => {
  const storage = new MemoryStorage(), first = new IdentifierJournal(() => storage), second = new IdentifierJournal(() => storage);
  const other = {...ref, key: 'other-tab'}; first.remember(ref); second.remember(other);
  assert.equal(new IdentifierJournal(() => storage).list(actorId).length, 2); assert.equal(first.list('999').length, 0);
  assert.throws(() => second.remember({...ref, skuId: '999'}), AccessFailure);
  first.confirmed(ref); assert.deepEqual(second.list(actorId), [other]);
  const serialized = [...storage.values.values()][0]; assert.equal(serialized.includes('rawValue'), false); assert.equal(serialized.includes(details.reason), false);
  assert.throws(() => first.remember({...other, reason: 'must never persist'} as IdentifierReference), AccessFailure);
  second.confirmed(other); assert.deepEqual(first.list(actorId), []);
});
test('unreadable and silently dropping storage cannot mark a command as durably tracked', () => {
  const storage = new MemoryStorage(), journal = new IdentifierJournal(() => storage); storage.setItem = () => undefined;
  assert.throws(() => journal.remember(ref), AccessFailure);
  const corrupt = new MemoryStorage(); corrupt.values.set('unit-toko.catalog-identifier.pending.' + actorId + '.bad', '{}');
  assert.throws(() => new IdentifierJournal(() => corrupt).list(actorId), AccessFailure);
});
test('recovery uses original actor operation key and retains unknown or repeatedly denied references', async () => {
  const storage = new MemoryStorage(), journal = new IdentifierJournal(() => storage); journal.remember(ref);
  for (const status of [403, 409, 503, 200]) {
    const {calls, gateway} = transport(async request => {assert.equal(request.headers.get('Idempotency-Key'), null);
      assert.deepEqual(await request.json(), {purpose: 'operation-recovery', operation: ref.operation, key: ref.key});
      return Response.json(status === 200 ? {status: 'not_confirmed', operation: ref.operation, result: null} : {code: 'synthetic_refusal'}, {status});});
    if (status === 200) assert.equal((await gateway.recover(ref, context)).status, 'not_confirmed'); else await assert.rejects(gateway.recover(ref, context), AccessFailure);
    assert.equal(calls.length, 1); assert.deepEqual(journal.list(actorId), [ref]);
  }
  const confirmed = transport(async () => Response.json({status: 'confirmed', operation: ref.operation, result}));
  assert.deepEqual((await confirmed.gateway.recover(ref, context)).result, result); journal.confirmed(ref); assert.deepEqual(journal.list(actorId), []);
});
test('result and recovery reject different SKU, revision, operation and contradictory not-confirmed receipt', async () => {
  for (const changed of [{...result, skuId: '999'}, {...result, skuRevision: sku.version}, {...result, identifierRevision: '2'}, {...result, recordedAt: 'yesterday'}])
    assert.throws(() => parseIdentifierResult(changed, ref), AccessFailure);
  for (const receipt of [{status: 'confirmed', operation: 'catalog.identifier.retire', result}, {status: 'not_confirmed', operation: ref.operation, result},
    {status: 'confirmed', operation: ref.operation, result: {...result, skuId: '999'}}]) {
    const {gateway} = transport(async () => Response.json(receipt)); await assert.rejects(gateway.recover(ref, context), AccessFailure);
  }
});
