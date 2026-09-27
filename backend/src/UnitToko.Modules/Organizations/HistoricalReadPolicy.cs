using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Modules.Organizations;

/// <summary>Explicit historical-read grants are a specialized stored grant kind, never a role template.</summary>
public static class HistoricalReadPolicy
{
    public const string TemplateId = "historical-read";
    public const int TemplateVersion = 1;
    public const int MaximumPurposeLength = 500;
    public const string HistoricalReviewPurpose = "historical-review";
    public const string ReportingPurpose = "reporting";

    public static bool IsPurposeAllowed(string permission, string purpose) =>
        permission == Permissions.HistoryRead && purpose == HistoricalReviewPurpose ||
        permission == Permissions.ReportRead && purpose == ReportingPurpose;

    public static bool IsTargetShape(GrantScope? scope) => scope is
        { OrganizationId: > 0, BranchId: > 0, ShopId: null, WarehouseId: null, ChannelId: null } &&
        (scope.Kind == GrantScopeKind.BuyingBranch && scope.DivisionId is null ||
         scope.Kind == GrantScopeKind.BuyingDivision && scope.DivisionId is > 0);

    public static bool IsTargetEligible(GrantScope scope, ScopeDirectory directory) =>
        IsTargetShape(scope) && IsDirectoryCanonical(directory) &&
        directory.IsActive(scope.OrganizationId, ScopeNodeKind.BuyingOrganization) &&
        IsHistoricalNode(directory.Find(scope.BranchId!.Value, ScopeNodeKind.Branch), scope.OrganizationId) &&
        (scope.DivisionId is null || IsHistoricalNode(directory.Find(scope.DivisionId.Value, ScopeNodeKind.Division), scope.BranchId));

