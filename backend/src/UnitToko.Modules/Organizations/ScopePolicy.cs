using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Modules.Organizations;

public static class ScopePolicy
{
    public static AuthorizationQuery Compile(ActorContext actor, AuthorizationSnapshot snapshot, string permission,
        DateTimeOffset now)
    {
        var definition = Permissions.Find(permission);
        var requirement = definition?.Assurance ?? new AssuranceRequirement(true);
        AuthorizationQuery Deny(string code) => new(permission, actor.UserId, snapshot.GrantVersion, [], requirement, snapshot.Directory, code);
        if (actor.UserId <= 0 || actor.GrantVersion < 0 || actor.AuthenticatedAt == default || actor.AuthenticatedAt > now ||
            string.IsNullOrWhiteSpace(actor.ProviderIssuer) || string.IsNullOrWhiteSpace(actor.ProviderSubject) ||
            snapshot.UserId != actor.UserId || snapshot.Status != AuthorizationStatus.Active || snapshot.GrantVersion != actor.GrantVersion)
            return Deny("stale_or_inactive_actor");
        if (definition is null) return Deny("unknown_permission");
        if (!AssurancePolicy.Satisfies(actor, requirement, now)) return Deny("step_up_required");
        if (snapshot.Grants.IsDefaultOrEmpty || snapshot.Memberships.IsDefaultOrEmpty) return Deny("permission_denied");
        if (snapshot.Grants.Select(grant => grant.Id).Distinct().Count() != snapshot.Grants.Length) return Deny("invalid_authorization_snapshot");
        var clauses = ImmutableArray.CreateBuilder<ScopeClause>();
        foreach (var grant in snapshot.Grants)
        {
            if (!IsGrantActive(grant, actor.UserId, now) || !grant.Permissions.Contains(permission, StringComparer.Ordinal) ||
                !definition.AllowedScopes.Contains(grant.Scope.Kind) || !IsScopeActive(grant.Scope, snapshot.Directory)) continue;
            if (grant.Scope.Kind is GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision &&
                !grant.Permissions.Contains(Permissions.BranchPic, StringComparer.Ordinal) && !IsDelegationPermission(permission)) continue;
            if (grant.Scope.Kind == GrantScopeKind.BuyingOrganization &&
                !grant.Permissions.Contains(Permissions.OrganizationWide, StringComparer.Ordinal) && !IsDelegationPermission(permission)) continue;
            var memberships = snapshot.Memberships.Where(membership => membership.Id == grant.MembershipId).ToArray();
            if (memberships.Length != 1) continue;
            var membership = memberships[0];
            if (membership.UserId != actor.UserId || membership.Id <= 0 || membership.Version <= 0 ||
                membership.ApprovedBy <= 0 || membership.ApprovedBy == membership.UserId ||
                !IsEffective(membership.Status, membership.EffectiveAt, membership.ExpiresAt, membership.RevokedAt, now) ||
                !IsScopeActive(membership.Scope, snapshot.Directory) || !ContainsScope(membership.Scope, grant.Scope)) continue;
            clauses.Add(new(grant.Id, membership.Id, actor.UserId, grant.Scope,
                RequiredNodes(grant.Scope, snapshot.Directory).Concat(RequiredNodes(membership.Scope, snapshot.Directory)).Distinct().ToImmutableArray()));
        }
        return clauses.Count == 0 ? Deny("permission_denied") : new(permission, actor.UserId, snapshot.GrantVersion,
            clauses.ToImmutable(), requirement, snapshot.Directory);
    }

