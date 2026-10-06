import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RequestNotSentError } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { createPackTransport, createPackConversionGateway } from '../src/modules/inventory/pack-conversion-client';
import { preparePackProposal, preparePackDecision } from '../src/modules/inventory/pack-conversion-workflow';
import { actorId, location, source, input, preview, reviewPreview, proposeResult, approveResult, proposal } from './pack-conversion-fixtures';

test('transport sends only frozen proposal and original key with cookie/CSRF fences; decision includes exact If-Match', async () => {
  const requests: Request[] = []; const transport = createPackTransport({ origin: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); requests.push(request);
    if (request.url.endsWith('/identity/csrf')) return Response.json({ requestToken: 'csrf' });
    return Response.json(request.url.endsWith('/approve') ? approveResult : proposeResult);
  } });
  const gateway = createPackConversionGateway(transport), command = preparePackProposal(actorId, preview, 'Aturan diperiksa');
  await gateway.submit(command, 'same-key', () => true); await gateway.submit(preparePackDecision(actorId, reviewPreview, 'Disetujui'), 'decision-key', () => true);
  const [proposalRequest, decisionRequest] = requests.filter(item => !item.url.endsWith('/identity/csrf'));
  assert.equal(proposalRequest.credentials, 'same-origin'); assert.equal(proposalRequest.cache, 'no-store'); assert.equal(proposalRequest.redirect, 'error');
  assert.equal(proposalRequest.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(proposalRequest.headers.get('X-CSRF-Token'), 'csrf');
  assert.equal(proposalRequest.headers.get('Idempotency-Key'), 'same-key'); assert.deepEqual(await proposalRequest.clone().json(), command.body);
  assert.equal(decisionRequest.headers.get('If-Match'), '"1"'); assert.equal(decisionRequest.headers.get('Idempotency-Key'), 'decision-key');
});
test('expiry after CSRF prevents dispatch; lost reply never triggers transport retry or altered command', async () => {
  let current = true, writes = 0;
  const transport = createPackTransport({ origin: 'https://unit-toko.test', fetch: async request => {
    assert.ok(request instanceof Request); if (request.url.endsWith('/identity/csrf')) { current = false; return Response.json({ requestToken: 'csrf' }); }
    writes++; return Response.json(proposeResult);
  } });
  const gateway = createPackConversionGateway(transport), command = preparePackProposal(actorId, preview, 'Aturan diperiksa');
  await assert.rejects(() => gateway.submit(command, 'original', () => current), error => error instanceof RequestNotSentError); assert.equal(writes, 0);
  let attempts = 0; const lost = createPackConversionGateway({ send: async () => { attempts++; throw new TypeError('reply lost'); } });
  await assert.rejects(() => lost.submit(command, 'original', () => true)); assert.equal(attempts, 1);
  await assert.rejects(() => lost.submit({ ...command, body: { ...command.body, reason: 'Changed after review' } }, 'original', () => true)); assert.equal(attempts, 1);
});
test('scoped options/preview/recovery use exact purposes and no private original body in recovery', async () => {
  const calls: { path: string; body: unknown }[] = [], gateway = createPackConversionGateway({ send: async request => {
    calls.push(request); if (request.path === '/packages/query') return { total: '1', items: [source] };
    if (request.path === '/rules/preview/query') return preview;
    return { state: 'committed', result: proposeResult };
  } });
  await gateway.packages(actorId, location, 'source'); await gateway.preview(actorId, input);
  await gateway.receipt({ actorId, operation: 'inventory.pack-conversion.rule.propose', key: 'original', createdAt: preview.observedAt });
  assert.deepEqual(calls[0].body, { purpose: 'pack-conversion-rule-source', location, role: 'source', search: null, offset: 0, limit: 25 });
  assert.deepEqual(calls[1].body, { purpose: 'pack-conversion-rule-preview', input });
  assert.deepEqual(calls[2].body, { purpose: 'operation-recovery', operation: 'inventory.pack-conversion.rule.propose', key: 'original' });
  const self = createPackConversionGateway({ send: async () => { assert.fail('server capability denies review without guessing identity'); } });
  await assert.rejects(() => self.review(actorId, { ...proposal, canReview: false }, 'approve'), /independent_person_required/);
});
test('malformed result remains uncertain and exact selected decision cannot accept another outcome', async () => {
  for (const result of [null, { ...approveResult, proposalId: '6', ruleId: '6' }]) {
    const gateway = createPackConversionGateway({ send: async () => result });
    await assert.rejects(() => gateway.submit(preparePackDecision(actorId, reviewPreview, 'Disetujui'), 'original', () => true),
      error => error instanceof AccessFailure && error.status === 503 && error.code === 'commit_outcome_unknown');
  }
});
