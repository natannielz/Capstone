import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUnitTokoClient, RequestNotSentError } from '../src/api/client';
import { AccessFailure } from '../src/modules/administration/workflow';
import { createStockReleaseGateway } from '../src/modules/inventory/stock-release-client';
import { prepareReleaseProposal, releaseProposeOperation } from '../src/modules/inventory/stock-release-workflow';
import { actorId, preview, proposed } from './stock-release-fixtures';

test('generated client sends original actor, key and exact string stock revision and rejects changed body reuse', async () => {
  const calls: Array<{ path: string; options: { params: { header: Record<string, string> }; body: unknown } }> = [];
  const client = { POST: async (path: string, options: typeof calls[number]['options']) => { calls.push({ path, options }); return { data: proposed, response: new Response('', { status: 200 }) }; } } as unknown as ReturnType<typeof createUnitTokoClient>;
  const gateway = createStockReleaseGateway(client), command = prepareReleaseProposal(actorId, preview, 'Pengajuan asli');
  assert.equal((await gateway.submit(command, 'original-release-key', () => true)).status, 'proposed');
  assert.equal(calls[0].options.params.header['If-Match'], '"9007199254740993"');
  assert.equal(calls[0].options.params.header['X-Expected-Actor-Id'], actorId);
  await assert.rejects(gateway.submit(prepareReleaseProposal(actorId, preview, 'Alasan berubah'), 'original-release-key', () => true), RequestNotSentError);
  assert.equal(calls.length, 1);
});
test('a malformed successful mutation remains uncertain and recovery only looks up original key', async () => {
  const calls: string[] = [];
  const client = { POST: async (path: string) => { calls.push(path); return { data: path.endsWith('/operations/query') ? { state: 'not_confirmed', result: null } : { proposalId: proposed.proposalId },
    response: new Response('', { status: 200 }) }; } } as unknown as ReturnType<typeof createUnitTokoClient>;
  const gateway = createStockReleaseGateway(client);
  await assert.rejects(gateway.submit(prepareReleaseProposal(actorId, preview, 'Periksa'), 'unknown-release-key', () => true), error => error instanceof AccessFailure && error.code === 'commit_outcome_unknown');
  assert.equal(await gateway.receipt({ actorId, operation: releaseProposeOperation, key: 'unknown-release-key', createdAt: '2026-10-06T01:00:00Z' }), null);
  assert.equal(calls.filter(path => path.endsWith('/proposals')).length, 1);
});
test('stale view is checked again after generated CSRF fetch, before any business dispatch', async () => {
  let current = true; const urls: string[] = [];
  const fetcher: typeof fetch = async input => {
    const request = input instanceof Request ? input : new Request(input); urls.push(request.url);
    current = false; return Response.json({ requestToken: 'synthetic-csrf' });
  };
  const gateway = createStockReleaseGateway(createUnitTokoClient({ baseUrl: 'https://synthetic.invalid' }), fetcher);
  await assert.rejects(gateway.submit(prepareReleaseProposal(actorId, preview, 'Periksa'), 'csrf-stale-key', () => current), RequestNotSentError);
  assert.equal(urls.length, 1); assert.ok(urls[0].endsWith('/identity/csrf'));
});
