import { configuration, pool } from './stock-source-policy-fixtures';
export { pool };
export const expected = { poolRevision: '9007199254740995', poolFence: '8', suspensionRevision: '2', policyId: '9007199254740996',
  policyRevision: '3', activationRevision: '0', configurationHash: 'a'.repeat(64) };
export const handoff = { reference: configuration.settings.handoffReference, observedAt: '2026-09-29T00:01:00.000Z', physicalPackages: '0', carriedValueMinorUnits: '0',
  emptyPoolConfirmed: true, previousWriterStopped: true, noUnrecordedStock: true };
export const domains = ['Orders', 'Reservations', 'Fulfillment', 'Procurement', 'AfterSales', 'SourceReconciliation', 'InventoryOperations'];
export const preview = { physicalPoolId: pool, expected, configuration, requiredHandoffReference: handoff.reference,
  obligations: domains.map(domain => ({ domain, producerRevision: '1', complete: true, openCount: '0' })), active: false };
export const proposal = { id: '9007199254740997', proposalVersion: '1', status: 'proposed', physicalPoolId: pool, expected, configuration,
  handoff, obligations: preview.obligations, proposedAt: '2026-09-29T00:01:01.000Z', reason: 'Serah terima sintetis', decision: null, decisionId: null, activationId: null };
export const checkerHandoff = { ...handoff, observedAt: '2026-09-29T00:01:02.000Z' };
export const review = { proposalId: proposal.id, physicalPoolId: pool, action: 'approve', expected: { ...expected, poolFence: '9' }, handoff: checkerHandoff, binding: 'b'.repeat(64), proposal };
export const proposedResult = { proposalId: proposal.id, proposalVersion: '1', status: 'proposed', physicalPoolId: pool, decisionId: null, activationId: null, recordedAt: proposal.proposedAt };
export const approvedResult = { ...proposedResult, status: 'activated', decisionId: '9007199254740998', activationId: proposal.id, recordedAt: '2026-09-29T00:01:03.000Z' };
