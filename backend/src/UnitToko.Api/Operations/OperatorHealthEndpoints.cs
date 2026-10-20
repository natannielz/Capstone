using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Operations;

namespace UnitToko.Api.Operations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorHealthQueryDto([property: JsonRequired] string Purpose);

/// <summary>Q10: the operator health report (queues, last purge, schema and replication). Counts and ages only.</summary>
public static class OperatorHealthEndpoints
{
    public static void MapOperatorHealthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/operations/health").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (OperatorHealthQueryDto request, HttpContext http, [FromServices] OperatorHealthService service, CancellationToken ct) => {
            if (request.Purpose != "operator-health") throw new DomainValidationException("invalid_health_query", "Health reads state their purpose");
            http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.Ok(await service.ReadAsync(Actor(http), ct));
        }).WithName("QueryOperatorHealth");
    }

    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
}
