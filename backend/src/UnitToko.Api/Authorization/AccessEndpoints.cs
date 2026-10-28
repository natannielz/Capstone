using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;

namespace UnitToko.Api.Authorization;

public static partial class AccessEndpoints
{
    private static readonly string[] AdministrativeActions =
    [Permissions.AccessPropose, Permissions.AccessApprove, Permissions.AccessRevoke,
        Permissions.MembershipPropose, Permissions.MembershipApprove, Permissions.AccessAuditRead,
        Permissions.TechnicalConfigure, Permissions.OrganizationApprove,
        Permissions.PersonReviewPropose, Permissions.PersonReviewApprove, Permissions.PersonReviewRead, Permissions.CatalogManage];

    public static void MapAccessEndpoints(this WebApplication app, bool currentOnly = false)
    {
        var group = app.MapGroup("/api/v1/access").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapGet("/current", async (HttpContext http, AuthorizationStore store, IClock clock, CancellationToken ct) =>
        {
            var actor = Actor(http);
            var snapshot = await store.ReadCurrentAsync(actor, ct);
            var scopes = snapshot.Memberships.SelectMany(member => ScopePolicy.RequiredNodes(member.Scope, snapshot.Directory))
                .DistinctBy(node => (node.Kind, node.Id)).Select(node => new AccessNodeDto(new(node.Id), node.Kind,
                    node.ParentId.HasValue ? new LongId(node.ParentId.Value) : null,
                    node.Status.ToString().ToLowerInvariant(), AccessResponses.Version(node.Version))).ToArray();
            var result = new CurrentAccessDto(new(snapshot.UserId), AccessResponses.Version(snapshot.GrantVersion),
                snapshot.Status.ToString().ToLowerInvariant(),
                snapshot.Memberships.Select(member => new AccessMembershipDto(new(member.Id), AccessScopeDto.From(member.Scope),
                    member.Status.ToString().ToLowerInvariant(), AccessResponses.Version(member.Version), [], member.EffectiveAt, member.ExpiresAt)).ToArray(),
                snapshot.Grants.Select(grant => new AccessGrantDto(new(grant.Id), new(grant.MembershipId), grant.TemplateId,
                    grant.TemplateVersion, [.. grant.Permissions], AccessScopeDto.From(grant.Scope),
                    grant.Status.ToString().ToLowerInvariant(), AccessResponses.Version(grant.Version), [],
                    grant.DelegablePermissions.IsDefault ? [] : [.. grant.DelegablePermissions], grant.EffectiveAt, grant.ExpiresAt)).ToArray(), scopes,
                await OfferedActionsAsync(http, actor, snapshot, CurrentActions(actor, snapshot, clock.UtcNow), ct));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(snapshot.GrantVersion);
            return TypedResults.Ok(result);
        }).WithName("CurrentAccess");

        if (currentOnly) return;

        group.MapGet("/catalog", async (HttpContext http, AuthorizationStore store, IClock clock, CancellationToken ct) =>
        {
            var actor = Actor(http);
            var snapshot = await store.ReadCurrentAsync(actor, ct);
            var actions = AvailableActions(actor, snapshot, clock.UtcNow);
            if (actions.Length == 0) throw new AuthorizationDeniedException("permission_denied");
            return TypedResults.Ok(new AccessCatalogDto(
                RoleTemplates.Templates.Values.OrderBy(role => role.Id).Select(role => new AccessRoleDto(
                    role.Id, RoleLabel(role.Id), role.Version, [.. role.Permissions],
                    RoleTemplates.Suggestions.TryGetValue(role.Id, out var suggestions) ? [.. suggestions] : [])).ToArray(),
                RoleTemplates.FinancePacks.OrderBy(pack => pack.Key).Select(pack => new AccessPackDto(pack.Key,
                    PackLabel(pack.Key), [.. pack.Value])).ToArray(),
                Permissions.Catalog.Values.OrderBy(permission => permission.Name).Select(permission => new AccessPermissionDto(
                    permission.Name, permission.Description, [.. permission.AllowedScopes],
                    permission.Assurance.MaximumAge.HasValue)).ToArray(), actions));
        }).WithName("AccessCatalog");

        group.MapGet("/subjects/{userId}", async (string userId, HttpContext http, AccessAdministrationService service, CancellationToken ct) =>
        {
            var result = await service.ReadSubjectAsync(Actor(http), LongId.Parse(userId).Value, ct,
                audit: new("access-review", http.TraceIdentifier));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.GrantVersion);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("AccessSubject");

        group.MapGet("/proposals", async (HttpContext http, AccessAdministrationService service, CancellationToken ct,
            int offset = 0, int limit = 25) =>
        {
            if (offset < 0 || offset > 10_000 || limit is < 1 or > 100)
                throw new DomainValidationException("invalid_pagination", "Use a bounded page size and offset");
            return TypedResults.Ok((await service.ListProposalsAsync(Actor(http), offset, limit, ct,
                audit: new("access-review", http.TraceIdentifier)))
                .Select(AccessResponses.From).ToArray());
        }).WithName("AccessProposals");

        group.MapPost("/grants/preview", async (GrantRequestDto request, HttpContext http,
            AccessAdministrationService service, CancellationToken ct) =>
            TypedResults.Ok(AccessResponses.From(await service.PreviewGrantAsync(Actor(http), request.ToDomain(), ct,
                audit: new("access-review", http.TraceIdentifier)))))
            .WithName("PreviewAccessGrant");

        group.MapPost("/grants/proposals", async (GrantProposalDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Grant is null) throw new DomainValidationException("invalid_grant", "A grant proposal is required");
            var result = await service.ProposeGrantAsync(Actor(http), request.Grant.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("ProposeAccessGrant");

        group.MapPost("/memberships/proposals", async (MembershipProposalDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Membership is null) throw new DomainValidationException("invalid_membership", "A membership proposal is required");
            var result = await service.ProposeMembershipAsync(Actor(http), request.Membership.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("ProposeMembership");

        MapChange(group, "/grants/proposals/{id}/approve", "ApproveAccessGrant",
            (service, actor, id, version, metadata, ct) => service.ApproveGrantAsync(actor, id, version, metadata, ct));
        MapChange(group, "/memberships/proposals/{id}/approve", "ApproveMembership",
            (service, actor, id, version, metadata, ct) => service.ApproveMembershipAsync(actor, id, version, metadata, ct));
        MapChange(group, "/grants/{id}/revoke", "RevokeAccessGrant",
            (service, actor, id, version, metadata, ct) => service.RevokeGrantAsync(actor, id, version, metadata, ct));
        MapChange(group, "/memberships/{id}/suspend", "SuspendMembership",
            (service, actor, id, version, metadata, ct) => service.SuspendMembershipAsync(actor, id, version, metadata, ct));
        MapChange(group, "/subjects/{id}/suspend", "SuspendIdentity",
            (service, actor, id, version, metadata, ct) => service.SuspendIdentityAsync(actor, id, version, metadata, ct));
        MapChange(group, "/scopes/{id}/suspend", "SuspendAccessScope",
            (service, actor, id, version, metadata, ct) => service.SuspendScopeAsync(actor, id, version, metadata, ct));
        MapLifecycle(group);
        MapHistoricalGrants(group);
    }

    private static void MapChange(RouteGroupBuilder group, string path, string name,
        Func<AccessAdministrationService, ActorContext, long, long, AuthorizationCommandMetadata,
            CancellationToken, Task<AccessChangeResult>> execute) =>
        group.MapPost(path, async (string id, AccessReasonDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName(name);

    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");

    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_reason", "A reason of at most 1000 characters is required");
        return new(AccessRequestMetadata.IdempotencyKey(http.Request), reason.Trim(), http.TraceIdentifier);
    }

    /// <summary>Capabilities that widen what a grant's scope covers (a branch or the whole buying organisation). They are part of a
    /// grant, never a task a page offers, so they are not listed among the actions.</summary>
    internal static readonly string[] ScopeCapabilities = [Permissions.BranchPic, Permissions.OrganizationWide];
    /// <summary>Every permission a page may check. Pages decide what to offer from these names, so the list is the whole catalog:
    /// a hand-kept list silently hid functions people were granted (F-001: invitations and account recovery). Report reading keeps
    /// its own shop-only rule (<see cref="ReportingActions"/>).</summary>
    internal static IReadOnlyList<string> DiscoverablePermissions { get; } =
        [.. Permissions.Catalog.Keys.Where(permission => !ScopeCapabilities.Contains(permission)).Order(StringComparer.Ordinal)];

    // UI task discovery is separate from administrative catalog admission. A permission is offered when the actor may use it now, or
    // when it needs a fresh factor the actor can renew: reviewers keep the verification entry when MFA ages; every protected read or
    // command still enforces current assurance.
    internal static string[] CurrentActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now) =>
        AvailableActions(actor, snapshot, now).Concat(ReportingActions(actor, snapshot, now))
            .Concat(DiscoverablePermissions.Where(permission => permission != Permissions.ReportRead &&
                (ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed || ScopePolicy.CanRequestStepUp(actor, snapshot, permission, now))))
            .Distinct(StringComparer.Ordinal).ToArray();
    /// <summary>Pages offer a function only when it can work for this account. catalog.read means "browse the organisation catalog"
    /// to every page, so staff whose catalog.read serves their own shop work, without a catalog entitlement, are not offered it
    /// (G-032). Person reviews are not offered while no review trust adapter is configured (G-039). Grants are unchanged.</summary>
    private static async Task<string[]> OfferedActionsAsync(HttpContext http, ActorContext actor, AuthorizationSnapshot snapshot, string[] actions,
        CancellationToken ct)
    {
        var offered = actions.ToList();
        if (offered.Contains(Permissions.CatalogRead) && (http.RequestServices.GetService<UnitToko.Platform.Catalog.CatalogVisibilityStore>() is not { } catalog ||
            !await catalog.CanBrowseAsync(actor, snapshot, ct))) offered.Remove(Permissions.CatalogRead);
        if (http.RequestServices.GetService<UnitToko.Platform.Identity.PersonWithdrawalService>() is not { ReviewAvailable: true })
            offered.RemoveAll(permission => permission is Permissions.PersonReviewRead or Permissions.PersonReviewPropose or Permissions.PersonReviewApprove);
        return [.. offered];
    }
    private static string[] AvailableActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now) =>
        AdministrativeActions.Where(permission => ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed).ToArray();

    private static string[] ReportingActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now)
    {
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.ReportRead, now);
        return query.IsAllowed && query.Clauses.Any(clause => clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
            ? [Permissions.ReportRead] : [];
    }

    private static string RoleLabel(string id) => id switch
    {
        "buyer" => "Pembeli", "store-staff" => "Staf toko", "store-head" => "Kepala toko",
        "courier" => "Kurir", "finance" => "Keuangan", "system-admin" => "Administrator sistem",
        "branch-approver" => "Penyetuju belanja cabang", "catalog-officer" => "Petugas katalog", "catalog-reviewer" => "Peninjau harga dan katalog tamu",
        "dispatcher" => "Petugas pengiriman", "finance-maker" => "Keuangan penagihan", "finance-checker" => "Pemeriksa pembayaran",
        "procurement-officer" => "Petugas pengadaan", "procurement-approver" => "Penyetuju pengadaan", "warehouse-receiver" => "Petugas penerimaan gudang", _ => id
    };
    private static string PackLabel(string id) => id switch
    {
        "reporting" => "Pelaporan", "billing" => "Penagihan", "approval" => "Persetujuan",
        "accounting" => "Akuntansi", _ => id
    };
}
