using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementArrivalNoticeInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId OrderId, [property: JsonRequired] string OrderVersion, [property: JsonRequired] LongId OrderApprovalId,
    [property: JsonRequired] string NoticeDate, [property: JsonRequired] string SupplierDocumentReference,
    [property: JsonRequired] string? DdoRequestReference, [property: JsonRequired] string? DdoTelephoneReference,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int DeclaredContainers, [property: JsonRequired] string Note) {
    internal ProcurementArrivalNoticeInput Domain() {
        if (Location is null || OrderVersion != "1") throw ProcurementReceivingRules.Invalid();
        return ProcurementReceivingRules.Copy(new ProcurementArrivalNoticeInput(Location.Domain(), OrderId.Value, 1, OrderApprovalId.Value,
            ProcurementReceivingEndpoints.Day(NoticeDate), SupplierDocumentReference, DdoRequestReference, DdoTelephoneReference, DeclaredContainers, Note));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementContainerIntakeInputDto([property: JsonRequired] string ReceivedDate, [property: JsonRequired] string IntakeReference,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int MatchedContainers,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int ExcessContainers,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int HeldContainers,
    [property: JsonRequired] string Condition, [property: JsonRequired] string? Discrepancy) {
    internal ProcurementContainerIntakeInput Domain() => ProcurementReceivingRules.Copy(new ProcurementContainerIntakeInput(ProcurementReceivingEndpoints.Day(ReceivedDate),
        IntakeReference, MatchedContainers, ExcessContainers, HeldContainers, Condition, Discrepancy));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingReadInputDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingIntakesInputDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingNoticePreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementArrivalNoticeInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingNoticeSubmitDto([property: JsonRequired] ProcurementArrivalNoticeInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingIntakePreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementContainerIntakeInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingIntakeSubmitDto([property: JsonRequired] ProcurementContainerIntakeInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReceivingRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record ProcurementReceivingOrdersDto(ProcurementLocationDto Location, ProcurementFundingOrderDto[] Items, LongId? NextOrderId);
public sealed record ProcurementArrivalNoticeDto(LongId Id, string Version, ProcurementArrivalNoticeInputDto Input, ProcurementFundingOrderDto Order,
    ProcurementSourceDto Source, DateTimeOffset RecordedAt, string Reason, string State);
public sealed record ProcurementContainerCapacityDto(string Version, int DeclaredContainers, int MatchedContainers, int RemainingContainers,
    string ExcessContainers, string HeldContainers, string ReceivedContainers);
public sealed record ProcurementContainerReceiptDto(LongId Id, LongId NoticeId, string Version, ProcurementContainerIntakeInputDto Input,
    DateTimeOffset RecordedAt, string Reason, string State);
public sealed record ProcurementReceivingNoticePreviewDto(ProcurementArrivalNoticeInputDto Input, ProcurementFundingOrderDto Order, ProcurementSourceDto Source, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementReceivingIntakePreviewDto(ProcurementArrivalNoticeDto Notice, ProcurementContainerCapacityDto Capacity,
    ProcurementContainerIntakeInputDto Input, ProcurementContainerCapacityDto After, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementReceivingListItemDto(ProcurementArrivalNoticeDto Notice, ProcurementContainerCapacityDto Capacity);
public sealed record ProcurementReceivingHistoryDto(ProcurementLocationDto Location, ProcurementReceivingListItemDto[] Items, LongId? NextNoticeId);
public sealed record ProcurementReceivingReadDto(ProcurementArrivalNoticeDto Notice, ProcurementContainerCapacityDto Capacity, ProcurementContainerReceiptDto[] Receipts, LongId? NextReceiptId);
public sealed record ProcurementReceivingIntakesDto(LongId NoticeId, ProcurementContainerReceiptDto[] Items, LongId? NextReceiptId);
public sealed record ProcurementReceivingResultDto(string Operation, LongId NoticeId, LongId? ReceiptId, string Version, string State, DateTimeOffset RecordedAt);
public sealed record ProcurementReceivingRecoveryDto(string Status, ProcurementReceivingResultDto? Result);

public static class ProcurementReceivingEndpoints
{
    public static void MapProcurementReceivingEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/receiving").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementReceivingReadInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementReceivingTargets");
        group.MapPost("/orders/query", async (ProcurementReceivingListInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OrdersAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementReceivingOrdersDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(ProcurementFundingEndpoints.Order)], Id(row["nextOrderId"])));
        }).WithName("ProcurementReceivingOrders");
        group.MapPost("/notices/preview/query", async (ProcurementReceivingNoticePreviewInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementReceivingRules.Invalid();
            var row = await service.PreviewNoticeAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new ProcurementReceivingNoticePreviewDto(NoticeInput(row["input"]), ProcurementFundingEndpoints.Order(row["order"]), R.Source(row["source"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("ProcurementReceivingNoticePreview");
        group.MapPost("/notices", async (ProcurementReceivingNoticeSubmitDto request, HttpContext http, [FromServices] ProcurementReceivingService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementReceivingRules.Invalid();
            return TypedResults.Ok(Result(await service.RecordNoticeAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RecordProcurementArrivalNotice");
        group.MapPost("/notices/query", async (ProcurementReceivingListInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementReceivingHistoryDto(R.Location(row["location"]),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementReceivingListItemDto(Notice(item["notice"]), Capacity(item["capacity"])))], Id(row["nextNoticeId"])));
        }).WithName("ProcurementReceivingHistory");
        group.MapPost("/notices/{id}/query", async (string id, ProcurementReceivingReadInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct);
            return TypedResults.Ok(new ProcurementReceivingReadDto(Notice(row["notice"]), Capacity(row["capacity"]), [.. row["receipts"].AsBsonArray.Select(Receipt)], Id(row["nextReceiptId"])));
        }).WithName("ProcurementReceivingRead");
        group.MapPost("/notices/{id}/intakes/query", async (string id, ProcurementReceivingIntakesInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.IntakesAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementReceivingIntakesDto(new(row["noticeId"].AsInt64), [.. row["items"].AsBsonArray.Select(Receipt)], Id(row["nextReceiptId"])));
        }).WithName("ProcurementReceivingIntakes");
        group.MapPost("/notices/{id}/intake-preview/query", async (string id, ProcurementReceivingIntakePreviewInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementReceivingRules.Invalid();
            var row = await service.PreviewIntakeAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Input.Domain(), ct);
            return TypedResults.Ok(new ProcurementReceivingIntakePreviewDto(Notice(row["notice"]), Capacity(row["capacity"]), IntakeInput(row["input"]),
                Capacity(row["after"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("ProcurementReceivingIntakePreview");
        group.MapPost("/notices/{id}/intakes", async (string id, ProcurementReceivingIntakeSubmitDto request, HttpContext http, [FromServices] ProcurementReceivingService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementReceivingRules.Invalid();
            return TypedResults.Ok(Result(await service.ReceiveAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReceiveProcurementContainers");
        group.MapPost("/operations/query", async (ProcurementReceivingRecoveryInputDto request, HttpContext http, [FromServices] ProcurementReceivingService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw ProcurementReceivingRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementReceivingRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementReceiving");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "receiving-review") throw ProcurementReceivingRules.Invalid(); }
    internal static DateOnly Day(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw ProcurementReceivingRules.Invalid();
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementReceivingRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static ProcurementArrivalNoticeInputDto NoticeInput(BsonValue value) => new(R.Location(value["location"]), new(value["orderId"].AsInt64), Number(value["orderVersion"]),
        new(value["orderApprovalId"].AsInt64), value["noticeDate"].AsString, value["supplierDocumentReference"].AsString,
        Text(value["ddoRequestReference"]), Text(value["ddoTelephoneReference"]), value["declaredContainers"].AsInt32, value["note"].AsString);
    private static ProcurementContainerIntakeInputDto IntakeInput(BsonValue value) => new(value["receivedDate"].AsString, value["intakeReference"].AsString,
        value["matchedContainers"].AsInt32, value["excessContainers"].AsInt32, value["heldContainers"].AsInt32, value["condition"].AsString, Text(value["discrepancy"]));
    private static ProcurementArrivalNoticeDto Notice(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), NoticeInput(value["input"]),
        ProcurementFundingEndpoints.Order(value["order"]), R.Source(value["source"]), Date(value["recordedAt"]), value["reason"].AsString, value["state"].AsString);
    private static ProcurementContainerCapacityDto Capacity(BsonValue value) => new(Number(value["version"]), value["declaredContainers"].AsInt32, value["matchedContainers"].AsInt32,
        value["remainingContainers"].AsInt32, Number(value["excessContainers"]), Number(value["heldContainers"]), Number(value["receivedContainers"]));
    private static ProcurementContainerReceiptDto Receipt(BsonValue value) => new(new(value["id"].AsInt64), new(value["noticeId"].AsInt64), Number(value["version"]),
        IntakeInput(value["input"]), Date(value["recordedAt"]), value["reason"].AsString, value["state"].AsString);
    private static ProcurementReceivingResultDto Result(BsonValue value) => new(value["operation"].AsString, new(value["noticeId"].AsInt64), Id(value["receiptId"]),
        Number(value["version"]), value["state"].AsString, Date(value["recordedAt"]));
}
