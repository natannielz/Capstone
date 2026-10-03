using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimLineInputDto([property: JsonRequired] string Basis, [property: JsonRequired] LongId InspectionId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int InspectionLineNumber,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Packages, [property: JsonRequired] string Issue,
    [property: JsonRequired] LongId? InvoiceDocumentId, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? InvoiceDocumentLineNumber,
    [property: JsonRequired] LongId? InvoiceMatchId, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? InvoiceMatchLineNumber) {
    internal SupplierClaimLineInput Domain() => new(Basis switch {
        "physical_received" => SupplierClaimBasis.PhysicalReceived, "shortage_observed" => SupplierClaimBasis.ShortageObserved,
        _ => throw SupplierClaimRules.Invalid() }, InspectionId.Value, InspectionLineNumber, Packages, Issue,
        InvoiceDocumentId?.Value, InvoiceDocumentLineNumber, InvoiceMatchId?.Value, InvoiceMatchLineNumber);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimInputDto([property: JsonRequired] ProcurementLocationDto Location, [property: JsonRequired] LongId OrderId,
    [property: JsonRequired] string OrderVersion, [property: JsonRequired] LongId OrderApprovalId, [property: JsonRequired] string ClaimReference,
    [property: JsonRequired] string BusinessDate, [property: JsonRequired] string ResponseDueOn, [property: JsonRequired] string DeadlineBasisReference,
    [property: JsonRequired] string RequestedResolution, [property: JsonRequired] string Reason, [property: JsonRequired] SupplierClaimLineInputDto[] Lines) {
    internal SupplierClaimInput Domain() {
        if (Location is null || Lines is null || Lines.Any(line => line is null)) throw SupplierClaimRules.Invalid();
        return SupplierClaimRules.Copy(new SupplierClaimInput(Location.Domain(), OrderId.Value, LongId.Parse(OrderVersion).Value, OrderApprovalId.Value,
            ClaimReference, ProcurementReceivingEndpoints.Day(BusinessDate), ProcurementReceivingEndpoints.Day(ResponseDueOn), DeadlineBasisReference,
            RequestedResolution switch { "replacement" => SupplierClaimResolution.Replacement, "credit" => SupplierClaimResolution.Credit,
                "cash_refund" => SupplierClaimResolution.CashRefund, "investigation" => SupplierClaimResolution.Investigation,
                _ => throw SupplierClaimRules.Invalid() }, Reason, Lines.Select(line => line.Domain()).ToImmutableArray()));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimSourceQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId OrderId, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimInvoiceMatchQueryDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] SupplierClaimInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimRequestCommandDto([property: JsonRequired] SupplierClaimInputDto Input, [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimDecisionPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimDecisionCommandDto([property: JsonRequired] string Decision, [property: JsonRequired] string Explanation,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierClaimRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record SupplierClaimCapacityDto(string Basis, LongId InspectionId, int InspectionLineNumber, string InspectionHash,
    LongId? HeadId, string Version, string TotalPackages, string OccupiedPackages, string RemainingPackages);
public sealed record SupplierClaimInspectionSourceDto(LongId Id, string Version, string Hash, string InspectionDate, string InspectionReference,
    LongId NoticeId, LongId IntakeId, ProcurementInspectionLineInputDto Line);
public sealed record SupplierClaimInvoiceSourceDto(LongId DocumentId, string DocumentRevision, string DocumentReference, string? DocumentDate,
    int DocumentLineNumber, SupplierDocumentLineInputDto DocumentLine, LongId? MatchId, int? MatchLineNumber, Money? SourceInvoiceAmount);
public sealed record SupplierClaimResolvedLineDto(int LineNumber, SupplierClaimLineInputDto Input, SupplierClaimInspectionSourceDto Inspection, SupplierClaimInvoiceSourceDto? Invoice);
public sealed record SupplierClaimRequestDto(LongId Id, string Version, SupplierClaimInputDto Input, ProcurementFundingOrderDto Order,
    SupplierClaimResolvedLineDto[] Lines, LongId RequestedBy, DateTimeOffset RecordedAt, string State);
public sealed record SupplierClaimDecisionDto(LongId Id, LongId ClaimId, string Decision, string Explanation, LongId DecidedBy, DateTimeOffset RecordedAt, string State);
public sealed record SupplierClaimPreviewDto(SupplierClaimInputDto Input, ProcurementFundingOrderDto Order, SupplierClaimResolvedLineDto[] Lines,
    SupplierClaimCapacityDto[] Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierClaimDecisionPreviewDto(SupplierClaimRequestDto Request, string Decision, SupplierClaimCapacityDto[] Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record SupplierClaimReadDto(SupplierClaimRequestDto Request, SupplierClaimDecisionDto? Decision, SupplierClaimCapacityDto[] Capacity, string State);
public sealed record SupplierClaimHistoryItemDto(SupplierClaimRequestDto Request, SupplierClaimDecisionDto? Decision, string State);
public sealed record SupplierClaimHistoryDto(ProcurementLocationDto Location, SupplierClaimHistoryItemDto[] Items, LongId? NextClaimId);
public sealed record SupplierClaimOrdersDto(ProcurementLocationDto Location, ProcurementFundingOrderDto[] Items, LongId? NextOrderId);
public sealed record SupplierClaimInspectionLineChoiceDto(int LineNumber, ProcurementInspectionLineInputDto Input, SupplierClaimCapacityDto Physical, SupplierClaimCapacityDto Shortage);
public sealed record SupplierClaimInspectionChoiceDto(LongId InspectionId, string Version, string InspectionReference, string InspectionDate,
    LongId NoticeId, LongId IntakeId, SupplierClaimInspectionLineChoiceDto[] Lines);
public sealed record SupplierClaimInspectionChoicesDto(ProcurementLocationDto Location, LongId OrderId, SupplierClaimInspectionChoiceDto[] Items, LongId? NextInspectionId);
public sealed record SupplierClaimInvoiceChoiceDto(LongId DocumentId, string DocumentRevision, SupplierDocumentInputDto Input);
public sealed record SupplierClaimInvoiceChoicesDto(ProcurementLocationDto Location, LongId OrderId, SupplierClaimInvoiceChoiceDto[] Items, LongId? NextDocumentId);
public sealed record SupplierClaimInvoiceMatchLineChoiceDto(int LineNumber, int DocumentLineNumber, LongId InspectionId, int InspectionLineNumber, int Packages, Money SourceInvoiceAmount);
public sealed record SupplierClaimInvoiceMatchChoiceDto(LongId MatchId, DateTimeOffset RecordedAt, SupplierClaimInvoiceMatchLineChoiceDto[] Lines);
public sealed record SupplierClaimInvoiceMatchesDto(LongId DocumentId, SupplierClaimInvoiceMatchChoiceDto[] Items, LongId? NextMatchId);
public sealed record SupplierClaimResultDto(LongId ClaimId, string ClaimVersion, LongId? DecisionId, string? Decision, string State);
public sealed record SupplierClaimRecoveryDto(string Status, SupplierClaimResultDto? Result);

public static class SupplierClaimEndpoints
{
    public static void MapSupplierClaimEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/supplier-claims").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (SupplierClaimQueryDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("SupplierClaimTargets");
        group.MapPost("/orders/query", async (SupplierClaimListInputDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OrdersAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierClaimOrdersDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(ProcurementFundingEndpoints.Order)], Id(row["nextOrderId"])));
        }).WithName("SupplierClaimOrders");
        group.MapPost("/inspections/query", async (SupplierClaimSourceQueryDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.InspectionsAsync(Actor(http), Location(request.Location), request.OrderId.Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierClaimInspectionChoicesDto(R.Location(row["location"]), new(row["orderId"].AsInt64), [.. row["items"].AsBsonArray.Select(InspectionChoice)], Id(row["nextInspectionId"])));
        }).WithName("SupplierClaimInspections");
        group.MapPost("/invoices/query", async (SupplierClaimSourceQueryDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.InvoicesAsync(Actor(http), Location(request.Location), request.OrderId.Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierClaimInvoiceChoicesDto(R.Location(row["location"]), new(row["orderId"].AsInt64), [.. row["items"].AsBsonArray.Select(value => new SupplierClaimInvoiceChoiceDto(
                new(value["documentId"].AsInt64), Number(value["documentRevision"]), SupplierDocumentEndpoints.Input(value["input"])))], Id(row["nextDocumentId"])));
        }).WithName("SupplierClaimInvoices");
        group.MapPost("/invoices/{id}/matches/query", async (string id, SupplierClaimInvoiceMatchQueryDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.InvoiceMatchesAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierClaimInvoiceMatchesDto(new(row["documentId"].AsInt64), [.. row["items"].AsBsonArray.Select(value => new SupplierClaimInvoiceMatchChoiceDto(new(value["matchId"].AsInt64), Date(value["recordedAt"]),
                [.. value["lines"].AsBsonArray.Select(line => new SupplierClaimInvoiceMatchLineChoiceDto(line["lineNumber"].AsInt32, line["documentLineNumber"].AsInt32, new(line["inspectionId"].AsInt64),
                    line["inspectionLineNumber"].AsInt32, line["packages"].AsInt32, Money.Parse(line["sourceInvoiceAmount"].AsString)))]))], Id(row["nextMatchId"])));
        }).WithName("SupplierClaimInvoiceMatches");
        group.MapPost("/preview/query", async (SupplierClaimPreviewInputDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw SupplierClaimRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new SupplierClaimPreviewDto(Input(row["input"]), ProcurementFundingEndpoints.Order(row["order"]), [.. row["lines"].AsBsonArray.Select(Resolved)],
                Capacities(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewSupplierClaim");
        group.MapPost("/requests", async (SupplierClaimRequestCommandDto request, HttpContext http, [FromServices] SupplierClaimService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw SupplierClaimRules.Invalid();
            return TypedResults.Ok(Result(await service.RequestAsync(Actor(http), request.Input.Domain(), request.Binding, Metadata(http, request.Input.Reason), ct)));
        }).WithName("RequestSupplierClaim");
        group.MapPost("/requests/query", async (SupplierClaimListInputDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SupplierClaimHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(value => new SupplierClaimHistoryItemDto(Request(value["request"]),
                value["decision"].IsBsonNull ? null : DecisionView(value["decision"]), value["state"].AsString))], Id(row["nextClaimId"])));
        }).WithName("SupplierClaimHistory");
        group.MapPost("/requests/{id}/query", async (string id, SupplierClaimQueryDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Read(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("SupplierClaimDetails");
        group.MapPost("/requests/{id}/decision-preview/query", async (string id, SupplierClaimDecisionPreviewInputDto request, HttpContext http, [FromServices] SupplierClaimService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw SupplierClaimRules.Invalid();
            var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new SupplierClaimDecisionPreviewDto(Request(row["request"]), row["decision"].AsString, Capacities(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewSupplierClaimDecision");
        group.MapPost("/requests/{id}/decision", async (string id, SupplierClaimDecisionCommandDto request, HttpContext http, [FromServices] SupplierClaimService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(Decision(request.Decision), request.Explanation), request.Binding, Metadata(http, request.Reason), ct)));
        }).WithName("DecideSupplierClaim");
        group.MapPost("/operations/query", async (SupplierClaimRecoveryInputDto request, HttpContext http, [FromServices] SupplierClaimService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new SupplierClaimRecoveryDto(row is null ? "not_found" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverSupplierClaimOperation");
    }

    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "supplier-claim-review") { if (value != expected) throw SupplierClaimRules.Invalid(); }
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw SupplierClaimRules.Invalid() : value.Domain();
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason) => new(AccessRequestMetadata.IdempotencyKey(http.Request), reason, http.TraceIdentifier);
    private static SupplierClaimDecision Decision(string value) => value switch { "approve" => SupplierClaimDecision.Approve, "reject" => SupplierClaimDecision.Reject, _ => throw SupplierClaimRules.Invalid() };
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static int? Count(BsonValue value) => value.IsBsonNull ? null : value.AsInt32;
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static Money? Amount(BsonValue value) => value.IsBsonNull ? null : Money.Parse(value.AsString);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static SupplierClaimLineInputDto Line(BsonValue row) => new(row["basis"].AsString, new(row["inspectionId"].AsInt64), row["inspectionLineNumber"].AsInt32,
        row["packages"].AsInt32, row["issue"].AsString, Id(row["invoiceDocumentId"]), Count(row["invoiceDocumentLineNumber"]), Id(row["invoiceMatchId"]), Count(row["invoiceMatchLineNumber"]));
    private static SupplierClaimInputDto Input(BsonValue row) => new(R.Location(row["location"]), new(row["orderId"].AsInt64), Number(row["orderVersion"]), new(row["orderApprovalId"].AsInt64),
        row["claimReference"].AsString, row["businessDate"].AsString, row["responseDueOn"].AsString, row["deadlineBasisReference"].AsString,
        row["requestedResolution"].AsString, row["reason"].AsString, [.. row["lines"].AsBsonArray.Select(Line)]);
    private static SupplierClaimCapacityDto Capacity(BsonValue row) => new(row["basis"].AsString, new(row["inspectionId"].AsInt64), row["inspectionLineNumber"].AsInt32,
        row["inspectionHash"].AsString, Id(row["headId"]), Number(row["version"]), Number(row["totalPackages"]), Number(row["occupiedPackages"]), Number(row["remainingPackages"]));
    private static SupplierClaimCapacityDto[] Capacities(BsonValue rows) => [.. rows.AsBsonArray.Select(Capacity)];
    private static SupplierClaimInspectionSourceDto Inspection(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), row["hash"].AsString,
        row["inspectionDate"].AsString, row["inspectionReference"].AsString, new(row["noticeId"].AsInt64), new(row["intakeId"].AsInt64), SupplierDocumentEndpoints.InspectionLine(row["line"]));
    private static SupplierDocumentLineInputDto DocumentLine(BsonValue line) => new(line["sourceLineReference"].AsString, line["description"].AsString, Text(line["sourceSystem"]), Text(line["sourceAccount"]),
        Text(line["sourceCode"]), Count(line["packages"]), Amount(line["netPricePerPackage"]), Amount(line["taxAmount"]), Amount(line["chargeAmount"]), Text(line["relatedInvoiceReference"]), Amount(line["statementAmount"]));
    private static SupplierClaimInvoiceSourceDto Invoice(BsonValue row) => new(new(row["documentId"].AsInt64), Number(row["documentRevision"]), row["documentReference"].AsString, Text(row["documentDate"]),
        row["documentLineNumber"].AsInt32, DocumentLine(row["documentLine"]), Id(row["matchId"]), Count(row["matchLineNumber"]), Amount(row["sourceInvoiceAmount"]));
    private static SupplierClaimResolvedLineDto Resolved(BsonValue row) => new(row["lineNumber"].AsInt32, Line(row["input"]), Inspection(row["inspection"]), row["invoice"].IsBsonNull ? null : Invoice(row["invoice"]));
    private static SupplierClaimRequestDto Request(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), Input(row["input"]), ProcurementFundingEndpoints.Order(row["order"]),
        [.. row["lines"].AsBsonArray.Select(Resolved)], new(row["requestedBy"].AsInt64), Date(row["recordedAt"]), row["state"].AsString);
    private static SupplierClaimDecisionDto DecisionView(BsonValue row) => new(new(row["id"].AsInt64), new(row["claimId"].AsInt64), row["decision"].AsString,
        row["explanation"].AsString, new(row["decidedBy"].AsInt64), Date(row["recordedAt"]), row["state"].AsString);
    private static SupplierClaimReadDto Read(BsonValue row) => new(Request(row["request"]), row["decision"].IsBsonNull ? null : DecisionView(row["decision"]), Capacities(row["capacity"]), row["state"].AsString);
    private static SupplierClaimResultDto Result(BsonValue row) => new(new(row["claimId"].AsInt64), Number(row["claimVersion"]), Id(row["decisionId"]), Text(row["decision"]), row["state"].AsString);
    private static SupplierClaimInspectionChoiceDto InspectionChoice(BsonValue row) => new(new(row["inspectionId"].AsInt64), Number(row["version"]), row["inspectionReference"].AsString,
        row["inspectionDate"].AsString, new(row["noticeId"].AsInt64), new(row["intakeId"].AsInt64), [.. row["lines"].AsBsonArray.Select(line => new SupplierClaimInspectionLineChoiceDto(
            line["lineNumber"].AsInt32, SupplierDocumentEndpoints.InspectionLine(line["input"]), Capacity(line["physical"]), Capacity(line["shortage"]))) ]);
}
