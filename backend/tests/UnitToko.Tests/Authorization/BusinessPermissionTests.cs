using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class BusinessPermissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Actor = new(101, "https://identity.example", "subject-101", 7,
        Now.AddMinutes(-1), FactorAssurance.MultiFactor);
    private static readonly GrantScope Buyer = new(GrantScopeKind.BuyerOwned, 1, 11, 111);
    private static readonly GrantScope Branch = new(GrantScopeKind.BuyingBranch, 1, 11);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 2, ShopId: 21, ChannelId: 201);
    private static readonly ScopeDirectory Directory = new([
        new(1, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
        new(11, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 1),
        new(111, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1),
        new(2, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
        new(21, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
        new(201, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1)
    ]);
    private static readonly ResourceScope Order = new(1, 11, 111, 2, 21, null, 201, Actor.UserId, [Actor.UserId]);

    [Fact]
    public void Buyer_spending_and_store_acceptance_are_separate_explicit_authorities()
    {
        var buyer = Snapshot("buyer", Branch, [Permissions.BranchPic, Permissions.OrderSpendApprove]);
        Assert.True(ScopePolicy.Compile(Actor, buyer, Permissions.OrderSpendApprove, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, buyer, Permissions.OrderAccept, Now).IsAllowed);
        var store = Snapshot("store-head", Shop, [Permissions.OrderAccept]);
        Assert.True(ScopePolicy.Compile(Actor, store, Permissions.OrderAccept, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, store, Permissions.OrderSpendApprove, Now).IsAllowed);
        Assert.DoesNotContain(Permissions.OrderSpendApprove, RoleTemplates.Templates["buyer"].Permissions);
        Assert.DoesNotContain(Permissions.OrderAccept, RoleTemplates.Templates["store-head"].Permissions);
    }

    [Fact]
    public void Store_proposal_cannot_consent_and_buyer_consent_cannot_propose()
    {
        var staff = Snapshot("store-staff", Shop, [Permissions.SubstitutionPropose]);
        var buyer = Snapshot("buyer", Buyer, [Permissions.SubstitutionAccept]);
        Assert.True(ScopePolicy.Compile(Actor, staff, Permissions.SubstitutionPropose, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, staff, Permissions.SubstitutionAccept, Now).IsAllowed);
        Assert.True(ScopePolicy.Compile(Actor, buyer, Permissions.SubstitutionAccept, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, buyer, Permissions.SubstitutionPropose, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, buyer, Permissions.SubstitutionAccept, Now)
            .Matches(Order with { CreatedBy = 999, AssignedUserIds = [] }));
    }

    [Fact]
    public void Fulfillment_stock_supplier_and_read_only_scopes_are_explicit()
    {
        foreach (var permission in new[] { Permissions.StockConvert, Permissions.ReservationRelease,
            Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove })
        {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse },
                Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer), permission, Now).IsAllowed);
        }
        foreach (var permission in new[] { Permissions.HistoryRead, Permissions.ReportRead })
        {
            Assert.Contains(GrantScopeKind.BuyerOwned, Permissions.Find(permission)!.AllowedScopes);
            Assert.Contains(GrantScopeKind.FulfillmentShop, Permissions.Find(permission)!.AllowedScopes);
            Assert.DoesNotContain(GrantScopeKind.System, Permissions.Find(permission)!.AllowedScopes);
            Assert.DoesNotContain(GrantScopeKind.CourierAssignment, Permissions.Find(permission)!.AllowedScopes);
            var baseGrant = Snapshot("buyer", Buyer);
            var auditor = baseGrant with
            {
                Grants = [baseGrant.Grants[0] with { Permissions = [permission] }]
            };
            Assert.True(ScopePolicy.Compile(Actor, auditor, permission, Now).Matches(Order));
            foreach (var other in Permissions.Catalog.Keys.Where(name => name != permission))
                Assert.False(ScopePolicy.Compile(Actor, auditor, other, Now).IsAllowed);
        }
    }

    [Fact]
    public void Suggestions_do_not_change_v1_materialization_or_legacy_snapshot()
    {
        Assert.Equal(1, RoleTemplates.CurrentVersion);
        Assert.Equal(new[] { Permissions.SubstitutionAccept }, RoleTemplates.Suggestions["buyer"].ToArray());
        Assert.Equal(new[] { Permissions.SubstitutionPropose, Permissions.StockConvert, Permissions.ReservationRelease,
            Permissions.SupplierChangePropose }, RoleTemplates.Suggestions["store-staff"].ToArray());
        Assert.Equal(new[] { Permissions.OrderAccept, Permissions.SupplierChangeApprove }, RoleTemplates.Suggestions["store-head"].ToArray());
        Assert.Empty(RoleTemplates.Suggestions["finance"]);
        Assert.Empty(RoleTemplates.Suggestions["courier"]);
        Assert.Empty(RoleTemplates.Suggestions["system-admin"]);
        Assert.DoesNotContain(Permissions.SubstitutionAccept, RoleTemplates.Materialize("buyer", [], []).Permissions);
        Assert.DoesNotContain(Permissions.SubstitutionPropose, RoleTemplates.Materialize("store-staff", [], []).Permissions);
        Assert.NotNull(Permissions.Find(Permissions.OrderApprove));
        Assert.NotNull(Permissions.Find(Permissions.OrderSubstitute));
        var legacy = Snapshot("buyer", Buyer);
        Assert.True(ScopePolicy.Compile(Actor, legacy, Permissions.OrderSubstitute, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, legacy, Permissions.SubstitutionAccept, Now).IsAllowed);
    }

    [Fact]
    public void Approval_evidence_checks_origin_and_exact_revision()
    {
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.OrderSpendApprove));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.OrderAccept));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.SubstitutionAccept));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.SupplierChangeApprove));
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.OrderSpendApprove, new(2, 2, RequestedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.OrderSpendApprove, new(2, 2, RequestedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(2, 2, ProposedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(2, 2, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(2, 1, RequestedBy: 101, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(2, 2, RequestedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(2, 2, RequestedBy: 101, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.OrderAccept, new(2, 2)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.OrderAccept, new(2, 2, RequestedBy: 101)).Allowed);
        Assert.NotNull(Permissions.Find(Permissions.OrderSpendApprove)!.Assurance.MaximumAge);
        Assert.NotNull(Permissions.Find(Permissions.SupplierChangeApprove)!.Assurance.MaximumAge);
    }

    private static AuthorizationSnapshot Snapshot(string template, GrantScope scope, string[]? additions = null)
    {
        var permissions = RoleTemplates.Materialize(template, [], additions ?? []).Permissions;
        return new(Actor.UserId, Actor.GrantVersion, AuthorizationStatus.Active,
            [new(1001, Actor.UserId, 501, template, 1, permissions, scope, AuthorizationStatus.Active,
                801, 802, "Reviewed business duties", Now.AddDays(-1))],
            [new(501, Actor.UserId, scope, AuthorizationStatus.Active, 802, Now.AddDays(-1))], Directory);
    }
}
