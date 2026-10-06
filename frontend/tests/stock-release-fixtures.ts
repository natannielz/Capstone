import type { IdentitySession } from '../src/modules/identity/identity-workflow';
import { parseReleasePreview, parseReleaseDetail, parseReleaseReview, releaseValuePolicy } from '../src/modules/inventory/stock-release-workflow';
export const actorId = '9007199254740995';
export const location = { kind: 'fulfillmentWarehouse' as const, organizationId: '100', branchId: null, divisionId: null, shopId: '101', warehouseId: '102', channelId: '103' };
export const warehouse = { location, organizationName: 'Koperasi sintetis', shopName: 'Toko Tebet sintetis', warehouseName: 'Gudang utama sintetis', channelName: 'OMI sintetis' };
export const choice = { receiptId: '9007199254741001', qualityAllocationId: '9007199254741003', valuationAllocationId: '9007199254741005',
  receiptReference: 'PENERIMAAN-SINTETIS-001', productName: 'Pena biru sintetis', packagingLabel: 'Dus tertutup isi 12', lotReference: 'LOT-SINTETIS-001', expiresOn: '2027-01-31',
  acceptedPackages: '3', valuedPackages: '3', releaseablePackages: '3', capitalizedValue: '0.03', policy: releaseValuePolicy };
export const input = { location, receiptId: choice.receiptId, qualityAllocationId: choice.qualityAllocationId, valuationAllocationId: choice.valuationAllocationId, packages: 2, policy: releaseValuePolicy };
export const expected = { balanceRevision: '9007199254740993', poolRevision: '1', poolFence: '8', heldValueRevision: '0', lotValueRevision: '0' };
export const selection = { receiptId: choice.receiptId, qualityAllocationId: choice.qualityAllocationId, valuationAllocationId: choice.valuationAllocationId,
  acceptedPackages: choice.acceptedPackages, valuedPackages: choice.valuedPackages, capitalizedValue: choice.capitalizedValue };
export const before = { balanceId: '9007199254741007', poolId: '9007199254741009', skuId: '9007199254741011', definitionId: '9007199254741013', lotId: '9007199254741015',
  revision: expected.balanceRevision, onHandPackages: '3', reservedPackages: '0', heldPackages: '3', expiredPackages: '0', availablePackages: '0' };
export const after = { ...before, revision: '9007199254740994', heldPackages: '1', availablePackages: '2' };
export const preview = parseReleasePreview({ input, expected, selection, stockBefore: before, stockAfter: after, binding: 'a'.repeat(64), observedAt: '2026-10-06T01:00:00.000Z' });
export const detail = parseReleaseDetail({ proposal: { id: '9007199254741017', revision: '1', input, expected, selection, stockBefore: before, stockAfter: after,
  proposedAt: '2026-10-06T01:00:01.000Z', reason: 'Pengajuan pelepasan sintetis' }, decision: null, status: 'proposed' });
export const review = parseReleaseReview({ proposal: detail.proposal, expected, selection, stockBefore: before, stockAfter: after,
  policy: releaseValuePolicy, action: 'approve', binding: 'b'.repeat(64), observedAt: '2026-10-06T01:01:00.000Z' }, detail, 'approve');
export const proposed = { proposalId: detail.proposal.id, revision: '1' as const, status: 'proposed' as const, decisionId: null, movementId: null, valueTransitionId: null, transferredValue: null };
export const approved = { ...proposed, status: 'approved' as const, decisionId: '9007199254741019', movementId: '9007199254741021', valueTransitionId: '9007199254741023', transferredValue: '0.02' };
export const session: IdentitySession = { authenticated: true, account: { userId: actorId, displayName: 'Petugas sintetis', verifiedEmail: 'synthetic@example.invalid' },
  serverTime: '2026-10-06T01:00:00.000Z', factorCompletedAt: '2026-10-06T01:00:00.000Z', freshFactorUntil: '2026-10-06T01:05:00.000Z',
  idleExpiresAt: '2026-10-06T01:30:00.000Z', absoluteExpiresAt: '2026-10-06T13:00:00.000Z', stepUpOperations: ['stock.release.approve'] };
export class MemoryStorage {
  private readonly values = new Map<string, string>(); get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; } getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); } removeItem(key: string) { this.values.delete(key); }
}
