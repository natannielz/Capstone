using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Operations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Operations;

namespace UnitToko.Api.Operations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnomalyEvaluateDto([property: JsonRequired] string SourceKind, [property: JsonRequired] LongId SourceId,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnomalyReviewDto([property: JsonRequired] string Outcome, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnomalyPolicyDto([property: JsonRequired] string Version, [property: JsonRequired] string ApprovalReference,
    [property: JsonRequired] LongId? MaximumPackages, [property: JsonRequired] string? MaximumUnitPrice,
    [property: JsonRequired] string? Currency, [property: JsonRequired] string? MaximumRefund)
{
    internal AnomalyReviewPolicy Domain() => new(Version, ApprovalReference, MaximumPackages?.Value,
        MaximumUnitPrice is null ? null : DomainValues.ParseMoney(MaximumUnitPrice), Currency,
        MaximumRefund is null ? null : DomainValues.ParseMoney(MaximumRefund));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnomalyConfigureDto([property: JsonRequired] string SourceKind, [property: JsonRequired] LongId SourceId,
    [property: JsonRequired] LongId ExpectedRevision, [property: JsonRequired] AnomalyPolicyDto Policy, [property: JsonRequired] string Reason);

public static class OperationalAnomalyEndpoints
{
    public static void MapOperationalAnomalyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/operations/anomalies").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapGet("/sources", async (string kind, string? before, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.ChoicesAsync(Actor(http), kind, before is null ? null : DomainValues.ParseId(before), ct));
        }).WithName("ReadAnomalySources");
        group.MapGet("/latest", async (string sourceKind, string sourceId, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.LatestAsync(Actor(http), sourceKind, DomainValues.ParseId(sourceId), ct));
        }).WithName("ReadLatestAnomalyEvaluation");
        group.MapGet("/{id}", async (string id, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.ReadAsync(Actor(http), DomainValues.ParseId(id), ct));
        }).WithName("ReadAnomalyEvaluation");
        group.MapPost("/evaluate", async (AnomalyEvaluateDto request, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.EvaluateAsync(Actor(http), request.SourceKind, request.SourceId.Value, Metadata(request.Reason, http), ct));
        }).WithName("EvaluateOperationalAnomaly");
        group.MapPost("/{id}/disposition", async (string id, AnomalyReviewDto request, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.ReviewAsync(Actor(http), DomainValues.ParseId(id), request.Outcome, Metadata(request.Reason, http), ct));
        }).WithName("RecordAnomalyDisposition");
        group.MapPost("/policy", async (AnomalyConfigureDto request, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            if (request.Policy is null) throw new DomainValidationException("anomaly_policy_invalid", "Explicit policy values are required");
            NoStore(http); return TypedResults.Ok(await service.ConfigurePolicyAsync(Actor(http), request.SourceKind, request.SourceId.Value,
                request.ExpectedRevision.Value, request.Policy.Domain(), Metadata(request.Reason, http), ct));
        }).WithName("ConfigureAnomalyReviewPolicy");
        group.MapGet("/policy/receipt", async (string sourceKind, string sourceId, string key, HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.RecoverPolicyAsync(Actor(http), sourceKind, DomainValues.ParseId(sourceId), key, ct));
        }).WithName("RecoverAnomalyReviewPolicy");
        group.MapGet("/operations/receipt", async (string sourceKind, string sourceId, string operation, string key, string? evaluationId,
            HttpContext http, [FromServices] OperationalAnomalyService service, CancellationToken ct) => {
            NoStore(http); return TypedResults.Ok(await service.RecoverAsync(Actor(http), sourceKind, DomainValues.ParseId(sourceId), operation,
                key, evaluationId is null ? null : DomainValues.ParseId(evaluationId), ct));
        }).WithName("RecoverAnomalyOperation");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static AuthorizationCommandMetadata Metadata(string reason, HttpContext http) => new(AccessRequestMetadata.IdempotencyKey(http.Request), reason, http.TraceIdentifier);
    private static void NoStore(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff"; }
}
