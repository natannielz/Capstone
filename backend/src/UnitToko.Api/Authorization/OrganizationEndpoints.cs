using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Organizations;

namespace UnitToko.Api.Authorization;

public sealed record OrganizationRequestDto([property: JsonRequired] OrganizationChangeKind ChangeKind,
    [property: JsonRequired] ScopeNodeKind Kind, [property: JsonRequired] string Code, [property: JsonRequired] string Name,
    LongId? ParentId = null, LongId? ScopeId = null, string ExpectedScopeVersion = "0", string ExpectedProfileVersion = "0")
{
    public OrganizationChangeRequest ToDomain() => new(ChangeKind, Kind, ParentId?.Value, Code, Name, ScopeId?.Value,
        ParseVersion(ExpectedScopeVersion), ParseVersion(ExpectedProfileVersion));
    private static long ParseVersion(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 0 || version == long.MaxValue || value != version.ToString(CultureInfo.InvariantCulture))
            throw new DomainValidationException("invalid_version", "Use a canonical nonnegative Int64 version string");
        return version;
    }
    public static OrganizationRequestDto From(OrganizationChangeRequest request) => new(request.ChangeKind, request.Kind,
        request.Code, request.Name, Id(request.ParentId), Id(request.ScopeId), AccessResponses.Version(request.ExpectedScopeVersion),
        AccessResponses.Version(request.ExpectedProfileVersion));
    internal static LongId? Id(long? value) => value.HasValue ? new LongId(value.Value) : null;
}
public sealed record OrganizationProposalRequestDto([property: JsonRequired] OrganizationRequestDto Change,
    [property: JsonRequired] string Reason);
public sealed record OrganizationDto(LongId Id, ScopeNodeKind Kind, LongId? ParentId, string Status,
    string ScopeVersion, string? Code, string? Name, string? ProfileVersion, string[] AllowedActions);
public sealed record OrganizationPageDto(string Total, OrganizationDto[] Items, string[] AllowedActions);
public sealed record OrganizationImpactDto(string AffectedScopes, string ActiveMemberships, string ActiveGrants, string AffectedAccounts, string Revision);
public sealed record OrganizationBeforeDto(string? Code, string? Name, string? Status, string ScopeVersion, string ProfileVersion);
public sealed record OrganizationProposalDto(LongId Id, OrganizationRequestDto Request, OrganizationImpactDto Impact,
    OrganizationBeforeDto Before, LongId RequestedBy, string Reason, string Status, string Version, DateTimeOffset CreatedAt,
    LongId? ApprovedBy, DateTimeOffset? ApprovedAt, LongId? DecisionBy, DateTimeOffset? DecisionAt, string? DecisionReason,
    LongId? ResultScopeId, string[] AllowedActions);
public sealed record OrganizationProposalPageDto(string Total, OrganizationProposalDto[] Items, string[] AllowedActions);
public sealed record OrganizationProposalResultDto(LongId Id, string Status, string Version);
public sealed record OrganizationChangeResultDto(LongId ProposalId, LongId? ScopeId, string Status, string ProposalVersion,
    string? ScopeVersion, string? ProfileVersion, string AffectedAccounts);

