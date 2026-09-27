using System.Collections.Immutable;
using UnitToko.Modules.Identity;

namespace UnitToko.Modules.Organizations;

/// <summary>Names one explicit branch or division and one bounded, controlled read purpose.</summary>
public sealed record HistoricalReadRequest(string Permission, GrantScope TargetScope, string Purpose);

/// <summary>One complete alternative. Active authority nodes and historical target nodes are fenced separately.</summary>
public sealed record HistoricalReadClause(PermissionGrant Grant, Membership Membership,
    ImmutableArray<ScopeNode> AuthorityNodes, ImmutableArray<ScopeNode> TargetNodes);

/// <summary>Read-only evidence; this is never interchangeable with ordinary business authorization.</summary>
public sealed record HistoricalReadQuery(HistoricalReadRequest Request, long ActorId, long GrantVersion,
    ImmutableArray<HistoricalReadClause> Clauses, AssuranceRequirement Assurance, ScopeDirectory Directory,
    string? DenialCode = null)
{
    public bool IsAllowed => DenialCode is null && !Clauses.IsDefaultOrEmpty;

    /// <summary>Branches include only the canonical eligible divisions retained in complete target evidence.
    /// Secondary fulfillment dimensions grant no buyer authority and need no historical liveness exception.</summary>
    public bool Matches(ResourceScope resource) => IsAllowed && resource is not null && !resource.IsAdministrative &&
        HasValidSecondaryShape(resource) && HistoricalReadPolicy.IsTargetEligible(Request.TargetScope, Directory) &&
        resource.BuyingOrganizationId == Request.TargetScope.OrganizationId && resource.BranchId == Request.TargetScope.BranchId &&
        Clauses.Any(clause => ScopePolicy.ContainsScope(clause.Grant.Scope, Request.TargetScope) &&
            (Request.TargetScope.Kind == GrantScopeKind.BuyingDivision
                ? resource.DivisionId == Request.TargetScope.DivisionId
                : resource.DivisionId is null || clause.TargetNodes.Any(node => node.Kind == ScopeNodeKind.Division &&
                    node.Id == resource.DivisionId && node.ParentId == resource.BranchId && node.Version > 0 &&
                    node.Status is AuthorizationStatus.Active or AuthorizationStatus.Suspended)));

    private static bool HasValidSecondaryShape(ResourceScope resource) =>
        (resource.CreatedBy is null or > 0) && (resource.AssignedUserIds.IsDefaultOrEmpty || resource.AssignedUserIds.All(id => id > 0)) &&
        (resource.FulfillmentOrganizationId is null or > 0) && (resource.ShopId is null or > 0) &&
        (resource.WarehouseId is null or > 0) && (resource.ChannelId is null or > 0) &&
        (resource.ShopId is null || resource.FulfillmentOrganizationId is > 0) &&
        (resource.WarehouseId is null || resource.ShopId is > 0) &&
        (resource.ChannelId is null || resource.FulfillmentOrganizationId is > 0);
}
