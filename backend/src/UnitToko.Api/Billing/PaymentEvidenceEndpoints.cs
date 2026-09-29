using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentEvidenceReserveDto([property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentEvidenceRecoverDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Reason);
public sealed record PaymentUploadReservationDto(LongId ReservationId, string Revision, DateTimeOffset ExpiresAt);
public sealed record PaymentUploadRecoveryDto(string Status, PaymentUploadReservationDto? Reservation);
public sealed record PaymentEvidenceReferenceDto(LongId Id, string Version, string Sha256);
public sealed record PaymentEvidenceStatusDto(LongId ReservationId, string State, string Revision, DateTimeOffset ExpiresAt,
    PaymentEvidenceReferenceDto? Reference, string ScanStatus, string DecoderStatus, string? SafeFailure);
public sealed record PaymentUploadOutcomeDto(string State, PaymentEvidenceReferenceDto? Reference);
public sealed record PaymentEvidencePageDto(ImmutableArray<PaymentEvidenceStatusDto> Items);

public static class PaymentEvidenceEndpoints
{
    public static void MapPaymentEvidenceEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-declarations/{id}/evidence").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, InvoiceQueryDto request, HttpContext http, PaymentEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(new PaymentEvidencePageDto([.. (await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.BeforeId?.Value, ct)).Select(Status)]));
        }).WithName("ListPaymentEvidence");
        group.MapPost("", async (string id, PaymentEvidenceReserveDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) =>
            TypedResults.Ok(Reservation(await service.ReserveAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)))).WithName("ReservePaymentEvidence");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentEvidenceService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentDraftEndpoints.Invalid();
            var row = await service.RecoverReservationAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("RecoverPaymentEvidenceReservation");
        group.MapPost("/{reservationId}/query", async (string id, string reservationId, OrderReadDto request, HttpContext http, PaymentEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Status(await service.StatusAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value, ct)));
        }).WithName("ReadPaymentEvidenceStatus");
        group.MapPost("/{reservationId}/content", async (string id, string reservationId, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            using var source = new PaymentEvidenceBodyStream(http.Request.Body);
            var result = await service.UploadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), source, new(AccessRequestMetadata.IdempotencyKey(http.Request), "Upload original payment evidence", http.TraceIdentifier), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithMetadata(PaymentEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("UploadPaymentEvidence");
        group.MapPost("/{reservationId}/recover", async (string id, string reservationId, PaymentEvidenceRecoverDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Purpose != "upload-recovery") throw PaymentDraftEndpoints.Invalid();
            var result = await service.RecoverUploadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithName("RecoverPaymentEvidenceUpload");
    }
    private static void Purpose(string value) { if (value != "payment-evidence") throw PaymentDraftEndpoints.Invalid(); }
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), OrderDraftInputDto.Number(row.Revision), row.ExpiresAt);
    private static PaymentEvidenceReferenceDto Reference(EvidenceRef value) => new(new(value.Id), OrderDraftInputDto.Number(value.Version), value.Sha256);
    private static PaymentEvidenceStatusDto Status(SafeStatus value) => new(new(value.ReservationId), value.State, OrderDraftInputDto.Number(value.Revision), value.ExpiresAt,
        value.Reference is null ? null : Reference(value.Reference), value.ScanStatus, value.DecoderStatus, value.SafeFailure);
}

// Only the matched byte-upload endpoint bypasses the JSON buffer. Authentication, exact
// Origin, CSRF and expected actor checks still run before this bounded source is read.
internal sealed class PaymentEvidenceBodyStream(Stream source) : Stream
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
        if (consumed > MaximumBytes) { Exceeded = true; throw new IOException("Payment upload limit exceeded"); } return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
