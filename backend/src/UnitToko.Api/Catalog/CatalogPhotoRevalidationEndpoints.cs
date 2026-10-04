using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;

namespace UnitToko.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoRevalidationRequestDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string ExpectedProofRevision,
    [property: JsonRequired] CatalogPhotoReferenceDto Reference,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoRevalidationQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] CatalogPhotoReferenceDto Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoRevalidationRecoveryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Key);
public sealed record CatalogPhotoRevalidationResultDto(LongId RequestId, LongId SkuId, LongId VersionRowId,
    string State, DateTimeOffset RecordedAt);
public sealed record CatalogPhotoRevalidationCurrentDto(LongId SkuId, LongId VersionRowId,
    string SkuRevision, string ProofRevision, string State, DateTimeOffset? CheckedAt,
    string? Mime, string? PipelineId);
public sealed record CatalogPhotoRevalidationRecoveryDto(CatalogPhotoRevalidationResultDto? Result);

public static class CatalogPhotoRevalidationEndpoints
{
    private const string ExpectedPurpose = "catalog-image-revalidation";

    public static void MapCatalogPhotoRevalidationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/catalog/skus/{id}/photo-evidence/{reservationId}/revalidation")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        Problems(group);
        group.MapPost("", async (string id, string reservationId, CatalogPhotoRevalidationRequestDto request,
            HttpContext http, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected,
            CancellationToken ct) => {
            Purpose(request.Purpose);
            var input = new RequestCatalogImageRevalidation(LongId.Parse(id), PositiveVersion(http),
                ProofRevision(request.ExpectedProofRevision), LongId.Parse(reservationId).Value, Reference(request.Reference));
            var runtime = Runtime(http);
            if (runtime is null) return await Unavailable(http);
            var row = await runtime.RequestCatalogImageAsync(Actor(http), input,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason,
                    http.TraceIdentifier), ct);
            NoStore(http);
            return Results.Ok(Result(row));
        }).WithName("RequestCatalogPhotoRevalidation");
        group.MapPost("/query", async (string id, string reservationId, CatalogPhotoRevalidationQueryDto request,
            HttpContext http, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, CancellationToken ct) => {
            Purpose(request.Purpose);
            var skuId = LongId.Parse(id).Value;
            var originalId = LongId.Parse(reservationId).Value;
            var reference = Reference(request.Reference);
            var runtime = Runtime(http);
            if (runtime is null) return await Unavailable(http);
            var row = await runtime.CurrentCatalogImageAsync(Actor(http), skuId, originalId, reference, ct);
            NoStore(http);
            return Results.Ok(Current(row));
        }).WithName("ReadCatalogPhotoRevalidation");

        var recovery = app.MapGroup("/api/v1/catalog/photo-revalidations")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        Problems(recovery);
        recovery.MapPost("/operations/query", async (CatalogPhotoRevalidationRecoveryQueryDto request,
            HttpContext http, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, CancellationToken ct) => {
            Purpose(request.Purpose);
            if (string.IsNullOrWhiteSpace(request.Key)) throw Invalid();
            var runtime = Runtime(http);
            if (runtime is null) return await Unavailable(http);
            var row = await runtime.LookupCatalogImageAsync(Actor(http), request.Key, ct);
            NoStore(http);
            return Results.Ok(new CatalogPhotoRevalidationRecoveryDto(row is null ? null : Result(row)));
        }).WithName("RecoverCatalogPhotoRevalidation");
    }

    private static EvidenceImageRevalidationRuntime? Runtime(HttpContext http)
    {
        try { return http.RequestServices.GetService<EvidenceImageRevalidationRuntime>(); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<IResult> Unavailable(HttpContext http)
    {
        NoStore(http);
        await global::Problems.WriteAsync(http, 503, "image_revalidation_runtime_unavailable",
            "Private image revalidation is unavailable");
        return Results.Empty;
    }

    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string purpose) {
        if (purpose != ExpectedPurpose) throw Invalid();
    }
    private static long PositiveVersion(HttpContext http) {
        var value = AccessRequestMetadata.ExpectedVersion(http.Request);
        return value > 0 ? value : throw Invalid();
    }
    private static long ProofRevision(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision is < 0 or >= 256 || value != revision.ToString(CultureInfo.InvariantCulture)) throw Invalid();
        return revision;
    }
    private static EvidenceRef Reference(CatalogPhotoReferenceDto? value) => value is null ? throw Invalid() :
        new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
    private static CatalogPhotoRevalidationResultDto Result(CatalogImageRevalidationRequestResult value) =>
        new(value.RequestId, value.SkuId, value.VersionRowId, value.State, value.RecordedAt);
    private static CatalogPhotoRevalidationCurrentDto Current(CatalogImageRevalidationCurrent value) =>
        new(value.SkuId, value.VersionRowId, I(value.SkuRevision), I(value.ProofRevision), value.State,
            value.CheckedAt, value.Mime, value.PipelineId);
    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DomainValidationException Invalid() =>
        new("invalid_image_revalidation_request", "Choose a current Catalog SKU image and proof revision");
    private static void NoStore(HttpContext http) {
        http.Response.Headers.CacheControl = "private, no-store, max-age=0";
        http.Response.Headers.Pragma = "no-cache";
    }
    private static void Problems(RouteGroupBuilder group) {
        foreach (var code in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(code, typeof(ApiProblem), ["application/problem+json"]));
    }
}
