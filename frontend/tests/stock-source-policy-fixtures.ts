import { settings } from "./stock-source-revision-fixtures";
export const pool = "9007199254740993";
export const configuration = { settings, active: false, sourceApprovedAt: "2026-09-29T00:00:00Z",
  creationProposalId: "11", revisionProposalId: "12", mappings: [
    { aliasId: "9007199254740994", aliasVersion: "3", poolVersion: "9007199254740995",
      location: { ...settings.location, channelId: "104" }, enabled: true, proposalId: "13", decisionId: "14" },
  ] };
export const preview = { physicalPoolId: pool, poolVersion: "9007199254740995", suspensionVersion: "2",
  policyVersion: "9007199254740996", configurationHash: "a".repeat(64), configuration };
export const input = { writer: "unitToko", externalTenant: null, feedIntervalMilliseconds: "0",
  reconciliationProcedureReference: "Rekonsiliasi stok", outageProcedureReference: "Prosedur gangguan" };
export const proposal = { id: "9007199254740997", proposalVersion: "1", status: "proposed", physicalPoolId: pool,
  poolVersion: preview.poolVersion, suspensionVersion: preview.suspensionVersion, basePolicyVersion: preview.policyVersion,
  configurationHash: preview.configurationHash, configuration, input, proposedAt: "2026-09-29T00:01:00Z", reason: "Pengajuan kebijakan",
  decision: null, decisionId: null, policyId: null, policyVersion: null };
export const review = { action: "approve", bindingCurrent: true, current: { poolVersion: preview.poolVersion,
  suspensionVersion: preview.suspensionVersion, policyVersion: preview.policyVersion, configurationHash: preview.configurationHash }, proposal };
export const outcome = { proposalId: proposal.id, proposalVersion: "1", status: "proposed", physicalPoolId: pool,
  basePolicyVersion: preview.policyVersion, decisionId: null, policyId: null, policyVersion: null };
export const approvedOutcome = { ...outcome, status: "approved", basePolicyVersion: null, decisionId: "9007199254740998",
  policyId: "9007199254740999", policyVersion: "9007199254740997" };
export const approved = { ...proposal, status: "approved", decisionId: approvedOutcome.decisionId,
  policyId: approvedOutcome.policyId, policyVersion: approvedOutcome.policyVersion,
  decision: { status: "approved", decidedAt: "2026-09-29T00:02:00Z", reviewReference: "Persetujuan", reason: "Tinjauan independen" } };