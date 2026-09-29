import { location } from "./stock-count-fixtures";
import { current as source } from "./stock-source-revision-fixtures";
export const requestedLocation = { ...location, channelId: "104" };
export const current = { source, requestedLocation, bindingState: "absent", mapping: null };
export const proposal = { id: "9007199254741001", proposalVersion: "1", status: "proposed", physicalPoolId: source.physicalPoolId,
  poolVersion: "1", ownerLocation: location, requestedLocation, physicalKey: source.settings.physicalKey, baseAliasVersion: "0", enabled: true,
  proposedAt: "2026-09-28T00:01:00Z", reason: "Akses kanal", decision: null, appliedAliasVersion: null };
export const approved = { ...proposal, status: "approved", decision: { status: "approved", decidedAt: "2026-09-28T00:02:00Z",
  reviewReference: "Review independen", reason: "Kanal telah diperiksa" }, appliedAliasVersion: "1" };
export const mapped = { ...current, bindingState: "current", mapping: approved };
export const outcome = { proposalId: proposal.id, proposalVersion: "1", status: "proposed", physicalPoolId: source.physicalPoolId,
  poolVersion: "1", baseAliasVersion: "0", appliedAliasVersion: null, decisionId: null };
export const approval = { ...outcome, status: "approved", baseAliasVersion: null, appliedAliasVersion: "1", decisionId: "9007199254741003" };
