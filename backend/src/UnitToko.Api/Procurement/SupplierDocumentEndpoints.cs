using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Billing;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;
using I = UnitToko.Api.Procurement.ProcurementInspectionEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentLineInputDto([property: JsonRequired] string SourceLineReference, [property: JsonRequired] string Description,
    [property: JsonRequired] string? SourceSystem, [property: JsonRequired] string? SourceAccount, [property: JsonRequired] string? SourceCode,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? Packages, [property: JsonRequired] Money? NetPricePerPackage,
    [property: JsonRequired] Money? TaxAmount, [property: JsonRequired] Money? ChargeAmount, [property: JsonRequired] string? RelatedInvoiceReference, [property: JsonRequired] Money? StatementAmount) {
    internal SupplierDocumentLineInput Domain() => new(SourceLineReference, Description, SourceSystem, SourceAccount, SourceCode, Packages, NetPricePerPackage, TaxAmount, ChargeAmount, RelatedInvoiceReference, StatementAmount);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentInputDto([property: JsonRequired] ProcurementLocationDto Location, [property: JsonRequired] LongId AnchorOrderId,
    [property: JsonRequired] string AnchorOrderVersion, [property: JsonRequired] LongId AnchorOrderApprovalId, [property: JsonRequired] string Kind,
    [property: JsonRequired] string DocumentReference, [property: JsonRequired] string? DocumentDate, [property: JsonRequired] string Currency,
    [property: JsonRequired] Money? DocumentTotal, [property: JsonRequired] SupplierDocumentLineInputDto[] Lines, [property: JsonRequired] string? Discrepancy) {
    internal SupplierDocumentInput Domain() {
        if (Location is null || Lines is null || Lines.Any(line => line is null)) throw SupplierDocumentRules.Invalid();
        return SupplierDocumentRules.Copy(new SupplierDocumentInput(Location.Domain(), AnchorOrderId.Value, LongId.Parse(AnchorOrderVersion).Value, AnchorOrderApprovalId.Value,
            Kind switch { "invoice" => SupplierDocumentKind.Invoice, "sph" => SupplierDocumentKind.Sph, _ => throw SupplierDocumentRules.Invalid() }, DocumentReference,
            DocumentDate is null ? null : ProcurementReceivingEndpoints.Day(DocumentDate), Currency, DocumentTotal, Lines.Select(line => line.Domain()).ToImmutableArray(), Discrepancy));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentMatchLineInputDto([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int DocumentLineNumber,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? Packages, [property: JsonRequired] LongId? InspectionId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? InspectionLineNumber, [property: JsonRequired] LongId? InvoiceMatchId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? InvoiceMatchLineNumber, [property: JsonRequired] Money? NetAmount,
    [property: JsonRequired] Money? TaxAmount, [property: JsonRequired] Money? ChargeAmount, [property: JsonRequired] Money? StatementAmount, [property: JsonRequired] string? Discrepancy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LongId? QualityAllocationId = null) {
    internal SupplierDocumentMatchLineInput Domain() => new(DocumentLineNumber, Packages, InspectionId?.Value, InspectionLineNumber, InvoiceMatchId?.Value, InvoiceMatchLineNumber, NetAmount, TaxAmount, ChargeAmount, StatementAmount, Discrepancy, QualityAllocationId?.Value);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentPdfMatchCandidateDto([property: JsonRequired] LongId ResultId, [property: JsonRequired] string ResultHash,
    [property: JsonRequired] PaymentEvidenceReferenceDto Evidence, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int PageIndex,
    [property: JsonRequired] string Field) {
    internal PdfDocumentCandidateReference Domain() => Evidence is null ? throw SupplierDocumentRules.Invalid() :
        new(ResultId.Value, ResultHash, new(Evidence.Id.Value, LongId.Parse(Evidence.Version).Value, Evidence.Sha256), PageIndex, Field);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentPdfMatchObservationDto([property: JsonRequired] string Field, [property: JsonRequired] SupplierDocumentPdfMatchCandidateDto Candidate,
    [property: JsonRequired] bool OriginalReviewed) {
    internal SupplierDocumentPdfMatchObservation Domain() => Candidate is null ? throw SupplierDocumentRules.Invalid() : new(Field, Candidate.Domain(), OriginalReviewed);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentMatchInputDto([property: JsonRequired] LongId DocumentId, [property: JsonRequired] string DocumentRevision,
    [property: JsonRequired] SupplierDocumentInputDto ReviewedInput, [property: JsonRequired] LongId? OcrResultId, [property: JsonRequired] SupplierDocumentMatchLineInputDto[] Lines) {
    private SupplierDocumentPdfMatchObservationDto[]? pdfObservations;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupplierDocumentPdfMatchObservationDto[] PdfObservations {
        // An omitted optional member remains unset; a supplied array is validated before domain conversion.
        get => pdfObservations!;
        init { pdfObservations = value; PdfObservationsPresent = true; }
    }
    [JsonIgnore] internal bool PdfObservationsPresent { get; private set; }
    internal SupplierDocumentMatchInput Domain() {
        if (ReviewedInput is null || Lines is null || Lines.Any(line => line is null)) throw SupplierDocumentRules.Invalid();
        ImmutableArray<SupplierDocumentPdfMatchObservation>? observations = null;
        if (PdfObservationsPresent) {
            if (PdfObservations is null || PdfObservations.Length is < 1 or > 8 || PdfObservations.Any(value => value is null)) throw SupplierDocumentRules.Invalid();
            observations = [.. PdfObservations.Select(value => value.Domain())];
        }
        return SupplierDocumentRules.Copy(new SupplierDocumentMatchInput(DocumentId.Value, LongId.Parse(DocumentRevision).Value, ReviewedInput.Domain(), OcrResultId?.Value,
            Lines.Select(line => line.Domain()).ToImmutableArray(), observations));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentQualityQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId InspectionId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int InspectionLineNumber, LongId? BeforeAllocationId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] SupplierDocumentInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentDraftCommandDto([property: JsonRequired] SupplierDocumentInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentSubmitPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentSubmitCommandDto([property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentMatchPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] SupplierDocumentMatchInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentMatchCommandDto([property: JsonRequired] SupplierDocumentMatchInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierDocumentRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record SupplierDocumentCapacityLineDto(int LineNumber, int? Packages, string MatchedPackages, string? RemainingPackages,
    Money MatchedNetAmount, Money MatchedTaxAmount, Money MatchedChargeAmount, Money MatchedStatementAmount);
public sealed record SupplierDocumentCapacityDto(string Version, SupplierDocumentCapacityLineDto[] Lines);
public sealed record SupplierDocumentDraftDto(LongId Id, string Version, SupplierDocumentInputDto Input, LongId SupplierId,
    ProcurementFundingOrderDto Order, DateTimeOffset RecordedAt, string Reason, string State);
public sealed record SupplierDocumentSubmissionDto(LongId Id, LongId DraftId, string Version, string State, DateTimeOffset RecordedAt, string Reason,
    LongId? ReservationId, PaymentEvidenceReferenceDto? Evidence);
public sealed record SupplierDocumentResolvedLineDto(int DocumentLineNumber, int? Packages, LongId? InspectionId, int? InspectionLineNumber,
    LongId? InvoiceMatchId, int? InvoiceMatchLineNumber, Money? NetAmount, Money? TaxAmount, Money? ChargeAmount, Money? StatementAmount, string State, string? Discrepancy,
    ProcurementFundingOrderDto? Order, ProcurementInspectionLineInputDto? InspectionLine, string? InvoiceDocumentReference,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SupplierDocumentQualityDto? Quality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LongId? QualityAllocationId = null);
public sealed record SupplierDocumentQualityDto(LongId AllocationId, LongId ProposalId, LongId DecisionId, LongId ReceiptId,
    string ReceiptReference, string AcceptedPackages, string LotReference, string? ExpiresOn, string AcceptedCondition,
    string PutawayReference, string SegregationReference, DateTimeOffset RecordedAt);
public sealed record SupplierDocumentMatchDto(LongId Id, LongId DraftId, LongId DocumentId, string Version, SupplierDocumentMatchInputDto Input,
    SupplierDocumentCapacityDto Before, SupplierDocumentCapacityDto After, SupplierDocumentResolvedLineDto[] Lines, string State, DateTimeOffset RecordedAt, string Reason);
public sealed record SupplierDocumentReadDto(SupplierDocumentDraftDto Draft, SupplierDocumentSubmissionDto? Document, SupplierDocumentCapacityDto Capacity,
    SupplierDocumentInputDto? ReviewedInput, bool PossibleDuplicate, SupplierDocumentMatchDto[] Matches, LongId? NextMatchId);
public sealed record SupplierDocumentHistoryDto(ProcurementLocationDto Location, SupplierDocumentReadDto[] Items, LongId? NextDraftId);
public sealed record SupplierDocumentOrdersDto(ProcurementLocationDto Location, ProcurementFundingOrderDto[] Items, LongId? NextOrderId);
public sealed record SupplierDocumentPreviewDto(SupplierDocumentInputDto Input, ProcurementFundingOrderDto Order, LongId SupplierId, bool PossibleDuplicate, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierDocumentSubmitPreviewDto(SupplierDocumentDraftDto Draft, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierDocumentMatchPreviewDto(SupplierDocumentMatchInputDto Input, SupplierDocumentDraftDto Document, SupplierDocumentCapacityDto Capacity,
    SupplierDocumentResolvedLineDto[] Lines, bool PossibleDuplicate, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierDocumentInspectionChoiceDto(LongId InspectionId, ProcurementArrivalNoticeDto Notice, ProcurementContainerReceiptDto Intake,
    ProcurementInspectionInputDto Input, ProcurementFundingOrderDto Order, SupplierDocumentCapacityDto Capacity);
public sealed record SupplierDocumentInspectionChoicesDto(LongId DocumentId, SupplierDocumentInspectionChoiceDto[] Items, LongId? NextInspectionId);
public sealed record SupplierDocumentQualityChoiceDto(LongId AllocationId, LongId ProposalId, LongId DecisionId, LongId ReceiptId,
    string ReceiptReference, string AcceptedPackages, string MatchedPackages, string RemainingPackages, string LotReference,
    string? ExpiresOn, string AcceptedCondition, string PutawayReference, string SegregationReference, DateTimeOffset RecordedAt);
public sealed record SupplierDocumentQualityChoicesDto(LongId DocumentId, LongId InspectionId, int InspectionLineNumber,
    SupplierDocumentQualityChoiceDto[] Items, LongId? NextAllocationId);
public sealed record SupplierDocumentInvoiceChoiceDto(SupplierDocumentDraftDto Document, SupplierDocumentMatchDto Match);
public sealed record SupplierDocumentInvoiceChoicesDto(LongId DocumentId, SupplierDocumentInvoiceChoiceDto[] Items, LongId? NextMatchId);
public sealed record SupplierDocumentMatchesDto(LongId DocumentId, SupplierDocumentMatchDto[] Items, LongId? NextMatchId);
public sealed record SupplierDocumentOcrLineDto(string Text, string Confidence, ImmutableArray<string> BoundingBox);
public sealed record SupplierDocumentOcrFieldDto(string Field, string? Value, string? Confidence, ImmutableArray<string>? BoundingBox, ImmutableArray<string> Flags);
public sealed record SupplierDocumentOcrDto(LongId DraftId, LongId SubmissionId, PaymentEvidenceReferenceDto Evidence, string State, LongId? ResultId,
    DateTimeOffset? ExtractedAt, ExtractionProvenance? Provenance, ImmutableArray<SupplierDocumentOcrLineDto> Lines,
    ImmutableArray<SupplierDocumentOcrFieldDto> Fields, ImmutableArray<string> QualityFlags, string? FailureCode);
public sealed record SupplierDocumentResultDto(string Operation, LongId DraftId, LongId? DocumentId, LongId? MatchId, string Version, string State, DateTimeOffset RecordedAt);
public sealed record SupplierDocumentRecoveryDto(string Status, SupplierDocumentResultDto? Result);

public static class SupplierDocumentEndpoints
{
    public static void MapSupplierDocumentEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/supplier-documents").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (SupplierDocumentQueryDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("SupplierDocumentTargets");
        group.MapPost("/orders/query", async (SupplierDocumentListInputDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OrdersAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierDocumentOrdersDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(ProcurementFundingEndpoints.Order)], Id(row["nextOrderId"])));
        }).WithName("SupplierDocumentOrders");
        group.MapPost("/preview/query", async (SupplierDocumentPreviewInputDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw SupplierDocumentRules.Invalid(); var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new SupplierDocumentPreviewDto(Input(row["input"]), ProcurementFundingEndpoints.Order(row["order"]), new(row["supplierId"].AsInt64), row["possibleDuplicate"].AsBoolean, row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewSupplierDocument");
        group.MapPost("/drafts", async (SupplierDocumentDraftCommandDto request, HttpContext http, [FromServices] SupplierDocumentService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw SupplierDocumentRules.Invalid(); return TypedResults.Ok(Result(await service.DraftAsync(Actor(http), request.Input.Domain(), request.Binding, Metadata(http, request.Reason), ct)));
        }).WithName("CreateSupplierDocumentDraft");
        group.MapPost("/drafts/query", async (SupplierDocumentListInputDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierDocumentHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Read)], Id(row["nextDraftId"])));
        }).WithName("SupplierDocumentHistory");
        group.MapPost("/operations/query", async (SupplierDocumentRecoveryInputDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new SupplierDocumentRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverSupplierDocument");
        var drafts = group.MapGroup("/drafts/{id}");
        drafts.MapPost("/query", async (string id, SupplierDocumentQueryDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Read(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ReadSupplierDocument");
        drafts.MapPost("/submit-preview/query", async (string id, SupplierDocumentSubmitPreviewInputDto request, HttpContext http, [FromServices] SupplierDocumentService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewSubmitAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value, Reference(request.Reference), ct);
            return TypedResults.Ok(new SupplierDocumentSubmitPreviewDto(Draft(row["draft"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewSupplierDocumentSubmission");
        drafts.MapPost("/submissions", async (string id, SupplierDocumentSubmitCommandDto request, HttpContext http, [FromServices] SupplierDocumentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) =>
            TypedResults.Ok(Result(await service.SubmitAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value,
                Reference(request.Reference), request.Binding, Metadata(http, request.Reason), ct)))).WithName("SubmitSupplierDocument");
        var documents = group.MapGroup("/documents/{id}");
        documents.MapPost("/inspections/query", async (string id, InvoiceQueryDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.InspectionsAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierDocumentInspectionChoicesDto(new(row["documentId"].AsInt64), [.. row["items"].AsBsonArray.Select(item => new SupplierDocumentInspectionChoiceDto(
                new(item["inspectionId"].AsInt64), I.Notice(item["notice"]), I.Intake(item["intake"]), I.Input(item["input"]), ProcurementFundingEndpoints.Order(item["order"]), Capacity(item["capacity"])))], Id(row["nextInspectionId"])));
        }).WithName("SupplierDocumentInspectionChoices");
        documents.MapPost("/quality-allocations/query", async (string id, SupplierDocumentQualityQueryDto request, HttpContext http,
            [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            if (request is null) throw SupplierDocumentRules.Invalid(); Purpose(request.Purpose);
            var row = await service.QualityAllocationsAsync(Actor(http), LongId.Parse(id).Value, request.InspectionId.Value,
                request.InspectionLineNumber, request.BeforeAllocationId?.Value, ct);
            return TypedResults.Ok(new SupplierDocumentQualityChoicesDto(new(row["documentId"].AsInt64), new(row["inspectionId"].AsInt64),
                row["inspectionLineNumber"].AsInt32, [.. row["items"].AsBsonArray.Select(QualityChoice)], Id(row["nextAllocationId"])));
        }).WithName("SupplierDocumentQualityChoices");
        documents.MapPost("/invoice-matches/query", async (string id, InvoiceQueryDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.InvoiceMatchesAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierDocumentInvoiceChoicesDto(new(row["documentId"].AsInt64), [.. row["items"].AsBsonArray.Select(item => new SupplierDocumentInvoiceChoiceDto(Draft(item["document"]), Match(item["match"])))], Id(row["nextMatchId"])));
        }).WithName("SupplierDocumentInvoiceChoices");
        documents.MapPost("/matches/query", async (string id, InvoiceQueryDto request, HttpContext http, [FromServices] SupplierDocumentService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.MatchesAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierDocumentMatchesDto(new(row["documentId"].AsInt64), [.. row["items"].AsBsonArray.Select(Match)], Id(row["nextMatchId"])));
        }).WithName("SupplierDocumentMatches");
        documents.MapPost("/match-preview/query", async (string id, SupplierDocumentMatchPreviewInputDto request, HttpContext http, [FromServices] SupplierDocumentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); var input = MatchInput(request.Input, id, http); var row = await service.PreviewMatchAsync(Actor(http), input, ct);
            return TypedResults.Ok(new SupplierDocumentMatchPreviewDto(MatchInput(row["input"]), Draft(row["document"]), Capacity(row["capacity"]), [.. row["lines"].AsBsonArray.Select(Resolved)],
                row["possibleDuplicate"].AsBoolean, row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewSupplierDocumentMatch");
        documents.MapPost("/matches", async (string id, SupplierDocumentMatchCommandDto request, HttpContext http, [FromServices] SupplierDocumentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) =>
            TypedResults.Ok(Result(await service.MatchAsync(Actor(http), MatchInput(request.Input, id, http), request.Binding, Metadata(http, request.Reason), ct)))).WithName("MatchSupplierDocument");
        MapEvidence(drafts);
        drafts.MapPost("/ocr/query", async (string id, PaymentEvidenceReadDto request, HttpContext http, [FromServices] SupplierDocumentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "supplier-document-ocr-view"); var result = await service.ReadExtractionAsync(Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Reference(request.Reference), new(request.Purpose, http.TraceIdentifier), ct);
            return TypedResults.Ok(Ocr(result));
        }).WithName("ReadSupplierDocumentOcr");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "supplier-document-review") { if (value != expected) throw SupplierDocumentRules.Invalid(); }
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason) => new(AccessRequestMetadata.IdempotencyKey(http.Request), reason, http.TraceIdentifier);
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw SupplierDocumentRules.Invalid() : value.Domain();
    private static SupplierDocumentMatchInput MatchInput(SupplierDocumentMatchInputDto? value, string id, HttpContext http) {
        if (value is null || value.DocumentId.Value != LongId.Parse(id).Value || LongId.Parse(value.DocumentRevision).Value != AccessRequestMetadata.ExpectedVersion(http.Request)) throw SupplierDocumentRules.Invalid();
        return value.Domain();
    }
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static int? Count(BsonValue value) => value.IsBsonNull ? null : value.AsInt32;
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static Money Amount(BsonValue value) => Money.Parse(value.AsString);
    private static Money? OptionalAmount(BsonValue value) => value.IsBsonNull ? null : Amount(value);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static EvidenceRef Reference(PaymentEvidenceReferenceDto value) => value is null ? throw SupplierDocumentRules.Invalid() : new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
    private static PaymentEvidenceReferenceDto Reference(EvidenceRef value) => new(new(value.Id), value.Version.ToString(CultureInfo.InvariantCulture), value.Sha256);
    private static PaymentEvidenceReferenceDto Evidence(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), value["sha256"].AsString);
    internal static SupplierDocumentInputDto Input(BsonValue row) => new(R.Location(row["location"]), new(row["anchorOrderId"].AsInt64), Number(row["anchorOrderVersion"]), new(row["anchorOrderApprovalId"].AsInt64),
        row["kind"].AsString, row["documentReference"].AsString, Text(row["documentDate"]), row["currency"].AsString, OptionalAmount(row["documentTotal"]), [.. row["lines"].AsBsonArray.Select(line => new SupplierDocumentLineInputDto(
            line["sourceLineReference"].AsString, line["description"].AsString, Text(line["sourceSystem"]), Text(line["sourceAccount"]), Text(line["sourceCode"]), Count(line["packages"]),
            OptionalAmount(line["netPricePerPackage"]), OptionalAmount(line["taxAmount"]), OptionalAmount(line["chargeAmount"]), Text(line["relatedInvoiceReference"]), OptionalAmount(line["statementAmount"]))) ], Text(row["discrepancy"]));
    internal static SupplierDocumentMatchInputDto MatchInput(BsonValue row) {
        var value = new SupplierDocumentMatchInputDto(new(row["documentId"].AsInt64), Number(row["documentRevision"]), Input(row["reviewedInput"]), Id(row["ocrResultId"]),
            [.. row["lines"].AsBsonArray.Select(line => new SupplierDocumentMatchLineInputDto(
                line["documentLineNumber"].AsInt32, Count(line["packages"]), Id(line["inspectionId"]), Count(line["inspectionLineNumber"]), Id(line["invoiceMatchId"]), Count(line["invoiceMatchLineNumber"]),
                OptionalAmount(line["netAmount"]), OptionalAmount(line["taxAmount"]), OptionalAmount(line["chargeAmount"]), OptionalAmount(line["statementAmount"]), Text(line["discrepancy"]),
                Id(line.AsBsonDocument.GetValue("qualityAllocationId", BsonNull.Value))))]);
        if (row.AsBsonDocument.TryGetValue("pdfObservations", out var observations))
            value = value with { PdfObservations = [.. observations.AsBsonArray.Select(item => {
                var candidate = item["candidate"];
                return new SupplierDocumentPdfMatchObservationDto(item["field"].AsString, new(new(candidate["resultId"].AsInt64), candidate["resultHash"].AsString,
                    Evidence(candidate["evidence"]), candidate["pageIndex"].AsInt32, candidate["field"].AsString), item["originalReviewed"].AsBoolean);
            })] };
        return value;
    }
    private static SupplierDocumentQualityChoiceDto QualityChoice(BsonValue row) => new(new(row["allocationId"].AsInt64), new(row["proposalId"].AsInt64),
        new(row["decisionId"].AsInt64), new(row["receiptId"].AsInt64), row["receiptReference"].AsString, row["acceptedPackages"].ToInt64().ToString(CultureInfo.InvariantCulture),
        Number(row["matchedPackages"]), Number(row["remainingPackages"]), row["lotReference"].AsString, Text(row["expiresOn"]),
        row["acceptedCondition"].AsString, row["putawayReference"].AsString, row["segregationReference"].AsString, Date(row["recordedAt"]));
    internal static SupplierDocumentQualityDto? Quality(BsonValue row) => row.IsBsonNull ? null : new(new(row["allocationId"].AsInt64),
        new(row["proposalId"].AsInt64), new(row["decisionId"].AsInt64), new(row["receiptId"].AsInt64), row["receiptReference"].AsString,
        row["acceptedPackages"].ToInt64().ToString(CultureInfo.InvariantCulture), row["lotReference"].AsString, Text(row["expiresOn"]), row["acceptedCondition"].AsString,
        row["putawayReference"].AsString, row["segregationReference"].AsString, Date(row["recordedAt"]));
    private static SupplierDocumentCapacityDto Capacity(BsonValue row) => new(Number(row["version"]), [.. row["lines"].AsBsonArray.Select(line => new SupplierDocumentCapacityLineDto(line["lineNumber"].AsInt32, Count(line["packages"]), Number(line["matchedPackages"]), line["remainingPackages"].IsBsonNull ? null : Number(line["remainingPackages"]),
        Amount(line["matchedNetAmount"]), Amount(line["matchedTaxAmount"]), Amount(line["matchedChargeAmount"]), Amount(line["matchedStatementAmount"]))) ]);
    private static SupplierDocumentDraftDto Draft(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), Input(row["input"]), new(row["supplierId"].AsInt64), ProcurementFundingEndpoints.Order(row["order"]), Date(row["recordedAt"]), row["reason"].AsString, row["state"].AsString);
    private static SupplierDocumentSubmissionDto Submission(BsonValue row) => new(new(row["id"].AsInt64), new(row["draftId"].AsInt64), Number(row["version"]), row["state"].AsString, Date(row["recordedAt"]), row["reason"].AsString, Id(row["reservationId"]), row["evidence"].IsBsonNull ? null : Evidence(row["evidence"]));
    internal static ProcurementInspectionLineInputDto InspectionLine(BsonValue line) => new(new(line["requestSkuId"].AsInt64), new(line["definitionId"].AsInt64), Number(line["definitionVersion"]), line["declaredPackages"].AsInt32,
        line["receivedPackages"].AsInt32, line["damagedPackages"].AsInt32, line["expiredPackages"].AsInt32, line["openedPackages"].AsInt32, Text(line["lotCode"]), Text(line["expiryDate"]), Text(line["putawayReference"]), Text(line["discrepancy"]));
    private static SupplierDocumentResolvedLineDto Resolved(BsonValue row) => new(row["documentLineNumber"].AsInt32, Count(row["packages"]), Id(row["inspectionId"]), Count(row["inspectionLineNumber"]), Id(row["invoiceMatchId"]), Count(row["invoiceMatchLineNumber"]),
        OptionalAmount(row["netAmount"]), OptionalAmount(row["taxAmount"]), OptionalAmount(row["chargeAmount"]), OptionalAmount(row["statementAmount"]), row["state"].AsString, Text(row["discrepancy"]), row["order"].IsBsonNull ? null : ProcurementFundingEndpoints.Order(row["order"]),
        row["inspectionLine"].IsBsonNull ? null : InspectionLine(row["inspectionLine"]), Text(row["invoiceDocumentReference"]),
        Quality(row.AsBsonDocument.GetValue("quality", BsonNull.Value)), Id(row.AsBsonDocument.GetValue("qualityAllocationId", BsonNull.Value)));
    private static SupplierDocumentMatchDto Match(BsonValue row) => new(new(row["id"].AsInt64), new(row["draftId"].AsInt64), new(row["documentId"].AsInt64), Number(row["version"]), MatchInput(row["input"]), Capacity(row["before"]), Capacity(row["after"]),
        [.. row["lines"].AsBsonArray.Select(Resolved)], row["state"].AsString, Date(row["recordedAt"]), row["reason"].AsString);
    private static SupplierDocumentReadDto Read(BsonValue row) => new(Draft(row["draft"]), row["document"].IsBsonNull ? null : Submission(row["document"]), Capacity(row["capacity"]), row["reviewedInput"].IsBsonNull ? null : Input(row["reviewedInput"]), row["possibleDuplicate"].AsBoolean, [.. row["matches"].AsBsonArray.Select(Match)], Id(row["nextMatchId"]));
    private static SupplierDocumentResultDto Result(BsonValue row) => new(row["operation"].AsString, new(row["draftId"].AsInt64), Id(row["documentId"]), Id(row["matchId"]), Number(row["version"]), row["state"].AsString, Date(row["recordedAt"]));
    private static SupplierDocumentOcrDto Ocr(SupplierDocumentExtractionView row) => new(new(row.DraftId), new(row.SubmissionId), Reference(row.Evidence), row.State,
        row.ResultId is { } resultId ? new(resultId) : null, row.ExtractedAt, row.Provenance,
        [.. row.Lines.Select(line => new SupplierDocumentOcrLineDto(line.Text, line.Confidence.ToString(CultureInfo.InvariantCulture), [.. line.Box.Values.Select(value => value.ToString(CultureInfo.InvariantCulture))]))],
        [.. row.Fields.Select(field => new SupplierDocumentOcrFieldDto(field.Field, field.Value, field.Confidence?.ToString(CultureInfo.InvariantCulture),
            field.BoundingBox is null ? null : [.. field.BoundingBox.Select(value => value.ToString(CultureInfo.InvariantCulture))], [.. field.Flags]))], [.. row.QualityFlags], row.FailureCode);
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), row.Revision.ToString(CultureInfo.InvariantCulture), row.ExpiresAt);
    private static PaymentEvidenceStatusDto Status(SafeStatus row) => new(new(row.ReservationId), row.State, row.Revision.ToString(CultureInfo.InvariantCulture), row.ExpiresAt,
        row.Reference is null ? null : Reference(row.Reference), row.ScanStatus, row.DecoderStatus, row.SafeFailure);
    private static void MapEvidence(RouteGroupBuilder drafts) {
        drafts.MapPost("/evidence/query", async (string id, InvoiceQueryDto request, HttpContext http, SupplierDocumentEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "supplier-document-evidence");
            return TypedResults.Ok(new PaymentEvidencePageDto([.. (await service.ListAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct)).Select(Status)]));
        }).WithName("ListSupplierDocumentEvidence");
        drafts.MapPost("/evidence", async (string id, PaymentEvidenceReserveDto request, HttpContext http, SupplierDocumentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            return TypedResults.Ok(Reservation(await service.ReserveAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReserveSupplierDocumentEvidence");
        drafts.MapPost("/evidence/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, SupplierDocumentEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.RecoverReservationAsync(Actor(http), LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("RecoverSupplierDocumentEvidenceReservation");
        drafts.MapPost("/evidence/{reservationId}/query", async (string id, string reservationId, SupplierDocumentQueryDto request, HttpContext http, SupplierDocumentEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "supplier-document-evidence"); return TypedResults.Ok(Status(await service.StatusAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value, ct)));
        }).WithName("ReadSupplierDocumentEvidenceStatus");
        drafts.MapPost("/evidence/{reservationId}/content", async (string id, string reservationId, HttpContext http, SupplierDocumentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            var actor = Actor(http); using var source = new PaymentEvidenceBodyStream(http.Request.Body);
            var result = await service.UploadAsync(actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), source,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), "Upload original supplier document image", http.TraceIdentifier), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithMetadata(PaymentEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("UploadSupplierDocumentEvidence");
        drafts.MapPost("/evidence/{reservationId}/recover", async (string id, string reservationId, PaymentEvidenceRecoverDto request, HttpContext http, SupplierDocumentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Purpose(request.Purpose, "upload-recovery"); var result = await service.RecoverUploadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithName("RecoverSupplierDocumentEvidenceUpload");
        drafts.MapPost("/evidence/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http, SupplierDocumentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "supplier-document-evidence-view"); if (http.Request.Headers.ContainsKey("Range")) throw SupplierDocumentRules.Invalid();
            var reference = Reference(request.Reference); var file = await service.OpenReadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), reference, new(request.Purpose, http.TraceIdentifier), ct);
            http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"bukti-pengadaan-{reference.Id}-{reference.Version}.{(file.Mime == "image/png" ? "png" : "jpg")}", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").WithName("ReadSupplierDocumentEvidenceOriginal");
    }

}