    public static AuthorizationDecision Authorize(ActorContext actor, AuthorizationSnapshot snapshot, string permission,
        ResourceScope resource, DateTimeOffset now, ApprovalRequest? approval = null)
    {
        var query = Compile(actor, snapshot, permission, now);
        if (!query.IsAllowed) return AuthorizationDecision.Deny(query.DenialCode ?? "permission_denied");
        if (!query.Matches(resource)) return AuthorizationDecision.Deny("scope_denied");
        if (ApprovalPolicy.RequiresEvidence(permission) && approval is null) return AuthorizationDecision.Deny("approval_evidence_required");
        if (approval is not null)
        {
            var independence = ApprovalPolicy.Evaluate(actor.UserId, permission, approval);
            if (!independence.Allowed) return independence;
        }
        return new(true, "allowed", query.Clauses.Where(clause => clause.Matches(resource)).Select(clause => clause.GrantId).ToImmutableArray());
    }

    public static bool IsGrantActive(PermissionGrant grant, long actorId, DateTimeOffset now) =>
        grant.Id > 0 && grant.UserId == actorId && grant.MembershipId > 0 && grant.Version > 0 &&
        !string.IsNullOrWhiteSpace(grant.TemplateId) && RoleTemplates.Templates.ContainsKey(grant.TemplateId) && grant.TemplateVersion == RoleTemplates.CurrentVersion &&
        grant.RequestedBy > 0 && grant.ApprovedBy > 0 && grant.RequestedBy != grant.ApprovedBy && grant.ApprovedBy != grant.UserId &&
        !string.IsNullOrWhiteSpace(grant.Reason) && !grant.Permissions.IsDefault &&
        grant.Permissions.All(permission => Permissions.Find(permission) is { } definition && definition.AllowedScopes.Contains(grant.Scope.Kind)) &&
        (grant.DelegablePermissions.IsDefaultOrEmpty || grant.DelegablePermissions.All(permission => Permissions.Find(permission) is not null)) &&
        IsEffective(grant.Status, grant.EffectiveAt, grant.ExpiresAt, grant.RevokedAt, now);

    public static bool IsEffective(AuthorizationStatus status, DateTimeOffset effectiveAt, DateTimeOffset? expiresAt,
        DateTimeOffset? revokedAt, DateTimeOffset now) => status == AuthorizationStatus.Active && effectiveAt != default &&
        effectiveAt <= now && (expiresAt is null || expiresAt > now && expiresAt > effectiveAt) && revokedAt is null;

    public static bool IsScopeActive(GrantScope scope, ScopeDirectory directory)
    {
        if (scope.Kind == GrantScopeKind.System) return AllBusinessIdsAbsent(scope);
        if (IsBuying(scope.Kind))
        {
            if (scope.ShopId is not null || scope.WarehouseId is not null || scope.ChannelId is not null ||
                !directory.IsActive(scope.OrganizationId, ScopeNodeKind.BuyingOrganization)) return false;
            if (scope.BranchId is not null && !directory.IsActive(scope.BranchId, ScopeNodeKind.Branch, scope.OrganizationId) ||
                scope.DivisionId is not null && (scope.BranchId is null || !directory.IsActive(scope.DivisionId, ScopeNodeKind.Division, scope.BranchId))) return false;
            return scope.Kind switch
            {
                GrantScopeKind.BuyerOwned => true,
                GrantScopeKind.BuyingDivision => scope.BranchId is > 0 && scope.DivisionId is > 0,
                GrantScopeKind.BuyingBranch => scope.BranchId is > 0 && scope.DivisionId is null,
                GrantScopeKind.BuyingOrganization => scope.BranchId is null && scope.DivisionId is null,
                _ => false
            };
        }
        if (!IsFulfillment(scope.Kind) || scope.BranchId is not null || scope.DivisionId is not null ||
            !directory.IsActive(scope.OrganizationId, ScopeNodeKind.FulfillmentOrganization) ||
            !directory.IsActive(scope.ShopId, ScopeNodeKind.Shop, scope.OrganizationId) ||
            !directory.IsActive(scope.ChannelId, ScopeNodeKind.Channel, scope.OrganizationId)) return false;
        return scope.Kind switch
        {
            GrantScopeKind.FulfillmentShop => scope.WarehouseId is null,
            GrantScopeKind.FulfillmentWarehouse => directory.IsActive(scope.WarehouseId, ScopeNodeKind.Warehouse, scope.ShopId),
            GrantScopeKind.CourierAssignment => scope.WarehouseId is null || directory.IsActive(scope.WarehouseId, ScopeNodeKind.Warehouse, scope.ShopId),
            _ => false
        };
    }

