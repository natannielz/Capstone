using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Billing;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionLineInputDto([property: JsonRequired] LongId RequestSkuId, [property: JsonRequired] LongId DefinitionId,
    [property: JsonRequired] string DefinitionVersion,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int DeclaredPackages,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int ReceivedPackages,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int DamagedPackages,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int ExpiredPackages,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int OpenedPackages,
    [property: JsonRequired] string? LotCode, [property: JsonRequired] string? ExpiryDate,
    [property: JsonRequired] string? PutawayReference, [property: JsonRequired] string? Discrepancy) {
    internal ProcurementInspectionLineInput Domain() => new(RequestSkuId.Value, DefinitionId.Value, LongId.Parse(DefinitionVersion).Value,
        DeclaredPackages, ReceivedPackages, DamagedPackages, ExpiredPackages, OpenedPackages, LotCode,
        ExpiryDate is null ? null : ProcurementReceivingEndpoints.Day(ExpiryDate), PutawayReference, Discrepancy);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId NoticeId, [property: JsonRequired] LongId IntakeId, [property: JsonRequired] string InspectionDate,
    [property: JsonRequired] string InspectionReference, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int InspectedContainers,
    [property: JsonRequired] ProcurementInspectionLineInputDto[] Lines, [property: JsonRequired] string? Discrepancy) {
    internal ProcurementInspectionInput Domain() {
        if (Location is null || Lines is null || Lines.Any(line => line is null)) throw ProcurementInspectionRules.Invalid();
        return ProcurementInspectionRules.Copy(new(Location.Domain(), NoticeId.Value, IntakeId.Value, ProcurementReceivingEndpoints.Day(InspectionDate),
            InspectionReference, InspectedContainers, Lines.Select(line => line.Domain()).ToImmutableArray(), Discrepancy));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementInspectionInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionDraftCommandDto([property: JsonRequired] ProcurementInspectionInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionSubmitPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionSubmitCommandDto([property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementInspectionRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record ProcurementInspectionCapacityDto(string Version, string ReceivedContainers, string InspectedContainers, string RemainingContainers);
public sealed record ProcurementInspectionChoiceDto(ProcurementArrivalNoticeDto Notice, ProcurementContainerReceiptDto Intake, ProcurementInspectionCapacityDto Capacity);
public sealed record ProcurementInspectionChoicesDto(ProcurementLocationDto Location, ProcurementInspectionChoiceDto[] Items, LongId? NextIntakeId);
public sealed record ProcurementInspectionPreviewDto(ProcurementInspectionInputDto Input, ProcurementArrivalNoticeDto Notice, ProcurementContainerReceiptDto Intake,
    ProcurementInspectionCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementInspectionDraftDto(LongId Id, string Version, ProcurementInspectionInputDto Input, ProcurementArrivalNoticeDto Notice, ProcurementContainerReceiptDto Intake,
    ProcurementInspectionCapacityDto Capacity, string Reason, DateTimeOffset RecordedAt, string State);
public sealed record ProcurementInspectionSubmissionDto(LongId Id, LongId DraftId, string Version, ProcurementInspectionInputDto Input,
    ProcurementInspectionCapacityDto Before, ProcurementInspectionCapacityDto After, string Reason, DateTimeOffset RecordedAt, string State,
    LongId? ReservationId, PaymentEvidenceReferenceDto? Evidence);
public sealed record ProcurementInspectionReadDto(ProcurementInspectionDraftDto Draft, ProcurementInspectionCapacityDto Capacity, ProcurementInspectionSubmissionDto? Inspection);
public sealed record ProcurementInspectionHistoryDto(ProcurementLocationDto Location, ProcurementInspectionReadDto[] Items, LongId? NextDraftId);
public sealed record ProcurementInspectionSubmitPreviewDto(ProcurementInspectionDraftDto Draft, ProcurementInspectionCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementInspectionResultDto(string Operation, LongId DraftId, LongId? InspectionId, string Version, string State, DateTimeOffset RecordedAt);
public sealed record ProcurementInspectionRecoveryDto(string Status, ProcurementInspectionResultDto? Result);
public sealed record ProcurementInspectionUploadLimitsDto(string MaximumBytes, string[] AcceptedMimeTypes);

public static class ProcurementInspectionEndpoints {
    public static void MapProcurementInspectionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/inspections").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementInspectionQueryDto request, HttpContext http, [FromServices] ProcurementInspectionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementInspectionTargets");
        group.MapPost("/intakes/query", async (ProcurementInspectionListInputDto request, HttpContext http, [FromServices] ProcurementInspectionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.IntakesAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementInspectionChoicesDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(item => new ProcurementInspectionChoiceDto(Notice(item["notice"]), Intake(item["intake"]), Capacity(item["capacity"])))], Id(row["nextIntakeId"])));
        }).WithName("ProcurementInspectionIntakes");
        group.MapPost("/preview/query", async (ProcurementInspectionPreviewInputDto request, HttpContext http, [FromServices] ProcurementInspectionService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementInspectionRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new ProcurementInspectionPreviewDto(Input(row["input"]), Notice(row["notice"]), Intake(row["intake"]), Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewProcurementInspection");
        group.MapPost("/drafts", async (ProcurementInspectionDraftCommandDto request, HttpContext http, [FromServices] ProcurementInspectionService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementInspectionRules.Invalid();
            return TypedResults.Ok(Result(await service.DraftAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("CreateProcurementInspectionDraft");
        group.MapPost("/drafts/query", async (ProcurementInspectionListInputDto request, HttpContext http, [FromServices] ProcurementInspectionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementInspectionHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Read)], Id(row["nextDraftId"])));
        }).WithName("ProcurementInspectionHistory");
        group.MapPost("/operations/query", async (ProcurementInspectionRecoveryInputDto request, HttpContext http, [FromServices] ProcurementInspectionService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementInspectionRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementInspection");
        var drafts = group.MapGroup("/drafts/{id}");
        drafts.MapPost("/query", async (string id, ProcurementInspectionQueryDto request, HttpContext http, [FromServices] ProcurementInspectionService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Read(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ReadProcurementInspection");
        drafts.MapPost("/submit-preview/query", async (string id, ProcurementInspectionSubmitPreviewInputDto request, HttpContext http, [FromServices] ProcurementInspectionService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewSubmitAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value, Reference(request.Reference), ct);
            return TypedResults.Ok(new ProcurementInspectionSubmitPreviewDto(Draft(row["draft"]), Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewProcurementInspectionSubmission");
        drafts.MapPost("/submissions", async (string id, ProcurementInspectionSubmitCommandDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.SubmitAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value, Reference(request.Reference), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("SubmitProcurementInspection");
        MapEvidence(drafts);
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "inspection-review") { if (value != expected) throw ProcurementInspectionRules.Invalid(); }
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementInspectionRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static EvidenceRef Reference(PaymentEvidenceReferenceDto value) => value is null ? throw ProcurementInspectionRules.Invalid() : new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
    private static PaymentEvidenceReferenceDto Reference(EvidenceRef value) => new(new(value.Id), value.Version.ToString(CultureInfo.InvariantCulture), value.Sha256);
    private static PaymentEvidenceReferenceDto Evidence(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), value["sha256"].AsString);
    private static ProcurementInspectionInputDto Input(BsonValue row) => new(R.Location(row["location"]), new(row["noticeId"].AsInt64), new(row["intakeId"].AsInt64), row["inspectionDate"].AsString,
        row["inspectionReference"].AsString, row["inspectedContainers"].AsInt32, [.. row["lines"].AsBsonArray.Select(item => new ProcurementInspectionLineInputDto(new(item["requestSkuId"].AsInt64), new(item["definitionId"].AsInt64), Number(item["definitionVersion"]),
            item["declaredPackages"].AsInt32, item["receivedPackages"].AsInt32, item["damagedPackages"].AsInt32, item["expiredPackages"].AsInt32, item["openedPackages"].AsInt32,
            Text(item["lotCode"]), Text(item["expiryDate"]), Text(item["putawayReference"]), Text(item["discrepancy"])))], Text(row["discrepancy"]));
    private static ProcurementInspectionCapacityDto Capacity(BsonValue row) => new(Number(row["version"]), Number(row["receivedContainers"]), Number(row["inspectedContainers"]), Number(row["remainingContainers"]));
    private static ProcurementArrivalNoticeDto Notice(BsonValue row) {
        var input = row["input"];
        return new(new(row["id"].AsInt64), Number(row["version"]), new(R.Location(input["location"]), new(input["orderId"].AsInt64), Number(input["orderVersion"]), new(input["orderApprovalId"].AsInt64),
            input["noticeDate"].AsString, input["supplierDocumentReference"].AsString, Text(input["ddoRequestReference"]), Text(input["ddoTelephoneReference"]), input["declaredContainers"].AsInt32, input["note"].AsString),
            ProcurementFundingEndpoints.Order(row["order"]), R.Source(row["source"]), Date(row["recordedAt"]), row["reason"].AsString, row["state"].AsString);
    }
    private static ProcurementContainerReceiptDto Intake(BsonValue row) {
        var input = row["input"];
        return new(new(row["id"].AsInt64), new(row["noticeId"].AsInt64), Number(row["version"]), new(input["receivedDate"].AsString, input["intakeReference"].AsString,
            input["matchedContainers"].AsInt32, input["excessContainers"].AsInt32, input["heldContainers"].AsInt32, input["condition"].AsString, Text(input["discrepancy"])), Date(row["recordedAt"]), row["reason"].AsString, row["state"].AsString);
    }
    private static ProcurementInspectionDraftDto Draft(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), Input(row["input"]), Notice(row["notice"]), Intake(row["intake"]), Capacity(row["capacity"]), row["reason"].AsString, Date(row["recordedAt"]), row["state"].AsString);
    private static ProcurementInspectionSubmissionDto Submission(BsonValue row) => new(new(row["id"].AsInt64), new(row["draftId"].AsInt64), Number(row["version"]), Input(row["input"]), Capacity(row["before"]), Capacity(row["after"]), row["reason"].AsString, Date(row["recordedAt"]), row["state"].AsString, Id(row["reservationId"]), row["evidence"].IsBsonNull ? null : Evidence(row["evidence"]));
    private static ProcurementInspectionReadDto Read(BsonValue row) => new(Draft(row["draft"]), Capacity(row["capacity"]), row["inspection"].IsBsonNull ? null : Submission(row["inspection"]));
    private static ProcurementInspectionResultDto Result(BsonValue row) => new(row["operation"].AsString, new(row["draftId"].AsInt64), Id(row["inspectionId"]), Number(row["version"]), row["state"].AsString, Date(row["recordedAt"]));
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), row.Revision.ToString(CultureInfo.InvariantCulture), row.ExpiresAt);
    private static PaymentEvidenceStatusDto Status(SafeStatus row) => new(new(row.ReservationId), row.State, row.Revision.ToString(CultureInfo.InvariantCulture), row.ExpiresAt, row.Reference is null ? null : Reference(row.Reference), row.ScanStatus, row.DecoderStatus, row.SafeFailure);
    private static void MapEvidence(RouteGroupBuilder drafts) {
        drafts.MapPost("/evidence/limits/query", async (string id, ProcurementInspectionQueryDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-inspection-evidence"); var limits = await service.UploadLimitsAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), ct);
            return TypedResults.Ok(new ProcurementInspectionUploadLimitsDto(limits.MaximumBytes.ToString(CultureInfo.InvariantCulture), ["video/mp4", "video/webm"]));
        }).WithName("ProcurementInspectionUploadLimits");
        drafts.MapPost("/evidence/query", async (string id, InvoiceQueryDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-inspection-evidence");
            return TypedResults.Ok(new PaymentEvidencePageDto([.. (await service.ListAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct)).Select(Status)]));
        }).WithName("ListProcurementInspectionEvidence");
        drafts.MapPost("/evidence", async (string id, PaymentEvidenceReserveDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            return TypedResults.Ok(Reservation(await service.ReserveAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReserveProcurementInspectionEvidence");
        drafts.MapPost("/evidence/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.RecoverReservationAsync(Actor(http), LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("RecoverProcurementInspectionEvidenceReservation");
        drafts.MapPost("/evidence/{reservationId}/query", async (string id, string reservationId, ProcurementInspectionQueryDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-inspection-evidence"); return TypedResults.Ok(Status(await service.StatusAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value, ct)));
        }).WithName("ReadProcurementInspectionEvidenceStatus");
        drafts.MapPost("/evidence/{reservationId}/content", async (string id, string reservationId, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            var actor = Actor(http); var draftId = LongId.Parse(id).Value;
            var limit = (await service.UploadLimitsAsync(actor, draftId, 1, ct)).MaximumBytes;
            if (limit <= 0) throw new BadHttpRequestException("Video upload limit unavailable", StatusCodes.Status503ServiceUnavailable);
            if (http.Request.ContentLength > limit) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is null || size.IsReadOnly) throw new BadHttpRequestException("Video upload boundary unavailable", StatusCodes.Status503ServiceUnavailable);
            size.MaxRequestBodySize = limit;
            using var source = new ProcurementInspectionEvidenceBodyStream(http.Request.Body, limit);
            var result = await service.UploadAsync(actor, draftId, LongId.Parse(reservationId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), source,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), "Upload original procurement inspection video", http.TraceIdentifier), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithMetadata(ProcurementInspectionEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("UploadProcurementInspectionEvidence");
        drafts.MapPost("/evidence/{reservationId}/recover", async (string id, string reservationId, PaymentEvidenceRecoverDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Purpose(request.Purpose, "upload-recovery"); var result = await service.RecoverUploadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithName("RecoverProcurementInspectionEvidenceUpload");
        drafts.MapPost("/evidence/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http, [FromServices] ProcurementInspectionEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-inspection-evidence-view"); if (http.Request.Headers.ContainsKey("Range")) throw ProcurementInspectionRules.Invalid();
            var reference = Reference(request.Reference); var file = await service.OpenReadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), reference, new(request.Purpose, http.TraceIdentifier), ct);
            http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox"; http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"video-pemeriksaan-{reference.Id}-{reference.Version}.{(file.Mime == "video/mp4" ? "mp4" : "webm")}", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "video/mp4", "video/webm").WithName("ReadProcurementInspectionEvidenceOriginal");
    }
}

internal sealed class ProcurementInspectionEvidenceBodyStream(Stream source, long maximumBytes) : Stream {
    internal sealed class UploadMetadata;
    internal static readonly UploadMetadata Metadata = new();
    private long consumed;
    internal bool Exceeded { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => consumed; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        if (maximumBytes <= 0 || consumed > maximumBytes) throw new IOException("Video upload limit unavailable");
        var size = (int)Math.Min(buffer.Length, maximumBytes - consumed < buffer.Length ? maximumBytes - consumed + 1 : buffer.Length);
        var read = await source.ReadAsync(buffer[..size], ct); consumed = checked(consumed + read);
        if (consumed > maximumBytes) { Exceeded = true; throw new IOException("Video upload limit exceeded"); } return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
