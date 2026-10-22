using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class PolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Actor = new(101, "https://identity.example", "subject-101", 7, Now.AddMinutes(-1), FactorAssurance.MultiFactor);
    private static readonly GrantScope Buyer = new(GrantScopeKind.BuyerOwned, 1, 11, 111);
    private static readonly GrantScope Branch = new(GrantScopeKind.BuyingBranch, 1, 11);
    private static readonly GrantScope Organization = new(GrantScopeKind.BuyingOrganization, 1);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 2, ShopId: 21, ChannelId: 201);
    private static readonly GrantScope Courier = new(GrantScopeKind.CourierAssignment, 2, ShopId: 21, ChannelId: 201);
    private static readonly GrantScope SystemScope = new(GrantScopeKind.System);
    private static readonly ScopeDirectory Directory = new([
        new(1, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
        new(3, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
        new(11, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 2),
        new(12, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 1),
        new(31, ScopeNodeKind.Branch, 3, AuthorizationStatus.Active, 1),
        new(111, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1),
        new(112, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1),
        new(121, ScopeNodeKind.Division, 12, AuthorizationStatus.Active, 1),
        new(2, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
        new(21, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
        new(22, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
        new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1),
        new(221, ScopeNodeKind.Warehouse, 22, AuthorizationStatus.Active, 1),
        new(201, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1),
        new(202, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1)
    ]);
    private static readonly ResourceScope Order = new(1, 11, 111, 2, 21, 211, 201, 101, [101]);

    [Theory]
    [InlineData("buyer", Permissions.OrderSubmit, Permissions.StockAdjustApprove)]
    [InlineData("store-staff", Permissions.StockReceive, Permissions.PaymentVerify)]
    [InlineData("store-head", Permissions.CatalogManage, Permissions.AccessApprove)]
    [InlineData("courier", Permissions.DeliveryAttempt, Permissions.InvoiceRead)]
    [InlineData("system-admin", Permissions.AccessPropose, Permissions.PaymentVerify)]
    public void Every_nonempty_template_has_a_scoped_allowance_and_denied_counterpart(string template, string allowed, string denied)
    {
        var scope = template switch { "buyer" => Buyer, "courier" => Courier, "system-admin" => SystemScope, _ => Shop };
        var snapshot = Snapshot(template, scope);
        var resource = template == "system-admin" ? new ResourceScope(IsAdministrative: true) : Order;
        Assert.True(ScopePolicy.Compile(Actor, snapshot, allowed, Now).Matches(resource));
        Assert.False(ScopePolicy.Compile(Actor, snapshot, denied, Now).Matches(resource));
        Assert.False(ScopePolicy.Compile(Actor, snapshot, allowed, Now).Matches(template == "system-admin" ? Order : resource with { ChannelId = 202, BranchId = 12, DivisionId = 121 }));
    }

    [Fact]
    public void Fifteen_templates_and_four_finance_packs_have_no_wildcard_or_automatic_verifier()
    {
        Assert.Equal(15, RoleTemplates.Templates.Count);
        Assert.Equal(4, RoleTemplates.FinancePacks.Count);
        Assert.Empty(RoleTemplates.Templates["finance"].Permissions);
        Assert.Empty(RoleTemplates.Materialize("finance", [], []).Permissions);
        Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop), Permissions.InvoiceRead, Now).IsAllowed);
        foreach (var template in RoleTemplates.Templates.Values)
        {
            Assert.Equal(1, template.Version);
            foreach (var permission in template.Permissions) Assert.NotNull(Permissions.Find(permission));
            Assert.DoesNotContain(Permissions.PaymentVerify, template.Permissions);
        }
        foreach (var pack in RoleTemplates.FinancePacks)
        {
            var snapshot = Snapshot("finance", Shop, [pack.Key]);
            foreach (var permission in pack.Value) Assert.True(ScopePolicy.Compile(Actor, snapshot, permission, Now).Matches(Order), permission);
            Assert.False(ScopePolicy.Compile(Actor, snapshot, Permissions.PaymentVerify, Now).IsAllowed);
        }
        Assert.All(Permissions.Catalog.Keys, permission => Assert.DoesNotContain('*', permission));
        Assert.Throws<DomainValidationException>(() => RoleTemplates.Materialize("finance", ["all"], []));
        Assert.Throws<DomainValidationException>(() => RoleTemplates.Materialize("buyer", ["billing"], []));
        Assert.Throws<DomainValidationException>(() => RoleTemplates.Materialize("finance", [], ["payment.*"]));
    }

    [Fact]
    public void Every_template_permission_is_valid_for_its_intended_scope()
    {
        foreach (var template in RoleTemplates.Templates.Values)
        {
            var scope = template.Id switch { "buyer" => Buyer, "branch-approver" => Branch, "courier" => Courier, "system-admin" => SystemScope, _ => Shop };
            var snapshot = Snapshot(template.Id, scope);
            var resource = template.Id == "system-admin" ? new ResourceScope(IsAdministrative: true) : Order;
            foreach (var permission in template.Permissions) Assert.True(ScopePolicy.Compile(Actor, snapshot, permission, Now).Matches(resource), $"{template.Id}: {permission}");
        }
    }

    [Fact]
    public void Buyer_requires_self_or_explicit_assignment_and_exact_membership_dimensions()
    {
        var query = ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer), Permissions.OrderRead, Now);
        Assert.True(query.Matches(Order with { AssignedUserIds = [], CreatedBy = Actor.UserId }));
        Assert.True(query.Matches(Order with { CreatedBy = 999, AssignedUserIds = [Actor.UserId] }));
        Assert.False(query.Matches(Order with { CreatedBy = 999, AssignedUserIds = [] }));
        Assert.False(query.Matches(Order with { DivisionId = 112 }));
        Assert.False(query.Matches(Order with { BranchId = 12, DivisionId = 121 }));
        Assert.False(query.Matches(Order with { BuyingOrganizationId = 3, BranchId = 31, DivisionId = null }));
    }

    [Fact]
    public void Pic_names_exact_branch_and_org_wide_is_a_separate_explicit_approved_grant()
    {
        var unnamed = Snapshot("buyer", Branch);
        Assert.False(ScopePolicy.Compile(Actor, unnamed, Permissions.OrderRead, Now).IsAllowed);
        var named = Snapshot("buyer", Branch, extras: [Permissions.BranchPic]);
        var query = ScopePolicy.Compile(Actor, named, Permissions.OrderRead, Now);
        Assert.True(query.Matches(Order with { CreatedBy = 999, AssignedUserIds = [], DivisionId = 112 }));
        Assert.False(query.Matches(Order with { BranchId = 12, DivisionId = 121 }));
        var org = Snapshot("buyer", Organization);
        Assert.False(ScopePolicy.Compile(Actor, org, Permissions.OrderRead, Now).IsAllowed);
        org = Snapshot("buyer", Organization, extras: [Permissions.OrganizationWide]);
        Assert.True(ScopePolicy.Compile(Actor, org, Permissions.OrderRead, Now).Matches(Order with { BranchId = 12, DivisionId = 121, CreatedBy = 999, AssignedUserIds = [] }));
    }

    [Fact]
    public void Complete_grants_are_OR_but_scope_parts_cannot_mix()
    {
        var first = Snapshot("store-staff", Shop);
        var second = Snapshot("store-staff", Shop with { ShopId = 22, ChannelId = 202 });
        var combined = first with
        {
            Grants = [first.Grants[0], second.Grants[0] with { Id = 1002, MembershipId = 502 }],
            Memberships = [first.Memberships[0], second.Memberships[0] with { Id = 502 }]
        };
        var query = ScopePolicy.Compile(Actor, combined, Permissions.StockRead, Now);
        Assert.Equal(2, query.Clauses.Length);
        Assert.True(query.Matches(Order));
        Assert.True(query.Matches(Order with { ShopId = 22, WarehouseId = 221, ChannelId = 202 }));
        Assert.False(query.Matches(Order with { ChannelId = 202 }));
        Assert.False(query.Matches(Order with { ShopId = 22, WarehouseId = 221 }));
    }

    [Fact]
    public void Courier_must_be_assigned_and_fulfillment_org_is_distinct_from_buying_org()
    {
        var query = ScopePolicy.Compile(Actor, Snapshot("courier", Courier), Permissions.ShipmentRead, Now);
        Assert.True(query.Matches(Order));
        Assert.True(query.Matches(Order with { BuyingOrganizationId = 3, BranchId = 31, DivisionId = null }));
        Assert.False(query.Matches(Order with { AssignedUserIds = [], CreatedBy = Actor.UserId }));
        Assert.False(query.Matches(Order with { FulfillmentOrganizationId = 1 }));
        Assert.False(query.Matches(Order with { ShopId = 22, WarehouseId = 221 }));
    }

    [Fact]
    public void Inactive_or_unrelated_hierarchy_is_denied_even_for_orgwide_grants()
    {
        var snapshot = Snapshot("buyer", Organization, extras: [Permissions.OrganizationWide]);
        var query = ScopePolicy.Compile(Actor, snapshot, Permissions.OrderRead, Now);
        Assert.False(query.Matches(Order with { BranchId = 31, DivisionId = null }));
        Assert.False(query.Matches(Order with { DivisionId = 121 }));
        Assert.False(query.Matches(Order with { WarehouseId = 221 }));
        var disabled = snapshot with { Directory = Directory with { Nodes = Directory.Nodes.Select(node => node.Id == 11 ? node with { Status = AuthorizationStatus.Suspended } : node).ToImmutableArray() } };
        Assert.False(ScopePolicy.Compile(Actor, disabled, Permissions.OrderRead, Now).Matches(Order));
        Assert.True(ScopePolicy.Compile(Actor, disabled, Permissions.OrderRead, Now).Matches(Order with { BranchId = 12, DivisionId = 121 }));
    }

    [Fact]
    public void Suspension_revocation_expiry_future_effect_and_stale_actor_all_fail_closed()
    {
        var snapshot = Snapshot("buyer", Buyer);
        var invalid = new[]
        {
            snapshot with { Status = AuthorizationStatus.Suspended }, snapshot with { GrantVersion = 8 },
            snapshot with { Grants = [snapshot.Grants[0] with { Status = AuthorizationStatus.Pending }] },
            snapshot with { Grants = [snapshot.Grants[0] with { EffectiveAt = Now.AddSeconds(1) }] },
            snapshot with { Grants = [snapshot.Grants[0] with { ExpiresAt = Now }] },
            snapshot with { Grants = [snapshot.Grants[0] with { RevokedAt = Now }] },
            snapshot with { Grants = [snapshot.Grants[0] with { ApprovedBy = Actor.UserId }] },
            snapshot with { Grants = [snapshot.Grants[0] with { ApprovedBy = snapshot.Grants[0].RequestedBy }] },
            snapshot with { Memberships = [snapshot.Memberships[0] with { Status = AuthorizationStatus.Suspended }] },
            snapshot with { Memberships = [snapshot.Memberships[0] with { ExpiresAt = Now }] },
            snapshot with { Memberships = [snapshot.Memberships[0] with { ApprovedBy = Actor.UserId }] }
        };
        foreach (var item in invalid) Assert.False(ScopePolicy.Compile(Actor, item, Permissions.OrderRead, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor with { UserId = 999 }, snapshot, Permissions.OrderRead, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddSeconds(1) }, snapshot, Permissions.OrderRead, Now).IsAllowed);
    }

    [Fact]
    public void Membership_cannot_be_borrowed_from_another_branch_or_identity()
    {
        var snapshot = Snapshot("buyer", Branch, extras: [Permissions.BranchPic]);
        Assert.False(ScopePolicy.Compile(Actor, snapshot with { Memberships = [snapshot.Memberships[0] with { Scope = Branch with { BranchId = 12 } }] }, Permissions.OrderRead, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, snapshot with { Memberships = [snapshot.Memberships[0] with { UserId = 999 }] }, Permissions.OrderRead, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, snapshot with { Memberships = [snapshot.Memberships[0], snapshot.Memberships[0]] }, Permissions.OrderRead, Now).IsAllowed);
    }

    [Fact]
    public void Null_scope_never_means_all_and_system_admin_cannot_read_transaction_content()
    {
        var snapshot = Snapshot("buyer", new(GrantScopeKind.BuyingOrganization));
        Assert.False(ScopePolicy.Compile(Actor, snapshot, Permissions.OrderRead, Now).IsAllowed);
        var admin = Snapshot("system-admin", SystemScope);
        Assert.True(ScopePolicy.Compile(Actor, admin, Permissions.AccessAuditRead, Now).Matches(new(IsAdministrative: true)));
        Assert.False(ScopePolicy.Compile(Actor, admin, Permissions.AccessAuditRead, Now).Matches(Order with { IsAdministrative = true }));
        Assert.False(ScopePolicy.Compile(Actor, admin, Permissions.OrderRead, Now).Matches(Order));
        var illicit = admin with { Grants = [admin.Grants[0] with { Permissions = [Permissions.PaymentVerify] }] };
        Assert.False(ScopePolicy.Compile(Actor, illicit, Permissions.PaymentVerify, Now).IsAllowed);
    }

    [Fact]
    public void Stored_snapshot_does_not_inherit_new_template_permissions_and_legacy_finance_is_not_union()
    {
        var snapshot = Snapshot("store-head", Shop);
        snapshot = snapshot with { Grants = [snapshot.Grants[0] with { Permissions = [Permissions.CatalogRead] }] };
        Assert.True(ScopePolicy.Compile(Actor, snapshot, Permissions.CatalogRead, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, snapshot, Permissions.CatalogManage, Now).IsAllowed);
        Assert.Null(RoleTemplates.MapLegacy("customer"));
        Assert.Null(RoleTemplates.MapLegacy("finance"));
        foreach (var pair in new[] { ("laporan", "reporting"), ("penagihan", "billing"), ("pimpinan", "approval"), ("akuntansi", "accounting") })
        {
            var mapping = Assert.IsType<LegacyRoleMapping>(RoleTemplates.MapLegacy(pair.Item1));
            Assert.Equal("finance", mapping.TemplateId);
            Assert.Equal(pair.Item2, Assert.Single(mapping.FinancePacks));
            Assert.True(mapping.RequiresApprovedMembership);
        }
    }

    [Fact]
    public void Verified_assurance_and_provider_authentication_age_are_explicitly_enforced()
    {
        var snapshot = Snapshot("finance", Shop, extras: [Permissions.AccessApprove]);
        Assert.Equal("step_up_required", ScopePolicy.Compile(Actor with { Assurance = FactorAssurance.Unknown }, snapshot, Permissions.AccessApprove, Now).DenialCode);
        Assert.Equal("step_up_required", ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, snapshot, Permissions.AccessApprove, Now).DenialCode);
        Assert.True(ScopePolicy.Compile(Actor, snapshot, Permissions.AccessApprove, Now).IsAllowed);
    }

    private static AuthorizationSnapshot Snapshot(string template, GrantScope scope, string[]? packs = null, string[]? extras = null)
    {
        var materialized = RoleTemplates.Materialize(template, packs ?? [], extras ?? []);
        return new(Actor.UserId, Actor.GrantVersion, AuthorizationStatus.Active,
            [new(1001, Actor.UserId, 501, template, materialized.Version, materialized.Permissions, scope,
                AuthorizationStatus.Active, 801, 802, "Reviewed business duties", Now.AddDays(-1))],
            [new(501, Actor.UserId, scope, AuthorizationStatus.Active, 802, Now.AddDays(-1))], Directory);
    }
}
