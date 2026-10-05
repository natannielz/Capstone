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
public sealed record ProcurementStockReceiptInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId OrderId, [property: JsonRequired] string OrderVersion, [property: JsonRequired] LongId OrderApprovalId,
    [property: JsonRequired] LongId InspectionId, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int InspectionLineNumber,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Packages,
    [property: JsonRequired] LongId PoolId, [property: JsonRequired] string PoolRevision, [property: JsonRequired] string ReceiptReference,
    [property: JsonRequired] string LotReference, [property: JsonRequired] string? ExpiresOn, [property: JsonRequired] string Reason) {
    internal ProcurementStockReceiptInput Domain() {
        if (Location is null) throw ProcurementStockReceiptRules.Invalid();
        return ProcurementStockReceiptRules.Copy(new(Location.Domain(), OrderId.Value, LongId.Parse(OrderVersion).Value, OrderApprovalId.Value,
            InspectionId.Value, InspectionLineNumber, Packages, PoolId.Value, LongId.Parse(PoolRevision).Value, ReceiptReference, LotReference,
            ExpiresOn is null ? null : ProcurementReceivingEndpoints.Day(ExpiresOn), Reason));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockReceiptQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockReceiptListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockReceiptSourceQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId OrderId, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockReceiptPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementStockReceiptInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockReceiptCommandDto([property: JsonRequired] ProcurementStockReceiptInputDto Input, [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementStockReceiptRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record ProcurementStockReceiptInspectionDto(LongId Id, string Version, string Hash, string InspectionDate, string InspectionReference,
    LongId NoticeId, LongId IntakeId, int LineNumber, ProcurementInspectionLineInputDto Line);
public sealed record ProcurementStockReceiptSourceDto(ProcurementFundingOrderDto Order, ProcurementStockReceiptInspectionDto Inspection);
public sealed record ProcurementStockReceiptPoolDto(LongId Id, string Revision, bool Active, ProcurementLocationDto Location, ProcurementLocationDto RequestedLocation,
    string PhysicalKey, string Label, string Mode, string Control, string ConfigurationHash, LongId PolicyId, string PolicyRevision, string PolicyHash,
    string Writer, string ReconciliationProcedureReference, string OutageProcedureReference);
public sealed record ProcurementStockReceiptPoolsDto(ProcurementLocationDto Location, ProcurementStockReceiptPoolDto[] Items, LongId? NextPoolId);
public sealed record ProcurementStockReceiptOrdersDto(ProcurementLocationDto Location, ProcurementFundingOrderDto[] Items, LongId? NextOrderId);
public sealed record ProcurementStockReceiptInspectionLineChoiceDto(int LineNumber, ProcurementInspectionLineInputDto Input, SupplierClaimCapacityDto Capacity);
public sealed record ProcurementStockReceiptInspectionChoiceDto(LongId InspectionId, string Version, string InspectionReference, string InspectionDate,
    LongId NoticeId, LongId IntakeId, ProcurementStockReceiptInspectionLineChoiceDto[] Lines);
public sealed record ProcurementStockReceiptInspectionsDto(ProcurementLocationDto Location, LongId OrderId, ProcurementStockReceiptInspectionChoiceDto[] Items, LongId? NextInspectionId);
public sealed record ProcurementStockReceiptPreviewDto(ProcurementStockReceiptInputDto Input, ProcurementStockReceiptSourceDto Source,
    ProcurementStockReceiptPoolDto Pool, SupplierClaimCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementStockReceiptDto(LongId Id, string Revision, ProcurementStockReceiptInputDto Input, ProcurementStockReceiptSourceDto Source,
    ProcurementStockReceiptPoolDto Pool, SupplierClaimCapacityDto Before, SupplierClaimCapacityDto After, LongId LotId, LongId BalanceId, LongId MovementId,
    string OnHand, string Held, string Reserved, string Expired, string Available, string ValuationState, Money? Valuation, string State, LongId RecordedBy, DateTimeOffset RecordedAt);
public sealed record ProcurementStockReceiptHistoryDto(ProcurementLocationDto Location, ProcurementStockReceiptDto[] Items, LongId? NextReceiptId);
public sealed record ProcurementStockReceiptResultDto(LongId ReceiptId, string Revision, LongId LotId, LongId BalanceId, LongId MovementId, string State);
public sealed record ProcurementStockReceiptRecoveryDto(string Status, ProcurementStockReceiptResultDto? Result);

public static class ProcurementStockReceiptEndpoints
{
    public static void MapProcurementStockReceiptEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/stock-receipts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementStockReceiptQueryDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementStockReceiptTargets");
        group.MapPost("/orders/query", async (ProcurementStockReceiptListInputDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OrdersAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockReceiptOrdersDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(ProcurementFundingEndpoints.Order)], Id(row["nextOrderId"])));
        }).WithName("ProcurementStockReceiptOrders");
        group.MapPost("/inspections/query", async (ProcurementStockReceiptSourceQueryDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.InspectionsAsync(Actor(http), Location(request.Location), request.OrderId.Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockReceiptInspectionsDto(R.Location(row["location"]), new(row["orderId"].AsInt64), [.. row["items"].AsBsonArray.Select(InspectionChoice)], Id(row["nextInspectionId"])));
        }).WithName("ProcurementStockReceiptInspections");
        group.MapPost("/pools/query", async (ProcurementStockReceiptListInputDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PoolsAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockReceiptPoolsDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Pool)], Id(row["nextPoolId"])));
        }).WithName("ProcurementStockReceiptPools");
        group.MapPost("/preview/query", async (ProcurementStockReceiptPreviewInputDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementStockReceiptRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new ProcurementStockReceiptPreviewDto(Input(row["input"]), Source(row["source"]), Pool(row["pool"]), Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewProcurementStockReceipt");
        group.MapPost("/receipts", async (ProcurementStockReceiptCommandDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementStockReceiptRules.Invalid();
            return TypedResults.Ok(Result(await service.ReceiveAsync(Actor(http), request.Input.Domain(), request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Input.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReceiveProcurementStock");
        group.MapPost("/receipts/query", async (ProcurementStockReceiptListInputDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementStockReceiptHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Receipt)], Id(row["nextReceiptId"])));
        }).WithName("ProcurementStockReceiptHistory");
        group.MapPost("/receipts/{id}/query", async (string id, ProcurementStockReceiptQueryDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Receipt(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementStockReceiptDetails");
        group.MapPost("/operations/query", async (ProcurementStockReceiptRecoveryInputDto request, HttpContext http, [FromServices] ProcurementStockReceiptService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementStockReceiptRecoveryDto(row is null ? "not_found" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementStockReceipt");
    }

    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "procurement-stock-receipt-review") { if (value != expected) throw ProcurementStockReceiptRules.Invalid(); }
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementStockReceiptRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static ProcurementStockReceiptInputDto Input(BsonValue row) => new(R.Location(row["location"]), new(row["orderId"].AsInt64), Number(row["orderVersion"]),
        new(row["orderApprovalId"].AsInt64), new(row["inspectionId"].AsInt64), row["inspectionLineNumber"].AsInt32, row["packages"].AsInt32,
        new(row["poolId"].AsInt64), Number(row["poolRevision"]), row["receiptReference"].AsString, row["lotReference"].AsString,
        row["expiresOn"].IsBsonNull ? null : row["expiresOn"].AsString, row["reason"].AsString);
    private static SupplierClaimCapacityDto Capacity(BsonValue row) => new(row["basis"].AsString, new(row["inspectionId"].AsInt64), row["inspectionLineNumber"].AsInt32,
        row["inspectionHash"].AsString, Id(row["headId"]), Number(row["version"]), Number(row["totalPackages"]), Number(row["occupiedPackages"]), Number(row["remainingPackages"]));
    private static ProcurementStockReceiptSourceDto Source(BsonValue row) {
        var inspection = row["inspection"];
        return new(ProcurementFundingEndpoints.Order(row["order"]), new(new(inspection["id"].AsInt64), Number(inspection["version"]), inspection["hash"].AsString,
            inspection["inspectionDate"].AsString, inspection["inspectionReference"].AsString, new(inspection["noticeId"].AsInt64), new(inspection["intakeId"].AsInt64),
            inspection["lineNumber"].AsInt32, SupplierDocumentEndpoints.InspectionLine(inspection["line"])));
    }
    internal static ProcurementStockReceiptPoolDto Pool(BsonValue row) => new(new(row["id"].AsInt64), Number(row["revision"]), row["active"].AsBoolean,
        R.Location(row["location"]), R.Location(row["requestedLocation"]), row["physicalKey"].AsString, row["label"].AsString,
        row["mode"].AsString, row["control"].AsString, row["configurationHash"].AsString, new(row["policyId"].AsInt64), Number(row["policyRevision"]),
        row["policyHash"].AsString, row["writer"].AsString, row["reconciliationProcedureReference"].AsString, row["outageProcedureReference"].AsString);
    internal static ProcurementStockReceiptDto Receipt(BsonValue row) => new(new(row["id"].AsInt64), Number(row["revision"]), Input(row["input"]), Source(row["source"]), Pool(row["pool"]),
        Capacity(row["before"]), Capacity(row["after"]), new(row["lotId"].AsInt64), new(row["balanceId"].AsInt64), new(row["movementId"].AsInt64),
        Number(row["onHand"]), Number(row["held"]), Number(row["reserved"]), Number(row["expired"]), Number(row["available"]), row["valuationState"].AsString,
        row["valuation"].IsBsonNull ? null : Money.Parse(row["valuation"].AsString), row["state"].AsString, new(row["recordedBy"].AsInt64), Date(row["recordedAt"]));
    private static ProcurementStockReceiptResultDto Result(BsonValue row) => new(new(row["receiptId"].AsInt64), Number(row["revision"]), new(row["lotId"].AsInt64),
        new(row["balanceId"].AsInt64), new(row["movementId"].AsInt64), row["state"].AsString);
    private static ProcurementStockReceiptInspectionChoiceDto InspectionChoice(BsonValue row) => new(new(row["inspectionId"].AsInt64), Number(row["version"]), row["inspectionReference"].AsString,
        row["inspectionDate"].AsString, new(row["noticeId"].AsInt64), new(row["intakeId"].AsInt64), [.. row["lines"].AsBsonArray.Select(line => new ProcurementStockReceiptInspectionLineChoiceDto(
            line["lineNumber"].AsInt32, SupplierDocumentEndpoints.InspectionLine(line["input"]), Capacity(line["capacity"])))]);
}
