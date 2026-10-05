import { z } from 'zod';
import { RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { procurementLocationKey } from './procurement-request-client';
import { parseStockReceiptPools, type StockReceiptPool } from './procurement-stock-receipt-client';

// Strict explicit planning extension; generated wire types come from the maintained .NET API.
const { id, hash } = paymentAllocationSchemas;
const inboundId = id.refine(value => BigInt(value) < BigInt('9223372036854775806'));
const allocation = z.object({ requestSkuId: inboundId, packages: z.number().int().min(1).max(1_000_000), poolId: inboundId, poolRevision: inboundId }).strict();
const compare = (a: { requestSkuId: string; poolId: string }, b: { requestSkuId: string; poolId: string }) => BigInt(a.requestSkuId) < BigInt(b.requestSkuId) ? -1 : BigInt(a.requestSkuId) > BigInt(b.requestSkuId) ? 1 : BigInt(a.poolId) < BigInt(b.poolId) ? -1 : BigInt(a.poolId) > BigInt(b.poolId) ? 1 : 0;
const uniqueAllocations = (rows: z.infer<typeof allocation>[]) => new Set(rows.map(row => row.requestSkuId + ':' + row.poolId)).size === rows.length && new Set(rows.map(row => row.poolId)).size <= 8 && rows.every(row => rows.filter(other => other.poolId === row.poolId).every(other => row.poolRevision === other.poolRevision));
export const purchaseOrderInboundSelectionSchema = z.object({ allocations: z.array(allocation).min(1).max(100) }).strict().refine(value => uniqueAllocations(value.allocations));
const pool = z.custom<StockReceiptPool>(value => {
  try { const row = value as StockReceiptPool; parseStockReceiptPools({ location: row.requestedLocation, items: [value], nextPoolId: null }); return BigInt(row.id) < BigInt('9223372036854775806') && BigInt(row.revision) < BigInt('9223372036854775806'); } catch { return false; }
});
const receiving = { allocations: z.array(allocation).min(1).max(100), receivingPools: z.array(pool).min(1).max(8) };
const coherentPools = (value: { allocations: z.infer<typeof allocation>[]; receivingPools: StockReceiptPool[] }) => {
  const ids = [...new Set(value.allocations.map(row => row.poolId))].sort((a, b) => BigInt(a) < BigInt(b) ? -1 : 1);
  return uniqueAllocations(value.allocations) && value.allocations.every((row, at) => at === 0 || compare(value.allocations[at - 1], row) < 0) && ids.length === value.receivingPools.length && value.receivingPools.every((row, at) => ids[at] === row.id && value.allocations.filter(item => item.poolId === row.id).every(item => item.poolRevision === row.revision));
};
export const purchaseOrderInboundPreviewSchema = z.object(receiving).strict().refine(coherentPools);
export const purchaseOrderInboundBindingSchema = z.object({ ...receiving, selectionId: inboundId, selectionHash: hash, decisionId: inboundId.nullable(), state: z.enum(['proposed', 'approve', 'reject']), unplannedPackagesBound: z.literal(false), stockReserved: z.literal(false), physicalStockReleased: z.literal(false) }).strict().refine(coherentPools).refine(value => (value.state === 'proposed') === (value.decisionId === null));
export type PurchaseOrderInboundSelection = z.infer<typeof purchaseOrderInboundSelectionSchema>;
export type PurchaseOrderInboundPreview = z.infer<typeof purchaseOrderInboundPreviewSchema>;
export type PurchaseOrderInboundBinding = z.infer<typeof purchaseOrderInboundBindingSchema>;
type InboundOrder = { location: StockReceiptPool['requestedLocation']; lines: readonly { requestSkuId: string; packages: number }[] };
export function inboundMatchesInput(input: InboundOrder, value: { allocations: PurchaseOrderInboundSelection['allocations']; receivingPools?: StockReceiptPool[] }) {
  return new Set(value.allocations.map(row => row.requestSkuId)).size === input.lines.length && value.allocations.every(row => input.lines.some(line => line.requestSkuId === row.requestSkuId)) && input.lines.every(line => value.allocations.filter(row => row.requestSkuId === line.requestSkuId).reduce((sum, row) => sum + BigInt(row.packages), BigInt(0)) === BigInt(line.packages)) && (value.receivingPools === undefined || value.receivingPools.every(row => procurementLocationKey(row.requestedLocation) === procurementLocationKey(input.location)));
}
export function purchaseOrderInboundSelection(input: InboundOrder, value: unknown): PurchaseOrderInboundSelection {
  const parsed = purchaseOrderInboundSelectionSchema.safeParse(value);
  if (!parsed.success || !inboundMatchesInput(input, parsed.data)) throw new RequestNotSentError(400, 'purchase_order_invalid');
  return { allocations: [...parsed.data.allocations].sort(compare) };
}
export function requireInboundPreview(input: InboundOrder, selected: PurchaseOrderInboundSelection | undefined, shown: PurchaseOrderInboundPreview | undefined) {
  if (selected === undefined ? shown !== undefined : shown === undefined || !inboundMatchesInput(input, shown) || JSON.stringify(purchaseOrderInboundSelection(input, selected)) !== JSON.stringify({ allocations: shown.allocations })) throw new AccessFailure(502, 'purchase_order_response_invalid');
}
export function requireInboundChoices(input: InboundOrder, selected: PurchaseOrderInboundSelection, choices: readonly StockReceiptPool[], shown: PurchaseOrderInboundPreview) {
  requireInboundPreview(input, selected, shown);
  if (choices.length !== shown.receivingPools.length || shown.receivingPools.some(row => !choices.some(chosen => JSON.stringify(chosen) === JSON.stringify(row)))) throw new RequestNotSentError(409, 'purchase_order_view_stale');
}
