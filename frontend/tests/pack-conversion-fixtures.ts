import type { IdentitySession } from '../src/modules/identity/identity-workflow';
import { OperationJournal } from '../src/modules/administration/operation-journal';
import { parsePackProposal, parsePackPreview, preparePackInput } from '../src/modules/inventory/pack-conversion-workflow';
export const actorId = '9007199254740995';
export const location = { kind: 'fulfillmentWarehouse' as const, organizationId: '100', branchId: null, divisionId: null, shopId: '101', warehouseId: '9007199254740993', channelId: '103' };
export const warehouse = { location, organizationName: 'Koperasi', shopName: 'Toko Tebet', warehouseName: 'Gudang utama', channelName: 'OMI' };
export const source = { skuId: '9007199254740997', skuRevision: '9007199254740999', definitionId: '9007199254741001', definitionVersion: '1',
  skuCode: 'PENA-12', familyName: 'Pena biru', packageLabel: 'Dus tertutup isi 12', packageKind: 'sealed' as const, baseUnitCode: 'PCS', baseUnitsPerPackage: 12 };
export const destination = { ...source, skuId: '9007199254741003', skuRevision: '9007199254741005', definitionId: '9007199254741007',
  skuCode: 'PENA-1', packageLabel: 'Satuan', packageKind: 'loose' as const, baseUnitsPerPackage: 1 };
export const input = preparePackInput(location, source, destination, '1', '12');
export const preview = parsePackPreview({ input, source, destination, binding: 'a'.repeat(64), sourcePackages: '1', destinationPackages: '12', baseUnits: '12',
  physicalConversionAvailable: false, observedAt: '2026-10-05T01:00:00Z' });
export const proposal = parsePackProposal({ id: '9007199254741009', revision: '1', status: 'pending', input, source, destination,
  proposedAt: '2026-10-05T01:00:01Z', reason: 'Siapkan aturan dus ke satuan', decision: null, canReview: true, physicalConversionAvailable: false });
export const proposeResult = { proposalId: proposal.id, revision: '1' as const, state: 'pending' as const, decisionId: null, ruleId: null, physicalConversionAvailable: false as const };
export const approveResult = { ...proposeResult, state: 'approve' as const, decisionId: '9007199254741011', ruleId: proposal.id };
export const reviewPreview = { proposal, action: 'approve' as const, binding: 'b'.repeat(64), physicalConversionAvailable: false as const };
export const rule = { id: proposal.id, version: '1', proposalId: proposal.id, decisionId: approveResult.decisionId, input, source, destination,
  approvedAt: '2026-10-05T01:01:00Z', physicalConversionAvailable: false };
export const session: IdentitySession = { authenticated: true, account: { userId: actorId, displayName: 'Petugas', verifiedEmail: 'synthetic@example.test' },
  serverTime: '2026-10-05T01:00:00Z', factorCompletedAt: '2026-10-05T01:00:00Z', freshFactorUntil: '2026-10-05T01:05:00Z',
  idleExpiresAt: '2026-10-05T01:10:00Z', absoluteExpiresAt: '2026-10-05T02:00:00Z', stepUpOperations: [] };
export function journalFixture() {
  const values = new Map<string, string>(); const storage = { get length() { return values.size; }, key: (index: number) => [...values.keys()][index] ?? null,
    getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  return { values, journal: new OperationJournal(() => storage, () => new Date('2026-10-05T01:00:00Z')) };
}
