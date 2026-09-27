using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

/// <summary>Stored, explicitly approved access to an assortment. This record does not confer issuance authority.</summary>
public sealed record CatalogEntitlement(
    long Id, long UserId, long MembershipId, long AssortmentId, GrantScope AudienceScope,
    AuthorizationStatus Status, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    long Version, long RequestedBy, long ApprovedBy, long ApprovalId, long ApprovalVersion, DateTimeOffset ApprovedAt);

/// <summary>Server-owned publication boundary; its fulfillment owner must remain active.</summary>
public sealed record CatalogAssortment(long Id, GrantScope OwnerScope, AuthorizationStatus Status, long Version);

/// <summary>A product's assortment and owner are one binding, never independent ID lists.</summary>
public sealed record CatalogListing(long AssortmentId, GrantScope OwnerScope);

/// <summary>One complete alternative; every source record and hierarchy node is retained for transactional fencing.</summary>
public sealed record CatalogVisibilityClause(
    CatalogEntitlement Entitlement, CatalogAssortment Assortment, PermissionGrant Grant, Membership Membership,
    ScopeClause Authority, ImmutableArray<ScopeNode> RequiredNodes);

public sealed record CatalogVisibilityQuery(
    AuthorizationQuery Authority, ImmutableArray<CatalogVisibilityClause> Clauses, string? DenialCode = null)
{
    public bool IsAllowed => DenialCode is null && Authority.IsAllowed && !Clauses.IsDefaultOrEmpty;

    /// <summary>Convenient projection only; callers must authorize with complete alternatives.</summary>
    public ImmutableArray<long> AssortmentIds => IsAllowed
        ? Clauses.Select(clause => clause.Assortment.Id).Distinct().ToImmutableArray() : [];

    public bool Matches(ImmutableArray<CatalogListing> listings)
    {
        if (!IsAllowed || listings.IsDefaultOrEmpty || listings.Length > 128 ||
            listings.Any(listing => listing is null || listing.AssortmentId <= 0 ||
                !CatalogVisibilityPolicy.IsOwnerScope(listing.OwnerScope)) ||
            listings.Select(listing => listing.AssortmentId).Distinct().Count() != listings.Length ||
            listings.Select(listing => listing.OwnerScope.OrganizationId).Distinct().Count() != 1) return false;

        return Clauses.Any(clause => listings.Any(listing => listing.AssortmentId == clause.Assortment.Id &&
            listing.OwnerScope == clause.Assortment.OwnerScope));
    }
}
