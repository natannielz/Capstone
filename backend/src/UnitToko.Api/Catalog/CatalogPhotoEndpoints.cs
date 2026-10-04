using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;

namespace UnitToko.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoKeyDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoReasonDto([property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoUploadRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoReferenceDto([property: JsonRequired] LongId Id, [property: JsonRequired] string Version,
    [property: JsonRequired] string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoAssociateDto([property: JsonRequired] string ExpectedPhotoRevision,
    [property: JsonRequired] LongId ReservationId, [property: JsonRequired] CatalogPhotoReferenceDto Reference,
    [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPhotoOriginalDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] CatalogPhotoReferenceDto Reference);
public sealed record CatalogPhotoSelectionDto(LongId AssociationId, LongId ReservationId, CatalogPhotoReferenceDto Reference,
    string Mime, DateTimeOffset RecordedAt, bool MatchesCurrentDefinition);
public sealed record CatalogPhotoCurrentDto(LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion,
    string PhotoRevision, CatalogPhotoSelectionDto? Photo);
public sealed record CatalogPhotoResultDto(LongId AssociationId, LongId SkuId, LongId DefinitionId,
    string DefinitionVersion, string SkuRevision, string PhotoRevision, DateTimeOffset RecordedAt);
public sealed record CatalogPhotoRecoveryDto(CatalogPhotoResultDto? Result);
public sealed record CatalogPhotoReservationDto(LongId ReservationId, string Revision, DateTimeOffset ExpiresAt);
public sealed record CatalogPhotoReservationRecoveryDto(string Status, CatalogPhotoReservationDto? Reservation);
public sealed record CatalogPhotoStatusDto(LongId ReservationId, string State, string Revision, DateTimeOffset ExpiresAt,
    CatalogPhotoReferenceDto? Reference, string ScanStatus, string DecoderStatus, string? SafeFailure);
public sealed record CatalogPhotoUploadOutcomeDto(string State, CatalogPhotoReferenceDto? Reference);

public static class CatalogPhotoEndpoints
{
    public static void MapCatalogPhotoEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/catalog/skus/{id}").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        Problems(group);
        group.MapPost("/primary-photo/query", async (string id, CatalogPhotoReadDto request, HttpContext http,
            CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, CancellationToken ct) => {
            Purpose(request.Purpose);
            var row = await service.CurrentAsync(Actor(http), LongId.Parse(id).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.SkuRevision);
            NoStore(http);
            return TypedResults.Ok(Current(row));
        }).WithName("ReadCatalogPrimaryPhoto");
        group.MapPost("/primary-photo", async (string id, CatalogPhotoAssociateDto request, HttpContext http,
            CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected,
            CancellationToken ct) => {
            var input = new CatalogAssociatePrimaryPhoto(LongId.Parse(id), PositiveVersion(http),
                PhotoRevision(request.ExpectedPhotoRevision), request.ReservationId.Value, Ref(request.Reference));
            var row = await service.AssociateAsync(Actor(http), input, Metadata(http, request.Reason), ct);
            NoStore(http);
            return TypedResults.Ok(Result(row));
        }).WithName("AssociateCatalogPrimaryPhoto");
        group.MapPost("/photo-evidence", async (string id, CatalogPhotoReasonDto request, HttpContext http,
            CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected,
            CancellationToken ct) => {
            var row = await service.ReserveAsync(Actor(http), LongId.Parse(id).Value, PositiveVersion(http), Metadata(http, request.Reason), ct);
            NoStore(http);
            return TypedResults.Ok(Reservation(row));
        }).WithName("ReserveCatalogPhotoEvidence");
        group.MapPost("/photo-evidence/operations/query", async (string id, CatalogPhotoKeyDto request, HttpContext http,
            CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, CancellationToken ct) => {
            Purpose(request.Purpose);
            var row = await service.RecoverReservationAsync(Actor(http), LongId.Parse(id).Value, request.Key, ct);
            NoStore(http);
            return TypedResults.Ok(new CatalogPhotoReservationRecoveryDto(row is null ? "not_confirmed" : "committed",
                row is null ? null : Reservation(row)));
        }).WithName("RecoverCatalogPhotoReservation");
        group.MapPost("/photo-evidence/{reservationId}/query", async (string id, string reservationId, CatalogPhotoReadDto request,
            HttpContext http, CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId,
            CancellationToken ct) => {
            Purpose(request.Purpose);
            var row = await service.StatusAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value, ct);
            NoStore(http);
            return TypedResults.Ok(Status(row));
        }).WithName("ReadCatalogPhotoEvidenceStatus");
        group.MapPost("/photo-evidence/{reservationId}/content", async (string id, string reservationId, HttpContext http,
            CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected,
            CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original image bytes", StatusCodes.Status415UnsupportedMediaType);
            using var source = new CatalogPhotoBodyStream(http.Request.Body);
            var row = await service.UploadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                PositiveVersion(http), source, Metadata(http, "Upload original catalog photograph"), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            NoStore(http);
            return TypedResults.Ok(Outcome(row));
        }).WithMetadata(CatalogPhotoBodyStream.Metadata).WithName("UploadCatalogPhotoEvidence");
        group.MapPost("/photo-evidence/{reservationId}/recover", async (string id, string reservationId,
            CatalogPhotoUploadRecoveryDto request, HttpContext http, CatalogPhotoWorkflow service,
            [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            Purpose(request.Purpose);
            var row = await service.RecoverUploadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                Metadata(http, request.Reason), ct);
            NoStore(http);
            return TypedResults.Ok(Outcome(row));
        }).WithName("RecoverCatalogPhotoUpload");
        group.MapPost("/photo-evidence/{reservationId}/original/query", async (string id, string reservationId,
            CatalogPhotoOriginalDto request, HttpContext http, CatalogPhotoWorkflow service,
            [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, [FromHeader(Name = "If-Match")] string expected,
            CancellationToken ct) => {
            Purpose(request.Purpose, "catalog-photo-association");
            NoRange(http);
            var row = await service.OpenStaffOriginalAsync(Actor(http), LongId.Parse(id).Value, PositiveVersion(http),
                LongId.Parse(reservationId).Value, Ref(request.Reference), new(request.Purpose, http.TraceIdentifier), ct);
            return Original(http, row);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").WithName("ReadCatalogPhotoUploadOriginal");
        group.MapPost("/primary-photo/original/query", async (string id, CatalogPhotoReadDto request, HttpContext http,
            CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, CancellationToken ct) => {
            Purpose(request.Purpose); NoRange(http);
            var row = await service.OpenCurrentStaffAsync(Actor(http), LongId.Parse(id).Value,
                new("catalog-photo-current", http.TraceIdentifier), ct);
            NoStore(http);
            return row is null ? Results.NoContent() : Original(http, row);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").Produces(204).WithName("ReadCurrentCatalogPrimaryPhotoOriginal");

        var recovery = app.MapGroup("/api/v1/catalog/primary-photos").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        Problems(recovery);
        recovery.MapPost("/operations/query", async (CatalogPhotoKeyDto request, HttpContext http, CatalogPhotoWorkflow service,
            [FromHeader(Name = "X-Expected-Actor-Id")] string actorId, CancellationToken ct) => {
            Purpose(request.Purpose);
            var row = await service.LookupAssociationAsync(Actor(http), request.Key, ct);
            NoStore(http);
            return TypedResults.Ok(new CatalogPhotoRecoveryDto(row is null ? null : Result(row)));
        }).WithName("RecoverCatalogPrimaryPhoto");

        var buyer = app.MapGroup("/api/v1/catalog/variants").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        Problems(buyer);
        buyer.MapPost("/{publicId}/primary-photo/query", async (string publicId, CatalogPhotoReadDto request,
            HttpContext http, CatalogPhotoWorkflow service, [FromHeader(Name = "X-Expected-Actor-Id")] string actorId,
            CancellationToken ct) => {
            Purpose(request.Purpose); NoRange(http);
            var row = await service.OpenBuyerAsync(Actor(http), publicId, new("catalog-photo-current", http.TraceIdentifier), ct);
            NoStore(http);
            return row is null ? Results.NoContent() : Original(http, row);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").Produces(204).WithName("ReadBuyerCatalogPrimaryPhotoOriginal");
    }

    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static void Problems(RouteGroupBuilder group) {
        foreach (var code in new[] { 400, 401, 403, 409, 413, 415, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(code, typeof(ApiProblem), ["application/problem+json"]));
    }
    private static void Purpose(string purpose, string expected = "catalog-photo") {
        if (purpose != expected) throw Invalid();
    }
    private static long PhotoRevision(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision is < 0 or >= 256 || value != revision.ToString(CultureInfo.InvariantCulture)) throw Invalid();
        return revision;
    }
    private static long PositiveVersion(HttpContext http) {
        var value = AccessRequestMetadata.ExpectedVersion(http.Request);
        return value > 0 ? value : throw Invalid();
    }
    private static EvidenceRef Ref(CatalogPhotoReferenceDto value) => value is null ? throw Invalid() :
        new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason) =>
        new(AccessRequestMetadata.IdempotencyKey(http.Request), reason, http.TraceIdentifier);
    private static CatalogPhotoReferenceDto Ref(EvidenceRef value) => new(new(value.Id), I(value.Version), value.Sha256);
    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static CatalogPhotoResultDto Result(CatalogPrimaryPhotoResult value) => new(value.AssociationId,
        value.SkuId, value.DefinitionId, I(value.DefinitionVersion), I(value.SkuRevision), I(value.PhotoRevision), value.RecordedAt);
    private static CatalogPhotoCurrentDto Current(CatalogPrimaryPhotoCurrent value) => new(value.SkuId, I(value.SkuRevision),
        value.DefinitionId, I(value.DefinitionVersion), I(value.PhotoRevision), value.Photo is null ? null :
            new(value.Photo.AssociationId, new(value.Photo.ReservationId), Ref(value.Photo.Reference), value.Photo.Mime,
                value.Photo.RecordedAt, value.Photo.MatchesCurrentDefinition));
    private static CatalogPhotoReservationDto Reservation(ReservationResult value) => new(new(value.ReservationId), I(value.Revision), value.ExpiresAt);
    private static CatalogPhotoStatusDto Status(SafeStatus value) => new(new(value.ReservationId), value.State, I(value.Revision),
        value.ExpiresAt, value.Reference is null ? null : Ref(value.Reference), value.ScanStatus, value.DecoderStatus, value.SafeFailure);
    private static CatalogPhotoUploadOutcomeDto Outcome(UploadOutcome value) => new(value.State, value.Evidence is null ? null : Ref(value.Evidence.Reference));
    private static DomainValidationException Invalid() => new("invalid_catalog_photo_request", "Choose a current catalog photo operation");
    private static void NoRange(HttpContext http) {
        if (http.Request.Headers.ContainsKey("Range")) throw Invalid();
    }
    private static void NoStore(HttpContext http) {
        http.Response.Headers.CacheControl = "private, no-store, max-age=0";
        http.Response.Headers.Pragma = "no-cache";
    }
    private static IResult Original(HttpContext http, ProtectedEvidenceRead value) {
        NoStore(http);
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Content-Sha256"] = value.Reference.Sha256;
        http.Response.ContentLength = value.Length;
        return Results.Stream(value.Content, value.Mime, enableRangeProcessing: false);
    }
}

internal sealed class CatalogPhotoBodyStream(Stream source) : Stream
{
    internal sealed class UploadMetadata;
    internal static readonly UploadMetadata Metadata = new();
    internal const int MaximumBytes = 10 * 1024 * 1024;
    private long consumed;
    internal bool Exceeded { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => consumed; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        var size = (int)Math.Min(buffer.Length, MaximumBytes - consumed + 1);
        var read = await source.ReadAsync(buffer[..size], ct); consumed += read;
        if (consumed > MaximumBytes) { Exceeded = true; throw new IOException("Catalog photograph upload limit exceeded"); }
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
