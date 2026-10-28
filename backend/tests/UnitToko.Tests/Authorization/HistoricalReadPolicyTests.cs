using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class HistoricalReadPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Reader = new(101, "https://identity.example", "reader-101", 7,
        Now.AddMinutes(-1), FactorAssurance.MultiFactor);
    private static readonly ActorContext Approver = Reader with { UserId = 802, ProviderSubject = "approver-802" };
    private static readonly GrantScope Organization = new(GrantScopeKind.BuyingOrganization, 1);
    private static readonly GrantScope Branch = new(GrantScopeKind.BuyingBranch, 1, 11);
    private static readonly GrantScope Division = new(GrantScopeKind.BuyingDivision, 1, 11, 111);
    private static readonly ScopeDirectory Directory = new([
        new(1, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 2),
        new(11, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 3),
        new(12, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 4),
        new(111, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 5),
        new(112, ScopeNodeKind.Division, 11, AuthorizationStatus.Suspended, 6),
        new(113, ScopeNodeKind.Division, 11, AuthorizationStatus.Pending, 7),
        new(121, ScopeNodeKind.Division, 12, AuthorizationStatus.Active, 8),
        new(2, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
        new(21, ScopeNodeKind.Branch, 2, AuthorizationStatus.Active, 1)
    ]);

    [Theory]
    [InlineData(AuthorizationStatus.Active)]
    [InlineData(AuthorizationStatus.Suspended)]
    public void Approved_branch_grant_reads_branch_and_valid_divisions_before_and_after_suspension(AuthorizationStatus status)
    {
        var snapshot = Snapshot() with { Directory = WithStatus(11, status) };
        var query = Compile(snapshot);
        Assert.True(query.IsAllowed);
        Assert.True(query.Matches(new(1, 11)));
        Assert.True(query.Matches(new(1, 11, 111)));
        Assert.True(query.Matches(new(1, 11, 112)));
        Assert.False(query.Matches(new(1, 11, 113)));
        Assert.False(query.Matches(new(1, 12, 121)));
        Assert.False(query.Matches(new(1, 11, 121)));
        Assert.False(query.Matches(new(2, 11, 111)));
        var clause = Assert.Single(query.Clauses);
        Assert.Equal(new long[] { 1 }, clause.AuthorityNodes.Select(node => node.Id).ToArray());
        Assert.Equal(new long[] { 11, 111, 112 }, clause.TargetNodes.Select(node => node.Id).Order().ToArray());
        Assert.Equal(status, clause.TargetNodes.Single(node => node.Id == 11).Status);
        Assert.Equal(3, clause.TargetNodes.Single(node => node.Id == 11).Version);
        Assert.Equal(4, clause.Grant.Version);
        Assert.Equal(5, clause.Membership.Version);
        Assert.Equal(801, clause.Grant.RequestedBy);
        Assert.Equal(802, clause.Grant.ApprovedBy);
    }

    [Fact]
    public void Division_request_is_exact_and_branch_grant_contains_it_without_reverse_broadening()
    {
        var request = Request() with { TargetScope = Division };
        var query = Compile(request: request);
        Assert.True(query.Matches(new(1, 11, 111)));
        Assert.False(query.Matches(new(1, 11)));
        Assert.False(query.Matches(new(1, 11, 112)));
        Assert.Equal(new long[] { 11, 111 }, query.Clauses[0].TargetNodes.Select(node => node.Id).Order().ToArray());
        var snapshot = Snapshot() with { Grants = [Grant() with { Scope = Division }] };
        Assert.True(Compile(snapshot, request).IsAllowed);
        Assert.False(Compile(snapshot).IsAllowed);
        Assert.False(Compile(request: request with { TargetScope = Division with { DivisionId = 121 } }).IsAllowed);
    }

    [Theory]
    [InlineData(Permissions.HistoryRead, "historical-review")]
    [InlineData(Permissions.ReportRead, "reporting")]
    public void Only_controlled_permission_and_purpose_pairs_compile(string permission, string purpose)
    {
        Assert.True(Compile(request: new(permission, Branch, purpose)).IsAllowed);
        Assert.False(Compile(request: new(permission, Branch, "other")).IsAllowed);
        Assert.False(Compile(request: new(permission, Branch, "")).IsAllowed);
        Assert.False(Compile(request: new(permission, Branch, purpose.ToUpperInvariant())).IsAllowed);
        Assert.False(Compile(request: new(permission, Branch, purpose + " ")).IsAllowed);
    }

    [Theory]
    [InlineData(Permissions.OrderSubmit)]
    [InlineData(Permissions.OrderSpendApprove)]
    [InlineData(Permissions.AccessApprove)]
    [InlineData(Permissions.ReportPrepare)]
    [InlineData("unknown")]
    public void No_write_or_unregistered_permission_uses_historical_evidence(string permission) =>
        Assert.False(Compile(request: new(permission, Branch, "historical-review")).IsAllowed);

    [Fact]
    public void Historical_marker_is_not_a_role_template_and_never_authorizes_ordinary_access()
    {
        Assert.Equal(15, RoleTemplates.Templates.Count);
        Assert.False(RoleTemplates.Templates.ContainsKey(HistoricalReadPolicy.TemplateId));
        foreach (var permission in new[] { Permissions.HistoryRead, Permissions.ReportRead, Permissions.OrderSubmit, Permissions.AccessApprove })
            Assert.False(ScopePolicy.Compile(Reader, Snapshot(), permission, Now).IsAllowed);
        Assert.False(Compile(Snapshot() with { Grants = [Grant() with { TemplateId = "buyer" }] }).IsAllowed);
    }

    [Fact]
    public void Reader_identity_factor_and_snapshot_must_be_current()
    {
        foreach (var actor in new[]
        {
            Reader with { UserId = 0 }, Reader with { UserId = 102 }, Reader with { GrantVersion = -1 },
            Reader with { GrantVersion = 8 }, Reader with { ProviderIssuer = "" }, Reader with { ProviderSubject = " " },
            Reader with { AuthenticatedAt = default }, Reader with { AuthenticatedAt = Now.AddTicks(1) },
            Reader with { Assurance = FactorAssurance.SingleFactor }
        }) Assert.False(HistoricalReadPolicy.Compile(actor, Snapshot(), Request(), Now).IsAllowed);
        foreach (var snapshot in new[]
        {
            Snapshot() with { Status = AuthorizationStatus.Pending }, Snapshot() with { Status = AuthorizationStatus.Suspended },
            Snapshot() with { Status = AuthorizationStatus.Revoked }, Snapshot() with { UserId = 102 },
            Snapshot() with { GrantVersion = 8 }, Snapshot() with { Grants = [] }, Snapshot() with { Memberships = [] }
        }) Assert.False(Compile(snapshot).IsAllowed);
    }

    [Fact]
    public void Beneficiary_is_exact_approved_current_active_parent_organization_membership()
    {
        var member = Member();
        foreach (var invalid in new[]
        {
            member with { Id = 0 }, member with { Id = 502 }, member with { UserId = 102 }, member with { Version = 0 },
            member with { ApprovedBy = 0 }, member with { ApprovedBy = 101 },
            member with { Status = AuthorizationStatus.Pending }, member with { Status = AuthorizationStatus.Suspended },
            member with { Status = AuthorizationStatus.Revoked }, member with { EffectiveAt = default },
            member with { EffectiveAt = Now.AddTicks(1) }, member with { ExpiresAt = Now }, member with { RevokedAt = Now },
            member with { Scope = Branch }, member with { Scope = Division }, member with { Scope = Organization with { OrganizationId = 2 } },
            member with { Scope = Organization with { BranchId = 11 } }, member with { Scope = null! }
        }) Assert.False(Compile(Snapshot() with { Memberships = [invalid] }).IsAllowed, invalid.ToString());
        var suspendedBranch = member with { Scope = Branch, Status = AuthorizationStatus.Suspended };
        Assert.False(HistoricalReadPolicy.IsBeneficiaryEligible(suspendedBranch, Grant(), WithStatus(11, AuthorizationStatus.Suspended), Now));
    }

    [Fact]
    public void Grant_validity_cannot_outlive_or_predate_approved_membership()
    {
        Assert.False(Compile(Snapshot() with { Memberships = [Member() with { EffectiveAt = Now.AddHours(-12) }] }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Memberships = [Member() with { ExpiresAt = Now.AddHours(1) }] }).IsAllowed);
        var snapshot = Snapshot() with
        {
            Memberships = [Member() with { ExpiresAt = Now.AddHours(1) }],
            Grants = [Grant() with { ExpiresAt = Now.AddHours(1) }]
        };
        Assert.True(Compile(snapshot).IsAllowed);
        Assert.False(Compile(snapshot with { Grants = [Grant() with { ExpiresAt = Now.AddHours(2) }] }).IsAllowed);
    }

    [Fact]
    public void Invalid_grant_shape_approval_provenance_versions_and_lifetime_deny()
    {
        var grant = Grant();
        foreach (var invalid in new[]
        {
            grant with { Id = 0 }, grant with { UserId = 102 }, grant with { MembershipId = 0 }, grant with { Version = 0 },
            grant with { TemplateId = "buyer" }, grant with { TemplateVersion = 2 }, grant with { RequestedBy = 0 },
            grant with { ApprovedBy = 0 }, grant with { ApprovedBy = 101 }, grant with { ApprovedBy = 801 },
            grant with { Reason = " " }, grant with { Reason = new string('x', 501) }, grant with { Permissions = default },
            grant with { Permissions = [] }, grant with { Permissions = [Permissions.HistoryRead, Permissions.HistoryRead] },
            grant with { Permissions = [Permissions.HistoryRead, Permissions.OrderSubmit] },
            grant with { DelegablePermissions = [Permissions.HistoryRead] },
            grant with { Status = AuthorizationStatus.Pending }, grant with { Status = AuthorizationStatus.Suspended },
            grant with { Status = AuthorizationStatus.Revoked }, grant with { EffectiveAt = default },
            grant with { EffectiveAt = Now.AddTicks(1) }, grant with { ExpiresAt = Now },
            grant with { ExpiresAt = Now.AddDays(-2) }, grant with { RevokedAt = Now }, grant with { Scope = null! }
        }) Assert.False(Compile(Snapshot() with { Grants = [invalid] }).IsAllowed, invalid.ToString());
    }

    [Fact]
    public void Missing_ambiguous_or_null_authorization_records_fail_closed()
    {
        Assert.False(Compile(Snapshot() with { Grants = [Grant(), Grant()] }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Memberships = [Member(), Member()] }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Grants = [null!] }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Memberships = [null!] }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Directory = new(default) }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Directory = new([.. Directory.Nodes, Directory.Nodes[0]]) }).IsAllowed);
        Assert.False(Compile(Snapshot() with { Directory = new([.. Directory.Nodes, null!]) }).IsAllowed);
    }

    [Fact]
    public void Complete_alternatives_keep_each_grant_membership_and_evidence()
    {
        var snapshot = Snapshot() with
        {
            Grants = [Grant(), Grant() with { Id = 1002, MembershipId = 502, Version = 8 }],
            Memberships = [Member(), Member() with { Id = 502, Version = 9 }]
        };
        var query = Compile(snapshot);
        Assert.Equal(2, query.Clauses.Length);
        Assert.Equal((1001L, 501L, 4L, 5L), (query.Clauses[0].Grant.Id, query.Clauses[0].Membership.Id,
            query.Clauses[0].Grant.Version, query.Clauses[0].Membership.Version));
        Assert.Equal((1002L, 502L, 8L, 9L), (query.Clauses[1].Grant.Id, query.Clauses[1].Membership.Id,
            query.Clauses[1].Grant.Version, query.Clauses[1].Membership.Version));
        Assert.False(Compile(snapshot with { Memberships = [Member() with { Status = AuthorizationStatus.Suspended },
            Member() with { Id = 502, Scope = Organization with { OrganizationId = 2 } }] }).IsAllowed);
    }

    [Fact]
    public void Targets_preserve_canonical_kind_parent_status_and_version()
    {
        foreach (var invalid in new[]
        {
            Branch with { OrganizationId = 0 }, Branch with { BranchId = 0 }, Branch with { DivisionId = 111 },
            Branch with { OrganizationId = 2 }, Branch with { ShopId = 9 }, Branch with { WarehouseId = 9 },
            Branch with { ChannelId = 9 }, Organization, new GrantScope(GrantScopeKind.System),
            Division with { BranchId = 12 }, Division with { DivisionId = 0 }
        }) Assert.False(HistoricalReadPolicy.IsTargetEligible(invalid, Directory), invalid.ToString());
        foreach (var invalidNode in new[]
        {
            Directory.Nodes[0] with { Status = AuthorizationStatus.Suspended },
            Directory.Nodes[0] with { ParentId = 9 }, Directory.Nodes[1] with { Status = AuthorizationStatus.Pending },
            Directory.Nodes[1] with { Status = AuthorizationStatus.Revoked }, Directory.Nodes[1] with { ParentId = 2 },
            Directory.Nodes[1] with { Version = 0 }, Directory.Nodes[1] with { Kind = ScopeNodeKind.Shop }
        })
        {
            var directory = new ScopeDirectory(Directory.Nodes.Select(node => node.Id == invalidNode.Id ? invalidNode : node).ToImmutableArray());
            Assert.False(Compile(Snapshot() with { Directory = directory }).IsAllowed, invalidNode.ToString());
        }
    }

    [Fact]
    public void Ambiguous_pending_revoked_or_mixed_parent_children_never_match_branch_reads()
    {
        foreach (var invalid in new[]
        {
            Directory.Nodes[3] with { Status = AuthorizationStatus.Pending },
            Directory.Nodes[3] with { Status = AuthorizationStatus.Revoked },
            Directory.Nodes[3] with { Version = 0 }, Directory.Nodes[3] with { ParentId = 12 }
        })
        {
            var directory = new ScopeDirectory(Directory.Nodes.Select(node => node.Id == invalid.Id ? invalid : node).ToImmutableArray());
            Assert.False(Compile(Snapshot() with { Directory = directory }).Matches(new(1, 11, 111)));
        }
        Assert.False(Compile(Snapshot() with { Directory = new([.. Directory.Nodes, Directory.Nodes[3]]) }).Matches(new(1, 11, 111)));
    }

    [Fact]
    public void Resource_matching_allows_dual_sided_records_but_rejects_malformed_and_administrative_shapes()
    {
        var resource = new ResourceScope(1, 11, 111);
        foreach (var invalid in new[]
        {
            resource with { FulfillmentOrganizationId = 0 }, resource with { ShopId = 21 },
            resource with { WarehouseId = 211 }, resource with { ChannelId = 201 }, resource with { IsAdministrative = true },
            resource with { BuyingOrganizationId = null }, resource with { BranchId = null }, resource with { DivisionId = 0 }
        }) Assert.False(Compile().Matches(invalid));
        Assert.False(Compile().Matches(null!));
        Assert.True(Compile().Matches(resource with { FulfillmentOrganizationId = 9, ShopId = 91, WarehouseId = 911, ChannelId = 901 }));
    }

    [Theory]
    [InlineData(AuthorizationStatus.Active)]
    [InlineData(AuthorizationStatus.Suspended)]
    public void Independent_approver_can_approve_explicit_read_grant_before_and_after_suspension(AuthorizationStatus status)
    {
        var snapshot = ApproverSnapshot() with { Directory = WithStatus(11, status) };
        var decision = HistoricalReadPolicy.CanApprove(Approver, snapshot, Proposed(), Member(), Now);
        Assert.True(decision.Allowed, decision.Code);
        Assert.Equal(new long[] { 2001 }, decision.GrantIds.ToArray());
    }

    [Fact]
    public void Approval_does_not_union_delegation_from_different_grants()
    {
        var snapshot = ApproverSnapshot();
        var authority = snapshot.Grants[0];
        snapshot = snapshot with { Grants = [authority with { DelegablePermissions = [Permissions.HistoryRead] },
            authority with { Id = 2002, DelegablePermissions = [Permissions.ReportRead] }] };
        Assert.False(HistoricalReadPolicy.CanApprove(Approver, snapshot, Proposed(), Member(), Now).Allowed);
        snapshot = snapshot with { Grants = [.. snapshot.Grants, authority with { Id = 2003 }] };
        var decision = HistoricalReadPolicy.CanApprove(Approver, snapshot, Proposed(), Member(), Now);
        Assert.True(decision.Allowed);
        Assert.Equal(new long[] { 2003 }, decision.GrantIds.ToArray());
    }

    [Fact]
    public void Approval_requires_immutable_independent_requester_beneficiary_and_pending_evidence()
    {
        foreach (var proposed in new[]
        {
            Proposed() with { RequestedBy = 0 }, Proposed() with { RequestedBy = Approver.UserId },
            Proposed() with { UserId = Approver.UserId }, Proposed() with { Version = 0 },
            Proposed() with { ApprovedBy = 999 }, Proposed() with { Status = AuthorizationStatus.Active },
            Proposed() with { Status = AuthorizationStatus.Revoked }, Proposed() with { RevokedAt = Now },
            Proposed() with { ExpiresAt = Now }
        }) Assert.False(HistoricalReadPolicy.CanApprove(Approver, ApproverSnapshot(), proposed, Member(), Now).Allowed);
    }

    [Fact]
    public void Approval_requires_current_ordinary_authority_and_active_beneficiary_parent()
    {
        var snapshot = ApproverSnapshot();
        foreach (var invalid in new[]
        {
            snapshot with { Status = AuthorizationStatus.Suspended }, snapshot with { GrantVersion = 8 },
            snapshot with { Memberships = [snapshot.Memberships[0] with { Status = AuthorizationStatus.Suspended }] },
            snapshot with { Grants = [snapshot.Grants[0] with { Scope = Organization with { OrganizationId = 2 } }] },
            snapshot with { Grants = [snapshot.Grants[0] with { TemplateId = HistoricalReadPolicy.TemplateId }] },
            snapshot with { Directory = WithStatus(1, AuthorizationStatus.Suspended) }
        }) Assert.False(HistoricalReadPolicy.CanApprove(Approver, invalid, Proposed(), Member(), Now).Allowed);
        Assert.False(HistoricalReadPolicy.CanApprove(Approver with { AuthenticatedAt = Now.AddHours(-1) }, snapshot, Proposed(), Member(), Now).Allowed);
        Assert.False(HistoricalReadPolicy.CanApprove(Approver, snapshot, Proposed(), Member() with { Status = AuthorizationStatus.Suspended }, Now).Allowed);
    }

    [Fact]
    public void Malformed_optional_actor_references_deny_even_when_the_buying_scope_matches()
    {
        var query = Compile();
        var resource = new ResourceScope(1, 11, 111);
        Assert.True(query.Matches(resource));
        Assert.True(query.Matches(resource with { CreatedBy = 201, AssignedUserIds = [202, 203] }));
        Assert.False(query.Matches(resource with { CreatedBy = 0 }));
        Assert.False(query.Matches(resource with { CreatedBy = -1 }));
        Assert.False(query.Matches(resource with { AssignedUserIds = [0] }));
        Assert.False(query.Matches(resource with { AssignedUserIds = [202, -1] }));
    }

    private static HistoricalReadRequest Request() => new(Permissions.HistoryRead, Branch, HistoricalReadPolicy.HistoricalReviewPurpose);
    private static HistoricalReadQuery Compile(AuthorizationSnapshot? snapshot = null, HistoricalReadRequest? request = null) =>
        HistoricalReadPolicy.Compile(Reader, snapshot ?? Snapshot(), request ?? Request(), Now);
    private static PermissionGrant Grant() => new(1001, 101, 501, HistoricalReadPolicy.TemplateId, 1,
        [Permissions.HistoryRead, Permissions.ReportRead], Branch, AuthorizationStatus.Active, 801, 802,
        "Reviewed branch history", Now.AddDays(-1), Version: 4);
    private static PermissionGrant Proposed() => Grant() with { Status = AuthorizationStatus.Pending, ApprovedBy = 0 };
    private static Membership Member() => new(501, 101, Organization, AuthorizationStatus.Active, 802, Now.AddDays(-2), Version: 5);
    private static AuthorizationSnapshot Snapshot() => new(101, 7, AuthorizationStatus.Active, [Grant()], [Member()], Directory);
    private static ScopeDirectory WithStatus(long id, AuthorizationStatus status) => new(Directory.Nodes.Select(node =>
        node.Id == id ? node with { Status = status } : node).ToImmutableArray());
    private static AuthorizationSnapshot ApproverSnapshot() => new(802, 7, AuthorizationStatus.Active,
        [new(2001, 802, 601, "buyer", 1, [Permissions.AccessApprove], Organization, AuthorizationStatus.Active,
            701, 702, "Explicit read delegation", Now.AddDays(-1), DelegablePermissions: [Permissions.HistoryRead, Permissions.ReportRead])],
        [new(601, 802, Organization, AuthorizationStatus.Active, 702, Now.AddDays(-2))], Directory);
}