public static partial class AccessEndpoints
{
    public static void MapOrganizationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/organizations").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapGet("", async (HttpContext http, OrganizationMasterDataService service, CancellationToken ct, int offset = 0, int limit = 25) =>
        {
            var page = await service.ListAsync(Actor(http), offset, limit, new("access-review", http.TraceIdentifier), ct);
            return TypedResults.Ok(new OrganizationPageDto(AccessResponses.Version(page.Total), page.Items.Select(Organization).ToArray(), [.. page.AllowedActions]));
        }).WithName("Organizations");
        group.MapGet("/{id}", async (string id, HttpContext http, OrganizationMasterDataService service, CancellationToken ct) =>
            TypedResults.Ok(Organization(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, new("access-review", http.TraceIdentifier), ct))))
            .WithName("Organization");
        group.MapGet("/proposals", async (HttpContext http, OrganizationMasterDataService service, CancellationToken ct, int offset = 0, int limit = 25) =>
        {
            var page = await service.ListProposalsAsync(Actor(http), offset, limit, new("access-review", http.TraceIdentifier), ct);
            return TypedResults.Ok(new OrganizationProposalPageDto(AccessResponses.Version(page.Total), page.Items.Select(OrganizationProposal).ToArray(), [.. page.AllowedActions]));
        }).WithName("OrganizationProposals");
        group.MapGet("/proposals/{id}", async (string id, HttpContext http, OrganizationMasterDataService service, CancellationToken ct) =>
            TypedResults.Ok(OrganizationProposal(await service.ReadProposalAsync(Actor(http), LongId.Parse(id).Value, new("access-review", http.TraceIdentifier), ct))))
            .WithName("OrganizationProposal");
        group.MapPost("/proposals", async (OrganizationProposalRequestDto request, HttpContext http,
            OrganizationMasterDataService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Change is null) throw new DomainValidationException("invalid_organization_request", "An organization change is required");
            var result = await service.ProposeAsync(Actor(http), request.Change.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(new OrganizationProposalResultDto(new(result.Id), result.Status, AccessResponses.Version(result.Version)));
        }).WithName("ProposeOrganization");
        MapOrganizationDecision(group, "/proposals/{id}/approve", "ApproveOrganization", (service, actor, id, version, metadata, ct) => service.ApproveAsync(actor, id, version, metadata, ct));
        MapOrganizationDecision(group, "/proposals/{id}/reject", "RejectOrganization", (service, actor, id, version, metadata, ct) => service.RejectAsync(actor, id, version, metadata, ct));
        MapOrganizationDecision(group, "/proposals/{id}/withdraw", "WithdrawOrganization", (service, actor, id, version, metadata, ct) => service.WithdrawAsync(actor, id, version, metadata, ct));
    }

    private static void MapOrganizationDecision(RouteGroupBuilder group, string path, string name,
        Func<OrganizationMasterDataService, ActorContext, long, long, AuthorizationCommandMetadata, CancellationToken, Task<OrganizationChangeResult>> execute) =>
        group.MapPost(path, async (string id, AccessReasonDto request, HttpContext http, OrganizationMasterDataService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.ProposalVersion);
            return TypedResults.Ok(new OrganizationChangeResultDto(new(result.ProposalId), OrganizationRequestDto.Id(result.ScopeId), result.Status,
                AccessResponses.Version(result.ProposalVersion), result.ScopeVersion.HasValue ? AccessResponses.Version(result.ScopeVersion.Value) : null,
                result.ProfileVersion.HasValue ? AccessResponses.Version(result.ProfileVersion.Value) : null, AccessResponses.Version(result.AffectedAccounts)));
        }).WithName(name);

    private static OrganizationDto Organization(OrganizationSummary item) => new(new(item.Id), item.Kind, OrganizationRequestDto.Id(item.ParentId),
        item.Status, AccessResponses.Version(item.ScopeVersion), item.Code, item.Name,
        item.ProfileVersion.HasValue ? AccessResponses.Version(item.ProfileVersion.Value) : null, [.. item.AllowedActions]);
    private static OrganizationProposalDto OrganizationProposal(OrganizationProposalSummary item) => new(new(item.Id), OrganizationRequestDto.From(item.Request),
        new(AccessResponses.Version(item.Impact.AffectedScopes), AccessResponses.Version(item.Impact.ActiveMemberships), AccessResponses.Version(item.Impact.ActiveGrants),
            AccessResponses.Version(item.Impact.AffectedAccounts), item.Impact.Revision),
        new(item.Before.Code, item.Before.Name, item.Before.Status, AccessResponses.Version(item.Before.ScopeVersion), AccessResponses.Version(item.Before.ProfileVersion)),
        new(item.RequestedBy), item.Reason, item.Status, AccessResponses.Version(item.Version), item.CreatedAt,
        OrganizationRequestDto.Id(item.ApprovedBy), item.ApprovedAt, OrganizationRequestDto.Id(item.DecisionBy), item.DecisionAt, item.DecisionReason,
        OrganizationRequestDto.Id(item.ResultScopeId), [.. item.AllowedActions]);
}
