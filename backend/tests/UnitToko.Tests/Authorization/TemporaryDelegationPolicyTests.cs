using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class TemporaryDelegationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly GrantScope Scope = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static readonly ScopeDirectory Directory = new([new(100, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
        new(101, ScopeNodeKind.Shop, 100, AuthorizationStatus.Active, 1), new(103, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)]);
    private static readonly PermissionGrant Source = new(20, 10, 30, "store-staff", 1, [Permissions.StockRead, Permissions.StockCount], Scope,
        AuthorizationStatus.Active, 80, 90, "Approved original source", Now.AddDays(-1), Now.AddDays(2), DelegablePermissions: [Permissions.StockRead]);
    private static readonly Membership SourceMembership = new(30, 10, Scope, AuthorizationStatus.Active, 90, Now.AddDays(-2), Now.AddDays(2));
    private static readonly Membership Recipient = new(31, 11, Scope, AuthorizationStatus.Active, 90, Now.AddDays(-2), Now.AddDays(1));
    private static readonly TemporaryDelegationRequest Request = new(20, 1, 11, 31, Scope, [Permissions.StockRead], Now, Now.AddHours(2));
    private static bool Allowed(TemporaryDelegationRequest request, PermissionGrant? source = null, Membership? recipient = null, DateTimeOffset? now = null) =>
        TemporaryDelegationPolicy.SourceAllows(request, source ?? Source, SourceMembership, recipient ?? Recipient, Directory, 10, now ?? Now);

    [Fact]
    public void Delegation_is_exact_explicit_intersection_and_never_expands_when_template_grows() {
        Assert.True(Allowed(Request));
        Assert.False(Allowed(Request with { Permissions = [Permissions.StockCount] }));
        Assert.False(Allowed(Request, Source with { Permissions = [Permissions.StockRead], DelegablePermissions = [Permissions.StockCount] }));
        Assert.False(Allowed(Request with { Scope = Scope with { ShopId = 999 } }));
        Assert.False(Allowed(Request with { SourceVersion = 2 }));
        Assert.Equal(new[] { Permissions.StockRead }, Request.Permissions.ToArray());
    }
    [Fact]
    public void Narrower_target_must_exist_be_active_and_belong_to_the_exact_parent() {
        var request = Request with { Scope = Scope with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 102 } };
        var node = new ScopeNode(102, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1);
        bool Allows(ScopeDirectory directory) => TemporaryDelegationPolicy.SourceAllows(request, Source, SourceMembership, Recipient, directory, 10, Now);
        Assert.True(Allows(new(Directory.Nodes.Add(node))));
        Assert.False(Allows(Directory));
        Assert.False(Allows(new(Directory.Nodes.Add(node with { Status = AuthorizationStatus.Suspended }))));
        Assert.False(Allows(new(Directory.Nodes.Add(node with { ParentId = 999 }))));
    }
    [Fact]
    public void Source_recipient_and_exact_end_bound_every_delegation() {
        Assert.False(Allowed(Request with { ExpiresAt = Now.AddDays(3) }));
        Assert.False(Allowed(Request with { ExpiresAt = Now.AddDays(2) }));
        Assert.False(Allowed(Request with { EffectiveAt = Source.EffectiveAt.AddDays(-1) }));
        Assert.False(Allowed(Request, recipient: Recipient with { Status = AuthorizationStatus.Suspended }));
        Assert.False(Allowed(Request, Source with { Status = AuthorizationStatus.Revoked, RevokedAt = Now }));
        Assert.False(Allowed(Request, now: Request.ExpiresAt));
    }
    [Fact]
    public void Missing_runtime_proof_chain_and_same_account_do_not_grant_authority() {
        var derived = Source with { TemplateId = TemporaryDelegationPolicy.TemplateId, DelegablePermissions = [],
            TemporaryAuthority = new(Source.Id, 44, Source.RequestedBy, 1, Now, Source.ExpiresAt!.Value) };
        Assert.False(Allowed(Request, derived));
        Assert.False(Allowed(Request with { BeneficiaryUserId = 10 }, recipient: Recipient with { UserId = 10 }));
        Assert.False(ScopePolicy.IsGrantActive(derived with { TemporaryAuthority = null }, 10, Now));
        Assert.False(TemporaryDelegationPolicy.HasCurrentProof(derived, Source.ExpiresAt!.Value));
        Assert.False(TemporaryDelegationPolicy.HasCurrentProof(derived, Now.AddTicks(-1)));
        Assert.False(TemporaryDelegationPolicy.HasCurrentProof(derived with { TemporaryAuthority = derived.TemporaryAuthority! with { Deadline = Source.ExpiresAt.Value.AddSeconds(1) } }, Now));
        Assert.DoesNotContain("TemporaryAuthority", System.Text.Json.JsonSerializer.Serialize(derived));
    }
    [Theory]
    [InlineData(GrantScopeKind.BuyerOwned)]
    [InlineData(GrantScopeKind.CourierAssignment)]
    public void Actor_owned_scopes_require_real_task_handover_before_delegation(GrantScopeKind kind) =>
        Assert.Throws<DomainValidationException>(() => TemporaryDelegationPolicy.Validate(Request with { Scope = Scope with { Kind = kind }, Permissions = [Permissions.ShipmentRead] }));
    [Fact]
    public void Empty_duplicate_or_unbounded_period_is_refused() {
        Assert.Throws<DomainValidationException>(() => TemporaryDelegationPolicy.Validate(Request with { Permissions = ImmutableArray<string>.Empty }));
        Assert.Throws<DomainValidationException>(() => TemporaryDelegationPolicy.Validate(Request with { Permissions = [Permissions.StockRead, Permissions.StockRead] }));
        Assert.Throws<DomainValidationException>(() => TemporaryDelegationPolicy.Validate(Request with { ExpiresAt = Request.EffectiveAt }));
        Assert.Throws<DomainValidationException>(() => TemporaryDelegationPolicy.Validate(Request with { EffectiveAt = Now.AddTicks(1) }));
    }
}