    public static bool IsResourceActive(ResourceScope resource, ScopeDirectory directory)
    {
        if (resource.IsAdministrative) return resource.BuyingOrganizationId is null && resource.BranchId is null && resource.DivisionId is null &&
            resource.FulfillmentOrganizationId is null && resource.ShopId is null && resource.WarehouseId is null && resource.ChannelId is null;
        if (resource.BuyingOrganizationId is null && resource.FulfillmentOrganizationId is null) return false;
        return (resource.BuyingOrganizationId is null || directory.IsActive(resource.BuyingOrganizationId, ScopeNodeKind.BuyingOrganization)) &&
            (resource.BranchId is null || resource.BuyingOrganizationId is not null && directory.IsActive(resource.BranchId, ScopeNodeKind.Branch, resource.BuyingOrganizationId)) &&
            (resource.DivisionId is null || resource.BranchId is not null && directory.IsActive(resource.DivisionId, ScopeNodeKind.Division, resource.BranchId)) &&
            (resource.FulfillmentOrganizationId is null || directory.IsActive(resource.FulfillmentOrganizationId, ScopeNodeKind.FulfillmentOrganization)) &&
            (resource.ShopId is null || resource.FulfillmentOrganizationId is not null && directory.IsActive(resource.ShopId, ScopeNodeKind.Shop, resource.FulfillmentOrganizationId)) &&
            (resource.WarehouseId is null || resource.ShopId is not null && directory.IsActive(resource.WarehouseId, ScopeNodeKind.Warehouse, resource.ShopId)) &&
            (resource.ChannelId is null || resource.FulfillmentOrganizationId is not null && directory.IsActive(resource.ChannelId, ScopeNodeKind.Channel, resource.FulfillmentOrganizationId));
    }

    public static bool Matches(ScopeClause clause, ResourceScope resource)
    {
        var scope = clause.Scope;
        if (scope.Kind == GrantScopeKind.System) return resource.IsAdministrative;
        if (resource.IsAdministrative) return false;
        if (IsBuying(scope.Kind))
        {
            if (resource.BuyingOrganizationId != scope.OrganizationId) return false;
            return scope.Kind switch
            {
                GrantScopeKind.BuyerOwned => resource.BranchId == scope.BranchId && resource.DivisionId == scope.DivisionId &&
                    (resource.CreatedBy == clause.ActorId || !resource.AssignedUserIds.IsDefault && resource.AssignedUserIds.Contains(clause.ActorId)),
                GrantScopeKind.BuyingDivision => resource.BranchId == scope.BranchId && resource.DivisionId == scope.DivisionId,
                GrantScopeKind.BuyingBranch => resource.BranchId == scope.BranchId,
                GrantScopeKind.BuyingOrganization => true,
                _ => false
            };
        }
        if (resource.FulfillmentOrganizationId != scope.OrganizationId || resource.ShopId != scope.ShopId || resource.ChannelId != scope.ChannelId) return false;
        return scope.Kind switch
        {
            GrantScopeKind.FulfillmentShop => true,
            GrantScopeKind.FulfillmentWarehouse => resource.WarehouseId == scope.WarehouseId,
            GrantScopeKind.CourierAssignment => (scope.WarehouseId is null || resource.WarehouseId == scope.WarehouseId) &&
                !resource.AssignedUserIds.IsDefault && resource.AssignedUserIds.Contains(clause.ActorId),
            _ => false
        };
    }

