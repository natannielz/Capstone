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

public static class CatalogIdentifierEndpoints
{
    public static void MapCatalogIdentifierEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/catalog/administration").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        group.MapPost("/skus/{id}/identifiers/query", async (string id, CatalogIdentifierReadDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            var result = await service.IdentifiersAsync(Actor(http), LongId.Parse(id).Value, Read(http, request.Purpose, false), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Catalog.Sku.Revision);
            return TypedResults.Ok(new CatalogIdentifierViewDto(CatalogResponses.From(result.Catalog), result.Identifiers.Select(From).ToArray()));
        }).WithName("CatalogIdentifierQuery");
        group.MapPost("/skus/{id}/identifiers", async (string id, CatalogIdentifierAssignDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.AssignIdentifierAsync(Actor(http), new(LongId.Parse(id), Version(http), request.DefinitionId,
                request.Kind, request.SourceAccount, request.RawValue, request.ProvenanceReference), Command(http, request.Reason), ct)))
            .WithName("CatalogIdentifierAssign");
        group.MapPost("/skus/{id}/identifiers/{identifierId}/retire", async (string id, string identifierId, CatalogIdentifierRetireDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.RetireIdentifierAsync(Actor(http), new(LongId.Parse(id), Version(http), LongId.Parse(identifierId),
                Positive(request.ExpectedIdentifierRevision), request.ProvenanceReference), Command(http, request.Reason), ct)))
            .WithName("CatalogIdentifierRetire");
        group.MapPost("/skus/{id}/code", async (string id, CatalogIdentifierCorrectDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.CorrectSkuCodeAsync(Actor(http), new(LongId.Parse(id), Version(http), request.PrimaryIdentifierId,
                Positive(request.ExpectedIdentifierRevision), request.NewCode, request.ProvenanceReference), Command(http, request.Reason), ct)))
            .WithName("CatalogSkuCodeCorrect");
        group.MapPost("/identifiers/operations/query", async (CatalogIdentifierRecoveryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            var result = await service.ReadIdentifierReceiptAsync(Actor(http), request.Operation, request.Key, Read(http, request.Purpose, true), ct);
            return TypedResults.Ok(new CatalogIdentifierRecoveryResultDto(result is null ? "not_confirmed" : "confirmed", request.Operation,
                result is null ? null : From(result)));
        }).WithName("CatalogIdentifierRecovery");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static long Version(HttpContext http)
    {
        var value = AccessRequestMetadata.ExpectedVersion(http.Request); if (value <= 0) throw CatalogIdentifierPolicy.Invalid(); return value;
    }
    private static long Positive(string value) => LongId.Parse(value).Value;
    private static AuthorizationCommandMetadata Command(HttpContext http, string reason) => new(AccessRequestMetadata.IdempotencyKey(http.Request), reason, http.TraceIdentifier);
    private static AuthorizationReadMetadata Read(HttpContext http, string purpose, bool recovery)
    {
        if (purpose != (recovery ? "operation-recovery" : "catalog-authoring")) throw CatalogIdentifierPolicy.Invalid();
        return new(purpose, http.TraceIdentifier);
    }
    private static Microsoft.AspNetCore.Http.HttpResults.Ok<CatalogIdentifierResultDto> Saved(HttpContext http, CatalogIdentifierResult value)
    {
        http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(value.SkuRevision); return TypedResults.Ok(From(value));
    }
    private static CatalogIdentifierDto From(CatalogIdentifier item) => new(item.Id, item.OrganizationId, item.SkuId, item.Kind, item.Namespace,
        item.RawValue, item.CanonicalValue, item.IsPrimary, item.EffectiveAt, item.RetiredAt, Number(item.Revision), item.ProvenanceReference, item.DefinitionId);
    private static CatalogIdentifierResultDto From(CatalogIdentifierResult item) => new(item.SkuId, Number(item.SkuRevision), item.IdentifierId, Number(item.IdentifierRevision), item.RecordedAt);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogIdentifierReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogIdentifierAssignDto([property: JsonRequired] string Kind, [property: JsonRequired] string? SourceAccount,
    [property: JsonRequired] string RawValue, [property: JsonRequired] LongId DefinitionId, [property: JsonRequired] string ProvenanceReference, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogIdentifierRetireDto([property: JsonRequired] string ExpectedIdentifierRevision,
    [property: JsonRequired] string ProvenanceReference, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogIdentifierCorrectDto([property: JsonRequired] LongId PrimaryIdentifierId, [property: JsonRequired] string ExpectedIdentifierRevision,
    [property: JsonRequired] string NewCode, [property: JsonRequired] string ProvenanceReference, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogIdentifierRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record CatalogIdentifierDto(LongId Id, LongId OrganizationId, LongId SkuId, string Kind, string Namespace, string RawValue,
    string CanonicalValue, bool IsPrimary, DateTimeOffset EffectiveAt, DateTimeOffset? RetiredAt, string Revision, string ProvenanceReference, LongId? DefinitionId);
public sealed record CatalogIdentifierViewDto(CatalogSkuDto Catalog, CatalogIdentifierDto[] Identifiers);
public sealed record CatalogIdentifierResultDto(LongId SkuId, string SkuRevision, LongId IdentifierId, string IdentifierRevision, DateTimeOffset RecordedAt);
public sealed record CatalogIdentifierRecoveryResultDto(string Status, string Operation, CatalogIdentifierResultDto? Result);
