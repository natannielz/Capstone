using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Identity;

public sealed class StepUpEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Actor = new(1000, "https://approved.co.id/", "subject", 4, Now.AddMinutes(-10), FactorAssurance.MultiFactor);
    private static readonly GrantScope Scope = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    private static AuthorizationSnapshot Snapshot() => new(Actor.UserId, 4, AuthorizationStatus.Active,
        [new(1, Actor.UserId, 1, "finance", RoleTemplates.CurrentVersion, [Permissions.RefundApprove], Scope,
            AuthorizationStatus.Active, 998, 999, "Independent approval", Now.AddDays(-1))],
        [new(1, Actor.UserId, Scope, AuthorizationStatus.Active, 999, Now.AddDays(-1))],
        new([new(100, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
            new(101, ScopeNodeKind.Shop, 100, AuthorizationStatus.Active, 1),
            new(102, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1),
            new(103, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)]));

    [Fact]
    public void Eligibility_can_request_a_factor_but_never_returns_business_authority_or_changes_original_factor_time()
    {
        var snapshot = Snapshot();
        Assert.True(ScopePolicy.CanRequestStepUp(Actor, snapshot, Permissions.RefundApprove, Now));
        Assert.Equal("step_up_required", ScopePolicy.Compile(Actor, snapshot, Permissions.RefundApprove, Now).DenialCode);
        Assert.Equal(Now.AddMinutes(-10), Actor.AuthenticatedAt);
        Assert.False(ScopePolicy.CanRequestStepUp(Actor, snapshot, Permissions.OrderRead, Now));
        Assert.False(ScopePolicy.CanRequestStepUp(Actor, snapshot, "unknown.permission", Now));
        Assert.False(ScopePolicy.CanRequestStepUp(Actor with { Assurance = FactorAssurance.SingleFactor }, snapshot, Permissions.RefundApprove, Now));
        Assert.False(ScopePolicy.CanRequestStepUp(Actor with { GrantVersion = 3 }, snapshot, Permissions.RefundApprove, Now));
    }

    [Theory]
    [InlineData("revoked-grant")]
    [InlineData("expired-membership")]
    [InlineData("duplicate-grant")]
    [InlineData("foreign-membership")]
    [InlineData("suspended-parent")]
    [InlineData("wrong-parent")]
    public void Eligibility_retains_complete_current_grants_memberships_and_scope_ancestry(string variation)
    {
        var snapshot = Snapshot();
        snapshot = variation switch
        {
            "revoked-grant" => snapshot with { Grants = [snapshot.Grants[0] with { Status = AuthorizationStatus.Revoked }] },
            "expired-membership" => snapshot with { Memberships = [snapshot.Memberships[0] with { ExpiresAt = Now }] },
            "duplicate-grant" => snapshot with { Grants = [snapshot.Grants[0], snapshot.Grants[0]] },
            "foreign-membership" => snapshot with { Memberships = [snapshot.Memberships[0] with { Scope = Scope with { ChannelId = 104 } }] },
            "suspended-parent" => snapshot with { Directory = new([.. snapshot.Directory.Nodes.Select(node => node.Id == 101 ? node with { Status = AuthorizationStatus.Suspended } : node)]) },
            "wrong-parent" => snapshot with { Directory = new([.. snapshot.Directory.Nodes.Select(node => node.Id == 102 ? node with { ParentId = 105 } : node)]) },
            _ => throw new InvalidOperationException()
        };
        Assert.False(ScopePolicy.CanRequestStepUp(Actor, snapshot, Permissions.RefundApprove, Now));
    }
}
