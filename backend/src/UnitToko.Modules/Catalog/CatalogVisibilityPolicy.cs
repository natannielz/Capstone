using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

public static class CatalogVisibilityPolicy
{
    /// <summary>Joins current catalog-read eligibility to explicitly approved access. It never treats a SKU as buyer-owned.</summary>
    public static CatalogVisibilityQuery Compile(ActorContext actor, AuthorizationSnapshot snapshot,
        ImmutableArray<CatalogEntitlement> entitlements, ImmutableArray<CatalogAssortment> assortments,
        DateTimeOffset now)
    {
        var authority = ScopePolicy.Compile(actor, snapshot, Permissions.CatalogRead, now);
        CatalogVisibilityQuery Deny(string code) => new(authority, [], code);
        if (!authority.IsAllowed) return Deny(authority.DenialCode ?? "permission_denied");
        if (entitlements.IsDefaultOrEmpty || assortments.IsDefaultOrEmpty) return Deny("catalog_visibility_denied");
        if (entitlements.Any(entitlement => entitlement is null || entitlement.Id <= 0) ||
            assortments.Any(assortment => assortment is null || assortment.Id <= 0) ||
            entitlements.Select(entitlement => entitlement.Id).Distinct().Count() != entitlements.Length ||
            assortments.Select(assortment => assortment.Id).Distinct().Count() != assortments.Length)
            return Deny("invalid_catalog_snapshot");

        var assortmentsById = assortments.ToDictionary(assortment => assortment.Id);
        var clauses = ImmutableArray.CreateBuilder<CatalogVisibilityClause>();
        foreach (var entitlement in entitlements)
        {
            if (!IsEntitlementActive(entitlement, actor.UserId, snapshot.Directory, now) ||
                !assortmentsById.TryGetValue(entitlement.AssortmentId, out var assortment) ||
                assortment.Status != AuthorizationStatus.Active || assortment.Version <= 0 ||
                !IsOwnerScope(assortment.OwnerScope) || !ScopePolicy.IsScopeActive(assortment.OwnerScope, snapshot.Directory) ||
                entitlement.AudienceScope.Kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse &&
                !ScopePolicy.ContainsScope(entitlement.AudienceScope, assortment.OwnerScope)) continue;

            foreach (var eligible in authority.Clauses)
            {
                if (eligible.ActorId != entitlement.UserId || eligible.MembershipId != entitlement.MembershipId ||
                    !ScopePolicy.ContainsScope(eligible.Scope, entitlement.AudienceScope)) continue;

                // ScopePolicy has already established unique, current grant/member records and their containment.
                var grant = snapshot.Grants.Single(candidate => candidate.Id == eligible.GrantId);
                var membership = snapshot.Memberships.Single(candidate => candidate.Id == eligible.MembershipId);
                var nodes = eligible.RequiredNodes
                    .Concat(ScopePolicy.RequiredNodes(entitlement.AudienceScope, snapshot.Directory))
                    .Concat(ScopePolicy.RequiredNodes(assortment.OwnerScope, snapshot.Directory))
                    .Distinct().ToImmutableArray();
                clauses.Add(new(entitlement, assortment, grant, membership, eligible, nodes));
            }
        }
        return clauses.Count == 0 ? Deny("catalog_visibility_denied") : new(authority, clauses.ToImmutable());
    }

    private static bool IsEntitlementActive(CatalogEntitlement entitlement, long actorId, ScopeDirectory directory,
        DateTimeOffset now) => entitlement.UserId == actorId && entitlement.MembershipId > 0 &&
        entitlement.AssortmentId > 0 && entitlement.Version > 0 && entitlement.RequestedBy > 0 &&
        entitlement.ApprovedBy > 0 && entitlement.RequestedBy != entitlement.ApprovedBy &&
        entitlement.ApprovedBy != entitlement.UserId && entitlement.ApprovalId > 0 && entitlement.ApprovalVersion > 0 &&
        entitlement.ApprovedAt != default && entitlement.ApprovedAt <= now &&
        ScopePolicy.IsEffective(entitlement.Status, entitlement.EffectiveAt, entitlement.ExpiresAt, entitlement.RevokedAt, now) &&
        entitlement.AudienceScope is { Kind: GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision or
            GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingOrganization or
            GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse } &&
        ScopePolicy.IsScopeActive(entitlement.AudienceScope, directory);

    internal static bool IsOwnerScope(GrantScope? scope) => scope is
    {
        OrganizationId: > 0, ShopId: > 0, ChannelId: > 0, BranchId: null, DivisionId: null
    } && (scope.Kind == GrantScopeKind.FulfillmentShop && scope.WarehouseId is null ||
          scope.Kind == GrantScopeKind.FulfillmentWarehouse && scope.WarehouseId is > 0);
}
