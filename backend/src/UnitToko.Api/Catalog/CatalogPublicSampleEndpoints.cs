using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPublicSampleQueryDto([property: JsonRequired] string Purpose, string? Search, int? Offset, int? Limit);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPublicSampleProposeDto([property: JsonRequired] LongId[] Adds, [property: JsonRequired] LongId[] Removes,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPublicSampleDecideDto([property: JsonRequired] string Reason);
/// <summary>OwnProposal tells the viewer whether they proposed it; who proposed stays in the audited history.</summary>
public sealed record CatalogPublicSamplePendingDto(LongId ProposalId, bool Adding, bool OwnProposal);
public sealed record CatalogPublicSampleItemDto(LongId FamilyId, string FamilyName, string OwnerLabel, string Packages, bool IsSample,
    CatalogPublicSamplePendingDto? Pending);
public sealed record CatalogPublicSampleChangeDto(LongId FamilyId, string FamilyName);
public sealed record CatalogPublicSampleProposalDto(LongId ProposalId, bool OwnProposal, string OwnerLabel, string Reason, DateTimeOffset ProposedAt,
    CatalogPublicSampleChangeDto[] Adds, CatalogPublicSampleChangeDto[] Removes);
public sealed record CatalogPublicSampleListDto(CatalogPublicSampleItemDto[] Items, string Total, CatalogPublicSampleProposalDto[] Proposals,
    string SampleCount, string SampleLimit);
public sealed record CatalogPublicSampleCommandDto(LongId ProposalId, string State);

/// <summary>Staff choose which products guests see as samples: a maker proposes, an independent checker with fresh MFA decides.</summary>
public static class CatalogPublicSampleEndpoints
{
    public static void MapCatalogPublicSampleEndpoints(this WebApplication app)
    {
        var staff = app.MapGroup("/api/v1/catalog/public-samples").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            staff.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        staff.MapPost("/query", async (CatalogPublicSampleQueryDto request, HttpContext http, [FromServices] CatalogPublicSampleService service, CancellationToken ct) =>
        {
            if (request.Purpose != "catalog-public-samples") throw Invalid();
            var page = await service.QueryAsync(Actor(http), request.Search, request.Offset ?? 0, request.Limit ?? 50, ct);
            return TypedResults.Ok(new CatalogPublicSampleListDto(
                [.. page.Items.Select(item => new CatalogPublicSampleItemDto(new(item.FamilyId), item.FamilyName, item.OwnerLabel, I(item.Packages), item.IsSample,
                    item.Pending is null ? null : new(new(item.Pending.ProposalId), item.Pending.Adding, item.Pending.OwnProposal)))],
                I(page.Total),
                [.. page.Proposals.Select(row => new CatalogPublicSampleProposalDto(new(row.ProposalId), row.OwnProposal, row.OwnerLabel, row.Reason, row.ProposedAt,
                    [.. row.Adds.Select(Change)], [.. row.Removes.Select(Change)]))],
                I(page.SampleCount), I(page.SampleLimit)));
        }).WithName("CatalogPublicSampleList");
        staff.MapPost("/proposals", async (CatalogPublicSampleProposeDto request, HttpContext http, [FromServices] CatalogPublicSampleService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Adds is null || request.Removes is null) throw Invalid();
            var row = await service.ProposeAsync(Actor(http), [.. request.Adds.Select(id => id.Value)], [.. request.Removes.Select(id => id.Value)],
                Metadata(http, request.Reason), ct);
            return TypedResults.Ok(Command(row));
        }).WithName("ProposeCatalogPublicSamples");
        foreach (var (action, decision, name) in new[] { ("approve", CatalogPublicSampleDecision.Approve, "ApproveCatalogPublicSamples"),
            ("reject", CatalogPublicSampleDecision.Reject, "RejectCatalogPublicSamples") })
            staff.MapPost("/proposals/{proposalId}/" + action, async (string proposalId, CatalogPublicSampleDecideDto request, HttpContext http,
                [FromServices] CatalogPublicSampleService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
            {
                var row = await service.DecideAsync(Actor(http), LongId.Parse(proposalId).Value, decision, Metadata(http, request.Reason), ct);
                return TypedResults.Ok(Command(row));
            }).WithName(name);
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static CatalogPublicSampleChangeDto Change(CatalogPublicSampleChange value) => new(new(value.FamilyId), value.FamilyName);
    private static CatalogPublicSampleCommandDto Command(MongoDB.Bson.BsonDocument row) => new(new(row["proposalId"].AsInt64), row["state"].AsString);
    private static DomainValidationException Invalid() => new("invalid_catalog_public_sample_request", "Complete the guest sample fields");
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason) => new(
        AccessRequestMetadata.IdempotencyKey(http.Request), CatalogAuthoringPolicy.Text(reason, 1000), http.TraceIdentifier);
}
