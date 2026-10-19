using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Work;

namespace UnitToko.Api.Work;

public sealed record WorkSummaryQueryDto([property: JsonRequired] string Purpose);
public sealed record WorkItemDto(string Key, string Area, string Label, string Href, int Count, bool HasMore, DateTimeOffset? OldestAt, bool Unavailable);
public sealed record WorkSummaryDto(ImmutableArray<WorkItemDto> Items, DateTimeOffset ObservedAt);

/// <summary>"Pekerjaan saya": counts and oldest age per owner queue the actor may read. Each owner authorises its own read.</summary>
public static class WorkEndpoints
{
    public static void MapWorkEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/work").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/summary/query", async (WorkSummaryQueryDto request, HttpContext http, WorkSummaryService service, IClock clock, CancellationToken ct) => {
            if (request.Purpose != "work-summary") throw new DomainValidationException("invalid_read_purpose", "Use the stated work-summary purpose");
            http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache";
            var items = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, ct);
            return TypedResults.Ok(new WorkSummaryDto([.. items.Select(item => new WorkItemDto(item.Key, item.Area, item.Label, item.Href, item.Count, item.HasMore, item.OldestAt, item.Unavailable))], clock.UtcNow));
        }).WithName("ReadWorkSummary");
    }
}