    /// <summary>Exact target evidence includes all eligible division descendants for a branch query.
    /// Its active organization is authority evidence and is deliberately not returned here.</summary>
    public static ImmutableArray<ScopeNode> TargetNodes(GrantScope scope, ScopeDirectory directory)
    {
        if (!IsTargetEligible(scope, directory)) return [];
        var nodes = ImmutableArray.CreateBuilder<ScopeNode>();
        nodes.Add(directory.Find(scope.BranchId!.Value, ScopeNodeKind.Branch)!);
        if (scope.Kind == GrantScopeKind.BuyingDivision)
            nodes.Add(directory.Find(scope.DivisionId!.Value, ScopeNodeKind.Division)!);
        else
            nodes.AddRange(directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Division &&
                IsHistoricalNode(node, scope.BranchId)).OrderBy(node => node.Id));
        return nodes.ToImmutable();
    }

    /// <summary>Validates the persisted special grant shape without treating a pending proposal as approved.</summary>
    public static bool IsGrantShape(PermissionGrant grant) => grant is not null && grant.Id > 0 && grant.UserId > 0 &&
        grant.MembershipId > 0 && grant.Version > 0 && grant.TemplateId == TemplateId && grant.TemplateVersion == TemplateVersion &&
        grant.RequestedBy > 0 && !string.IsNullOrWhiteSpace(grant.Reason) && grant.Reason.Length <= MaximumPurposeLength &&
        IsTargetShape(grant.Scope) && !grant.Permissions.IsDefaultOrEmpty && grant.Permissions.Length <= 2 &&
        grant.Permissions.Distinct(StringComparer.Ordinal).Count() == grant.Permissions.Length &&
        grant.Permissions.All(permission => permission is Permissions.HistoryRead or Permissions.ReportRead) &&
        grant.DelegablePermissions.IsDefaultOrEmpty && grant.EffectiveAt != default &&
        (grant.ExpiresAt is null || grant.ExpiresAt > grant.EffectiveAt);

    public static bool IsGrantActive(PermissionGrant grant, long actorId, DateTimeOffset now) => IsGrantShape(grant) &&
        actorId > 0 && grant.UserId == actorId && grant.ApprovedBy > 0 && grant.ApprovedBy != grant.UserId &&
        grant.RequestedBy != grant.ApprovedBy &&
        ScopePolicy.IsEffective(grant.Status, grant.EffectiveAt, grant.ExpiresAt, grant.RevokedAt, now);

    /// <summary>A suspended branch membership never substitutes for approved active parent membership.</summary>
    public static bool IsBeneficiaryEligible(Membership membership, PermissionGrant grant, ScopeDirectory directory,
        DateTimeOffset now) => IsGrantShape(grant) && membership is not null && membership.Id > 0 &&
        membership.Id == grant.MembershipId && membership.UserId == grant.UserId && membership.Version > 0 &&
        membership.ApprovedBy > 0 && membership.ApprovedBy != membership.UserId &&
        membership.Scope is { Kind: GrantScopeKind.BuyingOrganization } &&
        ScopePolicy.IsEffective(membership.Status, membership.EffectiveAt, membership.ExpiresAt, membership.RevokedAt, now) &&
        IsTargetEligible(grant.Scope, directory) && ScopePolicy.IsScopeActive(membership.Scope, directory) &&
        ScopePolicy.ContainsScope(membership.Scope, grant.Scope) && grant.EffectiveAt >= membership.EffectiveAt &&
        (membership.ExpiresAt is null || grant.ExpiresAt is not null && grant.ExpiresAt <= membership.ExpiresAt);

    public static HistoricalReadQuery Compile(ActorContext actor, AuthorizationSnapshot snapshot,
        HistoricalReadRequest request, DateTimeOffset now)
    {
        var requirement = Permissions.Find(request?.Permission ?? "")?.Assurance ?? new AssuranceRequirement(true);
        HistoricalReadQuery Deny(string code) => new(request!, actor.UserId, snapshot.GrantVersion, [], requirement, snapshot.Directory, code);
        if (actor.UserId <= 0 || actor.GrantVersion < 0 || actor.AuthenticatedAt == default || actor.AuthenticatedAt > now ||
            string.IsNullOrWhiteSpace(actor.ProviderIssuer) || string.IsNullOrWhiteSpace(actor.ProviderSubject) ||
            snapshot.UserId != actor.UserId || snapshot.Status != AuthorizationStatus.Active || snapshot.GrantVersion != actor.GrantVersion)
            return Deny("stale_or_inactive_actor");
        if (request is null || !IsPurposeAllowed(request.Permission, request.Purpose)) return Deny("invalid_historical_request");
        if (!AssurancePolicy.Satisfies(actor, requirement, now)) return Deny("step_up_required");
        if (!IsTargetEligible(request.TargetScope, snapshot.Directory)) return Deny("historical_target_unavailable");
        if (!IsSnapshotCanonical(snapshot)) return Deny("invalid_authorization_snapshot");

        var targetNodes = TargetNodes(request.TargetScope, snapshot.Directory);
        var clauses = ImmutableArray.CreateBuilder<HistoricalReadClause>();
        foreach (var grant in snapshot.Grants)
        {
            if (!IsGrantActive(grant, actor.UserId, now) || !grant.Permissions.Contains(request.Permission, StringComparer.Ordinal) ||
                !ScopePolicy.ContainsScope(grant.Scope, request.TargetScope)) continue;
            var membership = snapshot.Memberships.SingleOrDefault(member => member.Id == grant.MembershipId);
            if (membership is null || !IsBeneficiaryEligible(membership, grant, snapshot.Directory, now)) continue;
            clauses.Add(new(grant, membership, ScopePolicy.RequiredNodes(membership.Scope, snapshot.Directory), targetNodes));
        }
        return clauses.Count == 0 ? Deny("historical_read_denied") :
            new(request, actor.UserId, snapshot.GrantVersion, clauses.ToImmutable(), requirement, snapshot.Directory);
    }

    /// <summary>The caller loads immutable pending proposal data and checks its expected revision transactionally.
    /// One current ordinary access-approval grant must delegate every requested read permission.</summary>
    public static AuthorizationDecision CanApprove(ActorContext actor, AuthorizationSnapshot snapshot,
        PermissionGrant proposed, Membership beneficiary, DateTimeOffset now)
    {
        if (!IsGrantShape(proposed) || proposed.Status != AuthorizationStatus.Pending || proposed.ApprovedBy != 0 ||
            proposed.RevokedAt is not null || proposed.ExpiresAt <= now)
            return AuthorizationDecision.Deny("invalid_historical_grant");
        var independent = ApprovalPolicy.Evaluate(actor.UserId, Permissions.AccessApprove,
            new(proposed.Version, proposed.Version, ProposedBy: proposed.RequestedBy, BeneficiaryId: proposed.UserId));
        if (!independent.Allowed) return independent;
        if (!IsBeneficiaryEligible(beneficiary, proposed, snapshot.Directory, now))
            return AuthorizationDecision.Deny("approved_membership_required");
        if (!IsSnapshotCanonical(snapshot)) return AuthorizationDecision.Deny("invalid_authorization_snapshot");
        var authority = ScopePolicy.Compile(actor, snapshot, Permissions.AccessApprove, now);
        if (!authority.IsAllowed) return AuthorizationDecision.Deny(authority.DenialCode ?? "permission_denied");
        var permitted = authority.Clauses.Where(clause => ScopePolicy.ContainsScope(clause.Scope, proposed.Scope) &&
            snapshot.Grants.Any(grant => grant.Id == clause.GrantId && !grant.DelegablePermissions.IsDefaultOrEmpty &&
                proposed.Permissions.All(permission => grant.DelegablePermissions.Contains(permission, StringComparer.Ordinal))))
            .Select(clause => clause.GrantId).ToImmutableArray();
        return permitted.IsEmpty ? AuthorizationDecision.Deny("delegation_denied") : new(true, "allowed", permitted);
    }

    private static bool IsHistoricalNode(ScopeNode? node, long? parentId) => node is { Id: > 0, Version: > 0 } &&
        node.ParentId == parentId && node.Status is AuthorizationStatus.Active or AuthorizationStatus.Suspended;

    private static bool IsDirectoryCanonical(ScopeDirectory directory) => directory is not null && !directory.Nodes.IsDefaultOrEmpty &&
        directory.Nodes.All(node => node is not null && node.Id > 0) &&
        directory.Nodes.Select(node => node.Id).Distinct().Count() == directory.Nodes.Length;

    private static bool IsSnapshotCanonical(AuthorizationSnapshot snapshot) =>
        !snapshot.Grants.IsDefaultOrEmpty && !snapshot.Memberships.IsDefaultOrEmpty &&
        snapshot.Grants.All(grant => grant is not null) && snapshot.Memberships.All(membership => membership is not null) &&
        snapshot.Grants.Select(grant => grant.Id).Distinct().Count() == snapshot.Grants.Length &&
        snapshot.Memberships.Select(membership => membership.Id).Distinct().Count() == snapshot.Memberships.Length;
}
