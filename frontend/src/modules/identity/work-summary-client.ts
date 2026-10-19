import { z } from 'zod';
import { createUnitTokoClient } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { formatNumber } from '@/lib/format';
import { workspaceNavigation } from './workspace-navigation';

const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/\p{Cc}/u.test(value));
// App-relative only: a second leading slash would make a protocol-relative URL to another host.
const appPath = z.string().max(200).regex(/^\/(?!\/)[A-Za-z0-9/_-]*$/);
const timestamp = z.string().datetime({ offset: true });
const item = z.object({
  key: z.string().regex(/^[a-z][a-z0-9.-]{1,79}$/), area: z.string().max(40), label: text(160), href: appPath,
  count: z.number().int().min(0).max(100000), hasMore: z.boolean(), oldestAt: timestamp.nullable(), unavailable: z.boolean(),
}).strict();
const summary = z.object({ items: z.array(item).max(50), observedAt: timestamp }).strict();
export type WorkSummaryItem = z.infer<typeof item>;
export type WorkSummary = z.infer<typeof summary>;

export function parseWorkSummary(value: unknown): WorkSummary {
  const result = summary.safeParse(value);
  if (!result.success) throw new AccessFailure(502, 'work_summary_invalid');
  return result.data;
}

/** "menunggu sejak 2 jam"; "menunggu setidaknya 2 jam" when only the owner's first page was counted; "" without a waiting time. */
export function workAgeText(oldestAt: string | null, now: number, partial = false): string {
  if (oldestAt === null) return '';
  const minutes = Math.floor((now - Date.parse(oldestAt)) / 60000);
  if (minutes < 1) return 'menunggu kurang dari 1 menit';
  const since = partial ? 'menunggu setidaknya' : 'menunggu sejak';
  if (minutes < 60) return `${since} ${minutes} menit`;
  const hours = Math.floor(minutes / 60);
  return hours < 24 ? `${since} ${hours} jam` : `${since} ${Math.floor(hours / 24)} hari`;
}

/** "25+" when the owner's first page had more; "Perlu diperiksa" when that page had nothing waiting but more pages exist. */
export function workCountText(count: number, hasMore: boolean): string {
  if (count === 0 && hasMore) return 'Perlu diperiksa';
  return formatNumber(count) + (hasMore ? '+' : '');
}

/** Backstop to the server's own check: never offer work whose workspace the actor's navigation does not show. */
export function visibleWorkItems(items: readonly WorkSummaryItem[], actions: readonly string[]): WorkSummaryItem[] {
  const hrefs = new Set(workspaceNavigation(actions, null).flatMap(group => group.items.filter(entry => entry.available).map(entry => entry.href.split('#')[0])));
  return items.filter(entry => hrefs.has(entry.href));
}

export function createWorkSummaryGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async read(actorId: string, signal?: AbortSignal): Promise<WorkSummary> {
      const response = await client.POST('/api/v1/work/summary/query', {
        body: { purpose: 'work-summary' }, params: { header: { 'X-Expected-Actor-Id': actorId } }, signal, cache: 'no-store' });
      if (!response.response.ok || response.data === undefined) {
        const error = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
        throw new AccessFailure(response.response.status, typeof error.code === 'string' ? error.code : 'request_failed');
      }
      return parseWorkSummary(response.data);
    },
  };
}
