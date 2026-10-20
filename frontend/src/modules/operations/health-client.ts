import { z } from 'zod';
import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';

// Q10: counts, ages and states only. Any extra field (a name, an amount, a payload) makes the response invalid.
const count = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER);
const time = z.string().datetime({ offset: true });
const queue = z.object({ pending: count, retrying: count, dead: count, leased: count, oldestWaitingAt: time.nullable() }).strict();
const report = z.object({ observedAt: time, outbox: queue, ocr: queue, lastDraftPurgeAt: time.nullable(), overdueDraftPurges: count,
  schemaVersion: count, targetSchemaVersion: count.min(1), databaseWritable: z.boolean(),
  replicationLagSeconds: z.number().nonnegative().finite().nullable() }).strict();
export type QueueHealth = z.infer<typeof queue>;
export type OperatorHealth = z.infer<typeof report>;

export function parseOperatorHealth(value: unknown): OperatorHealth {
  const checked = report.safeParse(value); if (!checked.success) throw new AccessFailure(502, 'operator_health_response_invalid'); return checked.data;
}

export function createOperatorHealthGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, signal?: AbortSignal): Promise<OperatorHealth> {
      const response = await client.POST('/api/v1/operations/health/query', { body: { purpose: 'operator-health' },
        headers: { 'X-Expected-Actor-Id': actorId }, signal });
      if (!response.response.ok || response.data === undefined)
        throw new AccessFailure(response.response.status, (response.error as { code?: string } | undefined)?.code ?? 'operator_health_unavailable');
      return parseOperatorHealth(response.data);
    },
  };
}
