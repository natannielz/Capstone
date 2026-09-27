using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;

namespace UnitToko.Api.Authorization;

public sealed record HistoricalGrantRequestDto(
    [property: JsonRequired] LongId UserId,
    [property: JsonRequired] LongId MembershipId,
    [property: JsonRequired] string[] Permissions,
    [property: JsonRequired] AccessScopeDto Scope,
    [property: JsonRequired] DateTimeOffset EffectiveAt,
    DateTimeOffset? ExpiresAt = null)
{
    public HistoricalGrantChangeRequest ToDomain()
    {
        if (UserId.Value <= 0 || MembershipId.Value <= 0 || Scope is null || EffectiveAt == default ||
            Permissions is null || Permissions.Length is < 1 or > 2 ||
            Permissions.Any(permission => permission is not (UnitToko.Modules.Identity.Permissions.HistoryRead or UnitToko.Modules.Identity.Permissions.ReportRead)) ||
            Permissions.Distinct(StringComparer.Ordinal).Count() != Permissions.Length)
            throw new DomainValidationException("invalid_historical_grant", "Select explicit historical read permissions and a reviewed target");
        return new(UserId.Value, MembershipId.Value, [.. Permissions], Scope.ToDomain(), EffectiveAt, ExpiresAt);
    }
}

public sealed record HistoricalGrantProposalDto([property: JsonRequired] HistoricalGrantRequestDto Grant,
    [property: JsonRequired] string Reason);


public sealed record HistoricalTargetQueryDto(
    [property: JsonRequired] LongId UserId,
    [property: JsonRequired] LongId MembershipId,
    int Offset = 0, int Limit = 100);
public sealed record HistoricalTargetDto(LongId Id, UnitToko.Modules.Organizations.ScopeNodeKind Kind, LongId? ParentId,
    string? Name, string? Code, string Status, string Version, AccessScopeDto Scope);
public sealed record HistoricalTargetPageDto(string Total, HistoricalTargetDto[] Items);

public static partial class AccessEndpoints
{
    private static void MapHistoricalGrants(RouteGroupBuilder group)
    {

        group.MapPost("/historical-targets/query", async (HistoricalTargetQueryDto request, HttpContext http,
            AccessAdministrationService service, CancellationToken ct) =>
        {
            var page = await service.ListHistoricalTargetsAsync(Actor(http), request.UserId.Value, request.MembershipId.Value,
                request.Offset, request.Limit, ct, ReadMetadata(http, "access-review"));
            return TypedResults.Ok(new HistoricalTargetPageDto(AccessResponses.Version(page.Total), page.Items.Select(item =>
                new HistoricalTargetDto(new(item.Id), item.Kind, item.ParentId is { } parent ? new LongId(parent) : null,
                    item.Name, item.Code, item.Status, AccessResponses.Version(item.Version), AccessScopeDto.From(item.Scope))).ToArray()));
        }).WithName("QueryHistoricalAccessTargets");

        group.MapPost("/historical-grants/preview", async (HistoricalGrantRequestDto request, HttpContext http,
            AccessAdministrationService service, CancellationToken ct) =>
            TypedResults.Ok(AccessResponses.From(await service.PreviewHistoricalGrantAsync(Actor(http), request.ToDomain(), ct,
                ReadMetadata(http, "access-review"))))).WithName("PreviewHistoricalAccessGrant");

        group.MapPost("/historical-grants/proposals", async (HistoricalGrantProposalDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Grant is null) throw new DomainValidationException("invalid_historical_grant", "A historical grant proposal is required");
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
                throw new DomainValidationException("invalid_reason", "A reason of at most 500 characters is required");
            var result = await service.ProposeHistoricalGrantAsync(Actor(http), request.Grant.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("ProposeHistoricalAccessGrant");
    }
}
