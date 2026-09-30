using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class PolicyApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Actor = new(101, "https://identity.example", "subject-101", 7, Now.AddMinutes(-1), FactorAssurance.MultiFactor);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 2, ShopId: 21, ChannelId: 201);
    private static readonly ScopeDirectory Directory = new([
        new(2, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
        new(21, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1), new(22, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
        new(201, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1), new(202, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1)
    ]);
    private static readonly ResourceScope Resource = new(FulfillmentOrganizationId: 2, ShopId: 21, ChannelId: 201);

    [Theory]
    [InlineData(Permissions.BankReceiptConfirm)]
    [InlineData(Permissions.PaymentVerify)]
    [InlineData(Permissions.DisbursementVerify)]
    [InlineData(Permissions.ReconciliationVerify)]
    public void Recorder_cannot_verify_even_after_receiving_new_role(string permission)
    {
        var snapshot = Snapshot([permission]);
        Assert.True(ScopePolicy.Compile(Actor, snapshot, permission, Now).Matches(Resource));
        Assert.False(ScopePolicy.Authorize(Actor, snapshot, permission, Resource, Now, new(3, 3, RecordedBy: Actor.UserId)).Allowed);
        Assert.False(ScopePolicy.Authorize(Actor, snapshot, permission, Resource, Now, new(3, 3)).Allowed);
        Assert.False(ScopePolicy.Authorize(Actor, snapshot, permission, Resource, Now).Allowed);
        Assert.True(ScopePolicy.Authorize(Actor, snapshot, permission, Resource, Now, new(3, 3, RecordedBy: 800)).Allowed);
        Assert.False(ScopePolicy.Authorize(Actor, snapshot, permission, Resource, Now, new(3, 2, RecordedBy: 800)).Allowed);
    }

    [Theory]
    [InlineData(Permissions.CreditApprove)]
    [InlineData(Permissions.RefundApprove)]
    [InlineData(Permissions.FundingApprove)]
    [InlineData(Permissions.ExpenseApprove)]
    public void Requester_cannot_approve_and_missing_history_denies(string permission)
    {
        Assert.False(ApprovalPolicy.Evaluate(Actor.UserId, permission, new(1, 1, RequestedBy: Actor.UserId)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(Actor.UserId, permission, new(1, 1)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(Actor.UserId, permission, new(1, 1, RequestedBy: 999)).Allowed);
    }

    [Fact]
    public void Refund_approver_cannot_record_disbursement_and_bank_reference_recorder_cannot_verify()
    {
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.DisbursementRecord, new(1, 1, ApprovedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(102, Permissions.DisbursementRecord, new(1, 1, ApprovedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(102, Permissions.DisbursementVerify, new(1, 1, ApprovedBy: 101, RecordedBy: 102)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(103, Permissions.DisbursementVerify, new(1, 1, ApprovedBy: 101, RecordedBy: 102)).Allowed);
    }

    [Fact]
    public void Neither_counter_nor_proposer_can_approve_stock_adjustment()
    {
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.StockAdjustApprove, new(1, 1, CountedBy: 101, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(102, Permissions.StockAdjustApprove, new(1, 1, CountedBy: 101, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(103, Permissions.StockAdjustApprove, new(1, 1, ProposedBy: 102)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(103, Permissions.StockAdjustApprove, new(1, 1, CountedBy: 101, ProposedBy: 102)).Allowed);
    }

    [Fact]
    public void Period_requires_three_distinct_actors_and_reopening_new_independent_revision()
    {
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.PeriodApprove, new(2, 2, SubmittedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(102, Permissions.PeriodApprove, new(2, 2, SubmittedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.PeriodClose, new(2, 2, SubmittedBy: 101, ApprovedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(102, Permissions.PeriodClose, new(2, 2, SubmittedBy: 101, ApprovedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(103, Permissions.PeriodClose, new(2, 2, SubmittedBy: 101, ApprovedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(103, Permissions.PeriodClose, new(2, 2, SubmittedBy: 101, ApprovedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(104, Permissions.PeriodReopenApprove, new(3, 3, RequestedBy: 104, ClosedBy: 103, PreviousRevision: 2)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(105, Permissions.PeriodReopenApprove, new(2, 2, RequestedBy: 104, ClosedBy: 103, PreviousRevision: 2)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(105, Permissions.PeriodReopenApprove, new(3, 3, RequestedBy: 104, PreviousRevision: 2)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(105, Permissions.PeriodReopenApprove, new(3, 3, RequestedBy: 104, ClosedBy: 103, PreviousRevision: 2)).Allowed);
    }

    [Fact]
    public void Self_beneficiary_and_proposer_cannot_activate_grant()
    {
        var snapshot = Snapshot([Permissions.AccessApprove], [Permissions.PaymentRecord]);
        var target = Proposed([Permissions.PaymentRecord]);
        Assert.True(AccessGrantPolicy.CanApprove(Actor, snapshot, target, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, snapshot, target with { UserId = Actor.UserId }, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, snapshot, target with { RequestedBy = Actor.UserId }, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, snapshot, target with { RequestedBy = 0 }, Now).Allowed);
    }

    [Fact]
    public void Delegation_is_explicit_and_all_permissions_and_scope_must_fit_one_authority_grant()
    {
        var target = Proposed([Permissions.PaymentRecord]);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, Snapshot([Permissions.PaymentRecord]), target, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, Snapshot([Permissions.AccessApprove]), target, Now).Allowed);
        var authority = Snapshot([Permissions.AccessApprove], [Permissions.PaymentRecord]);
        Assert.True(AccessGrantPolicy.CanApprove(Actor, authority, target, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, authority, target with { Permissions = [Permissions.PaymentVerify] }, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, authority, target with { Scope = Shop with { ShopId = 22 } }, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApprove(Actor, authority, target with { DelegablePermissions = [Permissions.PaymentVerify] }, Now).Allowed);
        var split = authority with { Grants = [authority.Grants[0], authority.Grants[0] with { Id = 1002, DelegablePermissions = [Permissions.PaymentVerify] }] };
        Assert.False(AccessGrantPolicy.CanApprove(Actor, split, target with { Permissions = [Permissions.PaymentRecord, Permissions.PaymentVerify] }, Now).Allowed);
        var corruptIds = authority with { Grants = [authority.Grants[0], authority.Grants[0] with { DelegablePermissions = [Permissions.PaymentVerify] }] };
        Assert.False(AccessGrantPolicy.CanApprove(Actor, corruptIds, target with { Permissions = [Permissions.PaymentVerify] }, Now).Allowed);
    }

    [Fact]
    public void Technical_system_authority_does_not_delegate_business_grants()
    {
        var system = new GrantScope(GrantScopeKind.System);
        var snapshot = Snapshot([Permissions.AccessApprove], [Permissions.PaymentRecord]);
        snapshot = snapshot with
        {
            Grants = [snapshot.Grants[0] with { TemplateId = "system-admin", Scope = system }],
            Memberships = [snapshot.Memberships[0] with { Scope = system }]
        };
        Assert.False(AccessGrantPolicy.CanApprove(Actor, snapshot, Proposed([Permissions.PaymentRecord]), Now).Allowed);
    }

    [Fact]
    public void Membership_approval_requires_named_scope_owner_and_another_actor()
    {
        var snapshot = Snapshot([Permissions.MembershipApprove]);
        var proposed = new Membership(555, 202, Shop, AuthorizationStatus.Pending, 0, Now);
        Assert.True(AccessGrantPolicy.CanApproveMembership(Actor, snapshot, proposed, 901, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApproveMembership(Actor, snapshot, proposed with { UserId = Actor.UserId }, 901, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApproveMembership(Actor, snapshot, proposed, Actor.UserId, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApproveMembership(Actor, snapshot, proposed with { Scope = Shop with { ShopId = 22 } }, 901, Now).Allowed);
        Assert.False(AccessGrantPolicy.CanApproveMembership(Actor, Snapshot([Permissions.AccessApprove]), proposed, 901, Now).Allowed);
    }

    private static AuthorizationSnapshot Snapshot(ImmutableArray<string> permissions, ImmutableArray<string> delegable = default) => new(
        Actor.UserId, Actor.GrantVersion, AuthorizationStatus.Active,
        [new(1001, Actor.UserId, 501, "finance", 1, permissions, Shop, AuthorizationStatus.Active, 801, 802,
            "Reviewed delegated responsibilities", Now.AddDays(-1), DelegablePermissions: delegable)],
        [new(501, Actor.UserId, Shop, AuthorizationStatus.Active, 802, Now.AddDays(-1))], Directory);

    private static PermissionGrant Proposed(ImmutableArray<string> permissions) => new(1002, 202, 502, "finance", 1,
        permissions, Shop, AuthorizationStatus.Pending, 901, 0, "Independent business request", Now);
}
