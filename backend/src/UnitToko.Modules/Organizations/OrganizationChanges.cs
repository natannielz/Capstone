using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Modules.Organizations;

public enum OrganizationChangeKind { Create, UpdateProfile, Reactivate }

public sealed record OrganizationChangeRequest(OrganizationChangeKind ChangeKind, ScopeNodeKind Kind,
    long? ParentId, string Code, string Name, long? ScopeId = null, long ExpectedScopeVersion = 0, long ExpectedProfileVersion = 0);

public sealed record OrganizationImpact(long AffectedScopes, long ActiveMemberships, long ActiveGrants,
    long AffectedAccounts, string Revision);

public static class OrganizationChangePolicy
{
    public static OrganizationChangeRequest Normalize(OrganizationChangeRequest request)
    {
        if (!Enum.IsDefined(request.ChangeKind) || !Enum.IsDefined(request.Kind) || request.ParentId is <= 0 ||
            request.ExpectedScopeVersion < 0 || request.ExpectedScopeVersion == long.MaxValue ||
            request.ExpectedProfileVersion < 0 || request.ExpectedProfileVersion == long.MaxValue)
            throw Invalid("invalid_organization_request", "Organization change references or versions are invalid");
        string code, name;
        try { code = (request.Code ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant(); name = (request.Name ?? "").Normalize(NormalizationForm.FormKC).Trim(); }
        catch (ArgumentException) { throw Invalid("invalid_organization_text", "Organization text must be valid Unicode"); }
        if (!Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant) || name.Length is < 1 or > 160 ||
            name.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.PrivateUse))
            throw Invalid("invalid_organization_text", "A bounded organization name and reference code are required");
        if (request.ChangeKind == OrganizationChangeKind.Create
            ? request.ScopeId is not null || request.ExpectedScopeVersion != 0 || request.ExpectedProfileVersion != 0
            : request.ScopeId is not > 0 || request.ExpectedScopeVersion <= 0)
            throw Invalid("invalid_organization_revision", "Creation or existing-scope revisions are invalid");
        if (ExpectedParent(request.Kind) is null ? request.ParentId is not null : request.ParentId is null)
            throw Invalid("invalid_organization_parent", "The organization kind requires its exact parent type");
        return request with { Code = code, Name = name };
    }

    public static ImmutableArray<ScopeNode> ValidateHierarchy(OrganizationChangeRequest request, ScopeDirectory directory, ScopeNode? existing)
    {
        if (request.ChangeKind != OrganizationChangeKind.Create)
        {
            if (existing is null || existing.Id != request.ScopeId || existing.Kind != request.Kind || existing.ParentId != request.ParentId)
                throw Invalid("immutable_organization_hierarchy", "Existing organization kind and parent cannot be changed");
            if (existing.Status is not (AuthorizationStatus.Active or AuthorizationStatus.Suspended) ||
                request.ChangeKind == OrganizationChangeKind.Reactivate && existing.Status != AuthorizationStatus.Suspended)
                throw Invalid("invalid_organization_state", "The organization is not in the required state");
        }
        var nodes = ImmutableArray.CreateBuilder<ScopeNode>();
        var parentId = request.ParentId;
        var kind = ExpectedParent(request.Kind);
        while (kind is not null)
        {
            if (parentId is not > 0 || directory.Find(parentId.Value, kind.Value) is not { Status: AuthorizationStatus.Active, Version: > 0 } node)
                throw Invalid("organization_parent_unavailable", "The related parent hierarchy must be active");
            nodes.Add(node); parentId = node.ParentId; kind = ExpectedParent(node.Kind);
        }
        if (parentId is not null) throw Invalid("invalid_organization_parent", "Root organizations cannot have a parent");
        if (existing is { Status: AuthorizationStatus.Active }) nodes.Add(existing);
        return nodes.ToImmutable();
    }

    public static AuthorizationDecision CanDecide(ActorContext actor, AuthorizationSnapshot snapshot, long requestedBy,
        DateTimeOffset now, bool withdraw = false)
    {
        if (requestedBy <= 0 || (withdraw ? actor.UserId != requestedBy : actor.UserId == requestedBy))
            return AuthorizationDecision.Deny(withdraw ? "original_requester_required" : "independent_actor_required");
        return ScopePolicy.Authorize(actor, snapshot, withdraw ? Permissions.TechnicalConfigure : Permissions.OrganizationApprove,
            new(IsAdministrative: true), now);
    }

    public static ScopeNodeKind? ExpectedParent(ScopeNodeKind kind) => kind switch
    {
        ScopeNodeKind.BuyingOrganization or ScopeNodeKind.FulfillmentOrganization => null,
        ScopeNodeKind.Branch => ScopeNodeKind.BuyingOrganization,
        ScopeNodeKind.Division => ScopeNodeKind.Branch,
        ScopeNodeKind.Shop or ScopeNodeKind.Channel => ScopeNodeKind.FulfillmentOrganization,
        ScopeNodeKind.Warehouse => ScopeNodeKind.Shop,
        _ => throw Invalid("invalid_organization_kind", "Unknown organization kind")
    };

    /// <summary>Potential restored access includes ancestor-wide scopes, even when their own
    /// required nodes were active throughout the target suspension. Status is deliberately ignored
    /// here; real activation still enforces current statuses and time in the authorization policy.</summary>
    public static bool IsAffectedScope(GrantScope scope, ScopeNode target, ScopeDirectory directory, ISet<long> descendantIds)
    {
        var structuralDirectory = new ScopeDirectory(directory.Nodes.Select(node => node with { Status = AuthorizationStatus.Active }).ToImmutableArray());
        if (scope.Kind == GrantScopeKind.System || !ScopePolicy.IsScopeActive(scope, structuralDirectory)) return false;
        if (ScopePolicy.RequiredNodes(scope, directory).Any(node => descendantIds.Contains(node.Id))) return true;
        if (target.Kind == ScopeNodeKind.Branch)
            return scope.Kind == GrantScopeKind.BuyingOrganization && scope.OrganizationId == target.ParentId;
        if (target.Kind == ScopeNodeKind.Division)
        {
            var branch = target.ParentId is { } id ? directory.Find(id, ScopeNodeKind.Branch) : null;
            return branch is not null && (scope.Kind == GrantScopeKind.BuyingOrganization && scope.OrganizationId == branch.ParentId ||
                scope.Kind == GrantScopeKind.BuyingBranch && scope.OrganizationId == branch.ParentId && scope.BranchId == branch.Id);
        }
        if (target.Kind == ScopeNodeKind.Warehouse)
        {
            var shop = target.ParentId is { } id ? directory.Find(id, ScopeNodeKind.Shop) : null;
            return shop is not null && scope.Kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.CourierAssignment &&
                scope.WarehouseId is null && scope.ShopId == shop.Id && scope.OrganizationId == shop.ParentId;
        }
        return false;
    }
    private static DomainValidationException Invalid(string code, string title) => new(code, title);
}
