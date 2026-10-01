using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnedStockLineDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId SkuId,
    [property: JsonRequired] string ReceivedBaseUnits, [property: JsonRequired] string ObservedCondition);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AfterSalesReturnRequestDto([property: JsonRequired] string Binding, [property: JsonRequired] string PublicNote,
    [property: JsonRequired] ImmutableArray<ReturnedStockLineDto> Lines, [property: JsonRequired] string Reason);
public sealed record AfterSalesReturnLimitDto(LongId ReceiptId, LongId SkuId, LongId WarehouseId, string AuthorizedBaseUnits, string ReceivedBaseUnits);
public sealed record AfterSalesReturnReceiptDto(LongId Id, string Revision, DateTimeOffset ReceivedAt, string PublicNote, ImmutableArray<ReturnedStockLineDto> Lines);
public sealed record AfterSalesReturnDto(LongId CaseId, LongId InvoiceId, string Revision, string Binding, ImmutableArray<AfterSalesReturnLimitDto> Limits,
    ImmutableArray<AfterSalesReturnReceiptDto> Recent, bool HasEarlier);
public sealed record AfterSalesReturnResultDto(LongId ReturnId, LongId CaseId, LongId InvoiceId, string Revision, DateTimeOffset ReceivedAt, string State, string Operation);
public sealed record AfterSalesReturnRecoveryDto(string Status, AfterSalesReturnResultDto? Result);
public static class AfterSalesReturnEndpoints
{
    public static void MapAfterSalesReturnEndpoints(this WebApplication app) {
        app.MapAfterSalesInspectionEndpoints();
        app.MapAfterSalesResolutionEndpoints();
        var group = app.MapGroup("/api/v1/fulfillment/cases/{id}/returns").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        foreach (var preview in new[] { false, true }) {
            var receiving = preview;
            group.MapPost(receiving ? "/preview/query" : "/query", async (string id, OrderReadDto request, HttpContext http, AfterSalesCaseService service,
                [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
                Private(http); if (request.Purpose != "after-sales-return" || AccessRequestMetadata.ExpectedVersion(http.Request) != 2) throw ReturnedStockPolicy.Invalid();
                var view = await service.ReadReturnsAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, receiving, ct);
                http.Response.Headers.ETag = "\"" + Number(view.Revision) + "\""; return TypedResults.Ok(View(view));
            }).WithName(receiving ? "PreviewAfterSalesReturnIntake" : "ReadAfterSalesReturns");
        }
        group.MapPost("", async (string id, AfterSalesReturnRequestDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); if (request.Lines.IsDefaultOrEmpty || request.Lines.Length > 100 || request.Lines.Any(line => line is null)) throw ReturnedStockPolicy.Invalid();
            var input = new ReturnedStockInput(request.PublicNote, [.. request.Lines.Select(line => new ReturnedStockLine(line.ReceiptId.Value, line.SkuId.Value, LongId.Parse(line.ReceivedBaseUnits).Value, line.ObservedCondition))]);
            return TypedResults.Ok(Result(await service.ReceiveReturnAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Binding,
                input, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReceiveAfterSalesReturn");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, AfterSalesCaseService service, CancellationToken ct) => {
            Private(http); if (request.Purpose != "operation-recovery") throw ReturnedStockPolicy.Invalid();
            var saved = await service.RecoverReturnAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new AfterSalesReturnRecoveryDto(saved is null ? "not_confirmed" : "committed", saved is null ? null : Result(saved)));
        }).WithName("RecoverAfterSalesReturn");
    }
    private static AfterSalesReturnDto View(AfterSalesReturnView value) => new(value.CaseId, value.InvoiceId, Number(value.Revision), value.Binding,
        [.. value.Limits.Select(line => new AfterSalesReturnLimitDto(line.ReceiptId, line.SkuId, line.WarehouseId, Number(line.AuthorizedBaseUnits), Number(line.ReceivedBaseUnits)))],
        [.. value.Recent.Select(row => new AfterSalesReturnReceiptDto(row.Id, Number(row.Revision), row.ReceivedAt, row.PublicNote,
            [.. row.Lines.Select(line => new ReturnedStockLineDto(new(line.ReceiptId), new(line.SkuId), Number(line.ReceivedBaseUnits), line.ObservedCondition))]))], value.HasEarlier);
    private static AfterSalesReturnResultDto Result(BsonDocument value) => new(new(value["returnId"].AsInt64), new(value["caseId"].AsInt64), new(value["invoiceId"].AsInt64), Number(value["revision"].AsInt64),
        new(value["receivedAt"].ToUniversalTime()), value["state"].AsString, value["operation"].AsString);
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
