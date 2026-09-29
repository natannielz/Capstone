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

    public static void MapAccessEndpoints(this WebApplication app)
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
                CurrentActions(actor, snapshot, clock.UtcNow));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(snapshot.GrantVersion);
            return TypedResults.Ok(result);
        }).WithName("CurrentAccess");

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

    // UI task discovery is separate from administrative catalog admission. Source reviewers retain
    // the verification entry when MFA ages; every source read or command still enforces current assurance.
    private static string[] CurrentActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now) =>
        AvailableActions(actor, snapshot, now).Concat(new[] { Permissions.StockRead, Permissions.StockCount,
                Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourceApprove, Permissions.StockSourceSuspend }
            .Where(permission => ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed ||
                (permission is Permissions.StockSourceApprove or Permissions.StockSourceSuspend) && ScopePolicy.CanRequestStepUp(actor, snapshot, permission, now))).ToArray();
    private static string[] AvailableActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now) =>
        AdministrativeActions.Where(permission => ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed).ToArray();

    private static string RoleLabel(string id) => id switch
    {
        "buyer" => "Pembeli", "store-staff" => "Staf toko", "store-head" => "Kepala toko",
        "courier" => "Kurir", "finance" => "Keuangan", "system-admin" => "Administrator sistem", _ => id
    };
    private static string PackLabel(string id) => id switch
    {
        "reporting" => "Pelaporan", "billing" => "Penagihan", "approval" => "Persetujuan",
        "accounting" => "Akuntansi", _ => id
    };
}
