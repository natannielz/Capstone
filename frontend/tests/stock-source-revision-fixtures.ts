import { location } from "./stock-count-fixtures";
export const settings = { location, physicalKey: "pool-utama", label: "Original", mode: "webLedger", control: "exclusiveWebAllocation",
  maximumAgeMilliseconds: "60001", safetyBufferBaseUnits: "9007199254740993", externalNamespace: null, reservationProtocolReference: null,
  discrepancyOwnerReference: "PIC gudang", handoffReference: "Serah terima" };
export const current = { physicalPoolId: "9007199254740993", poolVersion: "1", poolState: "inactive", settings,
  approvedAt: "2026-09-28T00:00:00Z", creationProposalId: "11", revisionProposalId: null };
export const proposal = { id: "12", proposalVersion: "1", status: "proposed", physicalPoolId: current.physicalPoolId, basePoolVersion: "1",
  baseSettings: settings, proposedSettings: { ...settings, label: "Revised" }, proposedAt: "2026-09-28T00:01:00Z", reason: "Perubahan sumber", decision: null, appliedPoolVersion: null };
export const outcome = { proposalId: "12", proposalVersion: "1", status: "proposed", physicalPoolId: current.physicalPoolId,
  basePoolVersion: "1", appliedPoolVersion: null, decisionId: null };
