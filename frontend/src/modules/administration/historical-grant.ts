import type { components } from "../../api/generated/schema";
import { toIsoDate } from "../organizations/access-scope";

type Schemas = components["schemas"];
export type HistoricalGrantRequest = Schemas["HistoricalGrantRequestDto"];
export type HistoricalGrantProposal = Schemas["HistoricalGrantProposalDto"];
export type HistoricalDirectoryNode = Schemas["HistoricalTargetDto"];
type Membership = Schemas["AccessMembershipDto"];
export const historicalPermissions = ["history.read", "report.read"] as const;

export function historicalParentMemberships(memberships: readonly Membership[], now = Date.now()) {
  return memberships.filter(membership => membership.status === "active" && membership.scope.kind === "buyingOrganization" &&
    !!membership.scope.organizationId && !membership.scope.branchId && !membership.scope.divisionId && !membership.scope.shopId && !membership.scope.warehouseId && !membership.scope.channelId &&
    Date.parse(membership.effectiveAt) <= now && (!membership.expiresAt || Date.parse(membership.expiresAt) > now));
}

const validTarget = (node: HistoricalDirectoryNode) => node.status === "active" || node.status === "suspended";
export function historicalBranches(directory: readonly HistoricalDirectoryNode[], organizationId?: string | null) {
  return directory.filter(node => node.kind === "branch" && node.parentId === organizationId && node.scope.kind === "buyingBranch" &&
    node.scope.organizationId === organizationId && node.scope.branchId === node.id && !node.scope.divisionId && validTarget(node));
}
export function historicalDivisions(directory: readonly HistoricalDirectoryNode[], branchId: string) {
  const branch = directory.find(node => node.id === branchId && node.kind === "branch" && validTarget(node));
  return directory.filter(node => branch && node.kind === "division" && node.parentId === branchId && node.scope.kind === "buyingDivision" &&
    node.scope.organizationId === branch.scope.organizationId && node.scope.branchId === branchId && node.scope.divisionId === node.id && validTarget(node));
}
export function historicalNodeLabel(node: HistoricalDirectoryNode) {
  const name = node.name || node.code || `Unit ${node.id}`;
  return `${name}${node.name && node.code ? ` (${node.code})` : ""}${node.status === "suspended" ? " — Ditangguhkan" : ""}`;
}

export function historicalScopeLabel(scope: Schemas["AccessScopeDto"], directory: readonly HistoricalDirectoryNode[] = []) {
  const target = directory.find(node => node.id === (scope.kind === "buyingDivision" ? scope.divisionId : scope.branchId));
  const unit = target ? historicalNodeLabel(target) : scope.kind === "buyingDivision" ? `Divisi ${scope.divisionId}` : `Cabang ${scope.branchId}`;
  return `${unit} · Organisasi ${scope.organizationId}${scope.kind === "buyingBranch" ? " · beserta divisi yang sah di bawahnya" : ""}`;
}

/** Targets are selected exclusively from an authoritative directory under the approved parent. */
export function buildHistoricalGrant(input: {
  userId: string; membership?: Membership; directory: readonly HistoricalDirectoryNode[];
  kind: "buyingBranch" | "buyingDivision"; branchId: string; divisionId: string;
  permissions: readonly string[]; effective: string; expiry: string;
}, now = Date.now()): HistoricalGrantRequest | null {
  const { membership, directory, kind, branchId, divisionId } = input;
  if (!membership || !historicalParentMemberships([membership], now).length) return null;
  if (!historicalBranches(directory, membership.scope.organizationId).some(node => node.id === branchId)) return null;
  if (kind === "buyingDivision" && !historicalDivisions(directory, branchId).some(node => node.id === divisionId)) return null;
  if (!input.permissions.length || new Set(input.permissions).size !== input.permissions.length ||
    input.permissions.some(permission => !historicalPermissions.some(allowed => allowed === permission))) return null;
  const effectiveAt = toIsoDate(input.effective), expiresAt = toIsoDate(input.expiry);
  if (!effectiveAt || input.expiry && !expiresAt || expiresAt && Date.parse(expiresAt) <= Date.parse(effectiveAt)) return null;
  return { userId: input.userId, membershipId: membership.id, permissions: [...input.permissions].sort(),
    scope: { kind, organizationId: membership.scope.organizationId, branchId,
      divisionId: kind === "buyingDivision" ? divisionId : null, shopId: null, warehouseId: null, channelId: null },
    effectiveAt, expiresAt };
}

/** Freeze the reviewed body in memory; the recovery journal stores only the operation reference. */
export function historicalReviewedBody(grant: HistoricalGrantRequest, reason: string): HistoricalGrantProposal | null {
  if (!reason.trim() || reason.trim().length > 500) return null;
  const reviewed = { grant: { ...grant, permissions: [...grant.permissions], scope: { ...grant.scope } }, reason: reason.trim() };
  Object.freeze(reviewed.grant.permissions); Object.freeze(reviewed.grant.scope); Object.freeze(reviewed.grant); Object.freeze(reviewed);
  return reviewed;
}