    public static bool ContainsScope(GrantScope outer, GrantScope inner)
    {
        if (outer.Kind == GrantScopeKind.System || inner.Kind == GrantScopeKind.System) return outer.Kind == inner.Kind && AllBusinessIdsAbsent(outer) && AllBusinessIdsAbsent(inner);
        if (outer.OrganizationId is not > 0 || outer.OrganizationId != inner.OrganizationId) return false;
        if (IsBuying(outer.Kind) && IsBuying(inner.Kind)) return outer.Kind switch
        {
            GrantScopeKind.BuyingOrganization => true,
            GrantScopeKind.BuyingBranch => inner.Kind != GrantScopeKind.BuyingOrganization && outer.BranchId == inner.BranchId,
            GrantScopeKind.BuyingDivision => inner.Kind is GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision && outer.BranchId == inner.BranchId && outer.DivisionId == inner.DivisionId,
            GrantScopeKind.BuyerOwned => inner.Kind == GrantScopeKind.BuyerOwned && outer.BranchId == inner.BranchId && outer.DivisionId == inner.DivisionId,
            _ => false
        };
        if (!IsFulfillment(outer.Kind) || !IsFulfillment(inner.Kind) || outer.ShopId != inner.ShopId || outer.ChannelId != inner.ChannelId) return false;
        return outer.Kind switch
        {
            GrantScopeKind.FulfillmentShop => true,
            GrantScopeKind.FulfillmentWarehouse => inner.Kind is GrantScopeKind.FulfillmentWarehouse or GrantScopeKind.CourierAssignment && outer.WarehouseId == inner.WarehouseId,
            GrantScopeKind.CourierAssignment => inner.Kind == GrantScopeKind.CourierAssignment && (outer.WarehouseId is null || outer.WarehouseId == inner.WarehouseId),
            _ => false
        };
    }

    public static ImmutableArray<ScopeNode> RequiredNodes(GrantScope scope, ScopeDirectory directory) => RequiredNodes(new ResourceScope(
        BuyingOrganizationId: IsBuying(scope.Kind) ? scope.OrganizationId : null, BranchId: scope.BranchId, DivisionId: scope.DivisionId,
        FulfillmentOrganizationId: IsFulfillment(scope.Kind) ? scope.OrganizationId : null, ShopId: scope.ShopId, WarehouseId: scope.WarehouseId,
        ChannelId: scope.ChannelId, IsAdministrative: scope.Kind == GrantScopeKind.System), directory);

    public static ImmutableArray<ScopeNode> RequiredNodes(ResourceScope scope, ScopeDirectory directory)
    {
        var result = ImmutableArray.CreateBuilder<ScopeNode>();
        void Add(long? id, ScopeNodeKind kind) { if (id is > 0 && directory.Find(id.Value, kind) is { } node) result.Add(node); }
        Add(scope.BuyingOrganizationId, ScopeNodeKind.BuyingOrganization); Add(scope.BranchId, ScopeNodeKind.Branch);
        Add(scope.DivisionId, ScopeNodeKind.Division); Add(scope.FulfillmentOrganizationId, ScopeNodeKind.FulfillmentOrganization);
        Add(scope.ShopId, ScopeNodeKind.Shop); Add(scope.WarehouseId, ScopeNodeKind.Warehouse); Add(scope.ChannelId, ScopeNodeKind.Channel);
        return result.ToImmutable();
    }

    private static bool AllBusinessIdsAbsent(GrantScope scope) => scope.OrganizationId is null && scope.BranchId is null && scope.DivisionId is null && scope.ShopId is null && scope.WarehouseId is null && scope.ChannelId is null;
    private static bool IsBuying(GrantScopeKind kind) => kind is GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision or GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingOrganization;
    private static bool IsFulfillment(GrantScopeKind kind) => kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse or GrantScopeKind.CourierAssignment;
    private static bool IsDelegationPermission(string permission) => permission is Permissions.AccessApprove or Permissions.AccessRevoke or Permissions.MembershipApprove;
}
