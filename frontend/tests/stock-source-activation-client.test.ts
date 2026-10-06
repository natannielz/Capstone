import { test } from 'node:test';
import assert from 'node:assert/strict';
import type { createUnitTokoClient } from '../src/api/client';
import { createStockSourceActivationGateway } from '../src/modules/inventory/stock-source-activation-client';
import { prepareActivationProposal } from '../src/modules/inventory/stock-source-activation-workflow';
import { AccessFailure } from '../src/modules/administration/workflow';
import { handoff, preview, proposedResult, pool } from './stock-source-activation-fixtures';

test('typed adapter keeps actual actor/original key/body/version; lost successful reply blocks changed-body reuse', async () => {
  const calls: { path: string; options: Record<string, unknown> }[] = []; let lose = true;
  const client = { POST: async (path: string, options: Record<string, unknown>) => {
    calls.push({ path, options }); return { data: lose ? undefined : proposedResult, response: new Response('', { status: 200 }) };
  } } as unknown as ReturnType<typeof createUnitTokoClient>;
  const gateway = createStockSourceActivationGateway(client), command = prepareActivationProposal('1', preview, handoff, 'Serah terima');
  await assert.rejects(gateway.submit(command, 'activation-original'), error => error instanceof AccessFailure && error.code === 'commit_outcome_unknown');
  assert.equal(calls.length, 1);
  const params = calls[0].options.params as { header: Record<string, string>; path: { poolId: string } };
  assert.equal(params.header['X-Expected-Actor-Id'], '1'); assert.equal(params.header['Idempotency-Key'], 'activation-original');
  assert.equal(params.header['If-Match'], '"9007199254740995"'); assert.equal(params.path.poolId, pool); assert.deepEqual(calls[0].options.body, command.body);
  const changed = prepareActivationProposal('1', preview, handoff, 'Alasan berubah');
  await assert.rejects(gateway.submit(changed, 'activation-original')); assert.equal(calls.length, 1);
  lose = false; assert.equal((await gateway.submit(command, 'activation-original')).status, 'proposed'); assert.equal(calls.length, 2);
});
test('read adapter rejects a foreign source and never sends mutation after actor mount becomes stale', async () => {
  let sends = 0;
  const client = { POST: async () => { sends++; return { data: { ...preview, physicalPoolId: '9' }, response: new Response('', { status: 200 }) }; } } as unknown as ReturnType<typeof createUnitTokoClient>;
  const gateway = createStockSourceActivationGateway(client); await assert.rejects(gateway.preview('1', pool));
  const before = sends; await assert.rejects(gateway.submit(prepareActivationProposal('1', preview, handoff, 'Serah terima'), 'stale', () => false)); assert.equal(sends, before);
});
