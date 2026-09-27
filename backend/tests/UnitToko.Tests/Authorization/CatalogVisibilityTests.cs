using System.Collections.Immutable;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class CatalogVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Actor = new(101, "https://identity.example", "subject-101", 7,
        Now.AddMinutes(-1), FactorAssurance.MultiFactor);
    private static readonly GrantScope Buyer = new(GrantScopeKind.BuyerOwned, 1, 11, 111);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 2, ShopId: 21, ChannelId: 201);
    private static readonly CatalogAssortment Assortment = new(7001, Shop, AuthorizationStatus.Active, 3);
    private static readonly ScopeDirectory Directory = new([
        new(1, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 2),
        new(11, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 3),
        new(12, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 1),
        new(111, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 4),
        new(121, ScopeNodeKind.Division, 12, AuthorizationStatus.Active, 1),
        new(2, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 5),
        new(21, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 6),
        new(22, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
        new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1),
        new(212, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1),
        new(221, ScopeNodeKind.Warehouse, 22, AuthorizationStatus.Active, 1),
        new(201, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 7),
        new(202, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1)
    ]);

    [Fact]
    public void Two_buyers_share_entitled_catalog_X_but_B_cannot_read_A_private_order()
    {
        var actorB = Actor with { UserId = 102, ProviderSubject = "subject-102" };
        var snapshotA = Snapshot();
        var snapshotB = Snapshot(actorB);
        var entitlements = new[] { Entitlement(), Entitlement() with { Id = 6002, UserId = 102 } }.ToImmutableArray();
        var assortments = new[] { Assortment, Assortment with { Id = 7002 } }.ToImmutableArray();
        var queryA = CatalogVisibilityPolicy.Compile(Actor, snapshotA, entitlements, assortments, Now);
        var queryB = CatalogVisibilityPolicy.Compile(actorB, snapshotB, entitlements, assortments, Now);

        Assert.True(queryA.Matches([new(7001, Shop)]));
        Assert.True(queryB.Matches([new(7001, Shop)]));
        Assert.False(queryA.Matches([new(7002, Shop)]));
        Assert.False(queryB.Matches([new(7002, Shop)]));
        var privateOrder = new ResourceScope(1, 11, 111, CreatedBy: 101, AssignedUserIds: []);
        Assert.True(ScopePolicy.Compile(Actor, snapshotA, Permissions.OrderRead, Now).Matches(privateOrder));
        Assert.False(ScopePolicy.Compile(actorB, snapshotB, Permissions.OrderRead, Now).Matches(privateOrder));
    }

    [Fact]
    public void Entitlement_cannot_borrow_an_unrelated_active_membership()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Memberships = [snapshot.Memberships[0], snapshot.Memberships[0] with { Id = 502 }] };
        Assert.False(Compile(snapshot: snapshot, entitlements: [Entitlement() with { MembershipId = 502 }]).IsAllowed);
    }

    [Fact]
    public void Audience_must_be_contained_by_the_same_eligible_grant()
    {
        var snapshot = Snapshot();
        var broaderMembership = snapshot.Memberships[0] with { Scope = new(GrantScopeKind.BuyingOrganization, 1) };
        snapshot = snapshot with { Memberships = [broaderMembership] };
        foreach (var scope in new[]
        {
            new GrantScope(GrantScopeKind.BuyingDivision, 1, 11, 111),
            new(GrantScopeKind.BuyingBranch, 1, 11), new(GrantScopeKind.BuyingOrganization, 1),
            Buyer with { BranchId = 12, DivisionId = 121 }, Shop, new(GrantScopeKind.System)
        }) Assert.False(Compile(snapshot: snapshot, entitlements: [Entitlement() with { AudienceScope = scope }]).IsAllowed);

        snapshot = snapshot with { Grants = [snapshot.Grants[0] with
        {
            Scope = new(GrantScopeKind.BuyingOrganization, 1),
            Permissions = [Permissions.CatalogRead, Permissions.OrganizationWide]
        }] };
        Assert.True(Compile(snapshot: snapshot).IsAllowed);
    }

    [Fact]
    public void Every_clause_keeps_its_approval_grant_membership_versions_and_scope_nodes()
    {
        var snapshot = Snapshot();
        var secondScope = Buyer with { BranchId = 12, DivisionId = 121 };
        snapshot = snapshot with
        {
            Grants = [snapshot.Grants[0], snapshot.Grants[0] with { Id = 1002, MembershipId = 502, Scope = secondScope, Version = 8 }],
            Memberships = [snapshot.Memberships[0], snapshot.Memberships[0] with { Id = 502, Scope = secondScope, Version = 9 }]
        };
        var secondOwner = Shop with { ShopId = 22, ChannelId = 202 };
        var query = Compile(snapshot, [Entitlement(), Entitlement() with
        {
            Id = 6002, MembershipId = 502, AudienceScope = secondScope, AssortmentId = 7002, Version = 10,
            ApprovalId = 9002, ApprovalVersion = 11
        }], [Assortment, Assortment with { Id = 7002, OwnerScope = secondOwner, Version = 12 }]);
        Assert.Equal(2, query.Clauses.Length);
        var first = query.Clauses[0];
        Assert.Equal(6001, first.Entitlement.Id);
        Assert.Equal(9001, first.Entitlement.ApprovalId);
        Assert.Equal(2, first.Entitlement.ApprovalVersion);
        Assert.Equal(801, first.Entitlement.RequestedBy);
        Assert.Equal(802, first.Entitlement.ApprovedBy);
        Assert.Equal(Now.AddDays(-2), first.Entitlement.ApprovedAt);
        Assert.Equal(4, first.Grant.Version);
        Assert.Equal(5, first.Membership.Version);
        Assert.Equal(3, first.Assortment.Version);
        Assert.Equal(1001, first.Authority.GrantId);
        Assert.Equal(new long[] { 1, 2, 11, 21, 111, 201 }, first.RequiredNodes.Select(node => node.Id).Order().ToArray());
        Assert.All(first.RequiredNodes, node => Assert.True(node.Version > 0));
        var second = query.Clauses[1];
        Assert.Equal(8, second.Grant.Version);
        Assert.Equal(9, second.Membership.Version);
        Assert.Equal(10, second.Entitlement.Version);
        Assert.Equal(11, second.Entitlement.ApprovalVersion);
        Assert.Equal(12, second.Assortment.Version);
        Assert.True(query.Matches([new(7001, Shop)]));
        Assert.True(query.Matches([new(7002, secondOwner)]));
        Assert.True(query.Matches([new(7001, Shop), new(7002, secondOwner)]));
        Assert.False(query.Matches([new(7001, secondOwner), new(7002, Shop)]));
        Assert.False(query.Matches([new(7001, secondOwner)]));
        Assert.False(query.Matches([new(7002, Shop)]));
        Assert.False(query.Matches([new(7001, Shop with { ChannelId = 202 }), new(7002, Shop with { ChannelId = 202 })]));
    }

    [Fact]
    public void Multiple_complete_entitlements_keep_alternatives_and_project_distinct_assortments()
    {
        var query = Compile(entitlements: [Entitlement(), Entitlement() with { Id = 6002, ApprovalId = 9002 }]);
        Assert.Equal(2, query.Clauses.Length);
        Assert.Equal(new long[] { 7001 }, query.AssortmentIds.ToArray());
        Assert.True(query.Matches([new(7001, Shop), new(7002, Shop)]));
    }

    [Fact]
    public void Malformed_or_unapproved_entitlement_records_do_not_authorize()
    {
        var valid = Entitlement();
        var invalid = new[]
        {
            valid with { Id = 0 }, valid with { UserId = 0 }, valid with { UserId = 999 },
            valid with { MembershipId = 0 }, valid with { AssortmentId = 0 }, valid with { Version = 0 },
            valid with { ApprovalId = 0 }, valid with { ApprovalVersion = 0 }, valid with { RequestedBy = 0 },
            valid with { ApprovedBy = 0 }, valid with { RequestedBy = 802 }, valid with { ApprovedBy = 101 },
            valid with { ApprovedAt = default }, valid with { ApprovedAt = Now.AddTicks(1) },
            valid with { Status = AuthorizationStatus.Pending }, valid with { Status = AuthorizationStatus.Suspended },
            valid with { Status = AuthorizationStatus.Revoked }, valid with { EffectiveAt = default },
            valid with { EffectiveAt = Now.AddTicks(1) }, valid with { ExpiresAt = Now },
            valid with { ExpiresAt = Now.AddDays(-3) }, valid with { RevokedAt = Now },
            valid with { AudienceScope = Buyer with { OrganizationId = 0 } },
            valid with { AudienceScope = Buyer with { ShopId = 21 } },
            valid with { AudienceScope = Buyer with { BranchId = 12 } }, valid with { AudienceScope = null! }
        };
        foreach (var item in invalid) Assert.False(Compile(entitlements: [item]).IsAllowed, item.ToString());
    }

    [Fact]
    public void Missing_or_conflicting_catalog_records_fail_closed()
    {
        Assert.False(Compile(entitlements: []).IsAllowed);
        Assert.False(Compile(assortments: []).IsAllowed);
        Assert.False(Compile(entitlements: [Entitlement(), Entitlement() with { AssortmentId = 7002 }]).IsAllowed);
        Assert.False(Compile(entitlements: [Entitlement(), Entitlement()]).IsAllowed);
        Assert.False(Compile(assortments: [Assortment, Assortment with { Version = 4 }]).IsAllowed);
        Assert.False(Compile(assortments: [Assortment, Assortment]).IsAllowed);
        Assert.False(Compile(entitlements: [null!]).IsAllowed);
        Assert.False(Compile(assortments: [null!]).IsAllowed);
    }

    [Fact]
    public void Current_actor_grant_membership_and_MFA_eligibility_remain_required()
    {
        var snapshot = Snapshot();
        foreach (var invalid in new[]
        {
            snapshot with { Status = AuthorizationStatus.Suspended }, snapshot with { GrantVersion = 8 },
            snapshot with { Grants = [] }, snapshot with { Memberships = [] },
            snapshot with { Grants = [snapshot.Grants[0] with { Permissions = [Permissions.CatalogManage] }] },
            snapshot with { Grants = [snapshot.Grants[0] with { RevokedAt = Now }] },
            snapshot with { Memberships = [snapshot.Memberships[0] with { Status = AuthorizationStatus.Suspended }] },
            snapshot with { Memberships = [snapshot.Memberships[0], snapshot.Memberships[0]] }
        }) Assert.False(Compile(snapshot: invalid).IsAllowed);
        Assert.False(CatalogVisibilityPolicy.Compile(Actor with { Assurance = FactorAssurance.SingleFactor }, snapshot,
            [Entitlement()], [Assortment], Now).IsAllowed);
    }

    [Fact]
    public void Assortment_owner_requires_exact_active_fulfillment_hierarchy()
    {
        foreach (var invalid in new[]
        {
            Assortment with { Id = 0 }, Assortment with { Version = 0 },
            Assortment with { Status = AuthorizationStatus.Pending }, Assortment with { Status = AuthorizationStatus.Suspended },
            Assortment with { OwnerScope = Buyer }, Assortment with { OwnerScope = new(GrantScopeKind.System) },
            Assortment with { OwnerScope = Shop with { Kind = GrantScopeKind.CourierAssignment } },
            Assortment with { OwnerScope = Shop with { OrganizationId = 1 } },
            Assortment with { OwnerScope = Shop with { ChannelId = null } },
            Assortment with { OwnerScope = Shop with { WarehouseId = 211 } },
            Assortment with { OwnerScope = Shop with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 221 } },
            Assortment with { OwnerScope = null! }
        }) Assert.False(Compile(assortments: [invalid]).IsAllowed, invalid.ToString());
        var warehouse = Shop with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 211 };
        var query = Compile(assortments: [Assortment with { OwnerScope = warehouse }]);
        Assert.True(query.Matches([new(7001, warehouse)]));
        Assert.False(query.Matches([new(7001, Shop)]));
        Assert.Contains(query.Clauses[0].RequiredNodes, node => node.Kind == ScopeNodeKind.Warehouse && node.Id == 211);
    }

    [Theory]
    [InlineData(1)] [InlineData(11)] [InlineData(111)]
    [InlineData(2)] [InlineData(21)] [InlineData(201)]
    public void Inactive_audience_or_owner_ancestors_deny_visibility(long nodeId)
    {
        var snapshot = Snapshot() with { Directory = Directory with
        {
            Nodes = Directory.Nodes.Select(node => node.Id == nodeId
                ? node with { Status = AuthorizationStatus.Suspended } : node).ToImmutableArray()
        } };
        Assert.False(Compile(snapshot).IsAllowed);
    }

    [Fact]
    public void Malformed_product_assortment_references_never_match()
    {
        var query = Compile();
        Assert.False(query.Matches(default));
        Assert.False(query.Matches([]));
        Assert.False(query.Matches([new(0, Shop), new(7001, Shop)]));
        Assert.False(query.Matches([new(-1, Shop), new(7001, Shop)]));
        Assert.False(query.Matches([new(7001, Shop), new(7001, Shop)]));
        Assert.False(query.Matches(Enumerable.Range(7001, 129).Select(id => new CatalogListing(id, Shop)).ToImmutableArray()));
        Assert.False(query.Matches([new(7001, Shop with { BranchId = 11 })]));
        Assert.False(query.Matches([new(7001, Shop), new(7002, Shop with { OrganizationId = 3 })]));
        Assert.False(query.Matches([new(7001, Shop), new(7002, Shop with { ChannelId = null })]));
        Assert.False(query.Matches([new(7001, null!)]));
        Assert.False(query.Matches([null!]));
    }

    [Fact]
    public void Shop_reader_needs_explicit_entitlement_for_its_shop_or_contained_warehouse()
    {
        var snapshot = FulfillmentSnapshot(Shop);
        var entitlement = Entitlement() with { AudienceScope = Shop };
        var warehouse = Shop with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 211 };

        Assert.True(Compile(snapshot, [entitlement]).Matches([new(7001, Shop)]));
        var warehouseQuery = Compile(snapshot, [entitlement], [Assortment with { OwnerScope = warehouse }]);
        Assert.True(warehouseQuery.Matches([new(7001, warehouse)]));
        Assert.Contains(warehouseQuery.Clauses[0].RequiredNodes,
            node => node.Id == 211 && node.Kind == ScopeNodeKind.Warehouse);
        Assert.False(Compile(snapshot, entitlements: []).IsAllowed);
        Assert.False(Compile(snapshot, [entitlement with { MembershipId = 502 }]).IsAllowed);
    }

    [Fact]
    public void Warehouse_reader_cannot_broaden_audience_or_reach_shop_sibling_or_other_channel()
    {
        var warehouse = Shop with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 211 };
        var snapshot = FulfillmentSnapshot(warehouse);
        var entitlement = Entitlement() with { AudienceScope = warehouse };
        Assert.True(Compile(snapshot, [entitlement], [Assortment with { OwnerScope = warehouse }])
            .Matches([new(7001, warehouse)]));

        foreach (var unrelatedOwner in new[]
        {
            Shop, warehouse with { WarehouseId = 212 }, warehouse with { ChannelId = 202 },
            warehouse with { ShopId = 22, WarehouseId = 221 }
        }) Assert.False(Compile(snapshot, [entitlement], [Assortment with { OwnerScope = unrelatedOwner }]).IsAllowed);

        Assert.False(Compile(snapshot, [entitlement with { AudienceScope = Shop }],
            [Assortment with { OwnerScope = warehouse }]).IsAllowed);
    }

    [Fact]
    public void Narrow_fulfillment_audience_bounds_owner_even_when_grant_is_broader()
    {
        var warehouse = Shop with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 211 };
        var snapshot = FulfillmentSnapshot(Shop);
        var entitlement = Entitlement() with { AudienceScope = warehouse };
        Assert.True(Compile(snapshot, [entitlement], [Assortment with { OwnerScope = warehouse }]).IsAllowed);
        Assert.False(Compile(snapshot, [entitlement], [Assortment]).IsAllowed);
        Assert.False(Compile(snapshot, [entitlement],
            [Assortment with { OwnerScope = warehouse with { WarehouseId = 212 } }]).IsAllowed);
    }

    [Fact]
    public void Shop_audience_cannot_entitle_another_shop_channel_or_fulfillment_organization()
    {
        var snapshot = FulfillmentSnapshot(Shop);
        snapshot = snapshot with { Directory = Directory with { Nodes = [.. Directory.Nodes,
            new(3, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
            new(31, ScopeNodeKind.Shop, 3, AuthorizationStatus.Active, 1),
            new(301, ScopeNodeKind.Channel, 3, AuthorizationStatus.Active, 1)
        ] } };
        var entitlement = Entitlement() with { AudienceScope = Shop };
        foreach (var unrelatedOwner in new[]
        {
            Shop with { ShopId = 22 }, Shop with { ChannelId = 202 },
            Shop with { OrganizationId = 3, ShopId = 31, ChannelId = 301 }
        }) Assert.False(Compile(snapshot, [entitlement], [Assortment with { OwnerScope = unrelatedOwner }]).IsAllowed);
    }

    [Fact]
    public void System_and_courier_scopes_do_not_gain_catalog_read_eligibility()
    {
        foreach (var scope in new[] { new GrantScope(GrantScopeKind.System), Shop with { Kind = GrantScopeKind.CourierAssignment } })
            Assert.False(Compile(FulfillmentSnapshot(scope), [Entitlement() with { AudienceScope = scope }]).IsAllowed);
    }

    private static AuthorizationSnapshot FulfillmentSnapshot(GrantScope scope)
    {
        var snapshot = Snapshot();
        return snapshot with
        {
            Grants = [snapshot.Grants[0] with { TemplateId = "store-staff", Scope = scope, Permissions = [Permissions.CatalogRead] }],
            Memberships = [snapshot.Memberships[0] with { Scope = scope }]
        };
    }

    private static CatalogVisibilityQuery Compile(AuthorizationSnapshot? snapshot = null,
        ImmutableArray<CatalogEntitlement>? entitlements = null, ImmutableArray<CatalogAssortment>? assortments = null) =>
        CatalogVisibilityPolicy.Compile(Actor, snapshot ?? Snapshot(), entitlements ?? [Entitlement()],
            assortments ?? [Assortment], Now);

    private static CatalogEntitlement Entitlement() => new(6001, 101, 501, 7001, Buyer,
        AuthorizationStatus.Active, Now.AddDays(-1), null, null, 6, 801, 802, 9001, 2, Now.AddDays(-2));

    private static AuthorizationSnapshot Snapshot(ActorContext? actor = null)
    {
        actor ??= Actor;
        return new(actor.UserId, actor.GrantVersion, AuthorizationStatus.Active,
            [new(1001, actor.UserId, 501, "buyer", 1, [Permissions.CatalogRead, Permissions.OrderRead], Buyer,
                AuthorizationStatus.Active, 801, 802, "Reviewed catalog buyer", Now.AddDays(-1), Version: 4)],
            [new(501, actor.UserId, Buyer, AuthorizationStatus.Active, 802, Now.AddDays(-1), Version: 5)], Directory);
    }
}
