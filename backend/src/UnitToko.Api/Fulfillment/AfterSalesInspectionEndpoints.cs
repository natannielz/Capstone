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
public sealed record ReturnInspectionLineDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId SkuId, [property: JsonRequired] string InspectedBaseUnits,
    [property: JsonRequired] string Identity, [property: JsonRequired] string Condition, [property: JsonRequired] string ObservedIdentity, [property: JsonRequired] string? ObservedLotLabel);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnInspectionRequestDto([property: JsonRequired] string Binding, [property: JsonRequired] string PublicNote, [property: JsonRequired] ImmutableArray<ReturnInspectionLineDto> Lines, [property: JsonRequired] string Reason);
public sealed record ReturnInspectionAssessmentLineDto(LongId ReceiptId, LongId SkuId, string InspectedBaseUnits, string Identity, string Condition);
public sealed record ReturnInspectionAssessmentDto(DateTimeOffset InspectedAt, string PublicNote, ImmutableArray<ReturnInspectionAssessmentLineDto> Lines);
public sealed record ReturnInspectionDto(LongId CaseId, LongId InvoiceId, string Revision, string Binding, AfterSalesReturnReceiptDto Receipt, ReturnInspectionAssessmentDto? Inspection);
public sealed record ReturnInspectionResultDto(LongId ReturnId, LongId CaseId, LongId InvoiceId, string Revision, DateTimeOffset InspectedAt, string State, string Operation);
public sealed record ReturnInspectionRecoveryDto(string Status, ReturnInspectionResultDto? Result);
public static class AfterSalesInspectionEndpoints
{
    public static void MapAfterSalesInspectionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/cases/{id}/returns/{returnId}/inspection").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        foreach (var preview in new[] { false, true }) {
            var inspecting = preview;
            group.MapPost(inspecting ? "/preview/query" : "/query", async (string id, string returnId, OrderReadDto request, HttpContext http, AfterSalesCaseService service,
                [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
                Private(http); if (request.Purpose != "after-sales-return-inspection" || AccessRequestMetadata.ExpectedVersion(http.Request) != 2) throw ReturnedStockInspectionPolicy.Invalid();
                var value = await service.ReadInspectionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, inspecting, ct);
                http.Response.Headers.ETag = "\"" + Number(value.Revision) + "\""; return TypedResults.Ok(View(value));
            }).WithName(inspecting ? "PreviewAfterSalesReturnInspection" : "ReadAfterSalesReturnInspection");
        }
        group.MapPost("", async (string id, string returnId, ReturnInspectionRequestDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Private(http); if (request.Lines.IsDefaultOrEmpty || request.Lines.Length > 100 || request.Lines.Any(line => line is null)) throw ReturnedStockInspectionPolicy.Invalid();
            var input = new ReturnedStockInspectionInput(request.PublicNote, [.. request.Lines.Select(line => new ReturnedStockInspectionLine(line.ReceiptId.Value, line.SkuId.Value, LongId.Parse(line.InspectedBaseUnits).Value,
                line.Identity, line.Condition, line.ObservedIdentity, line.ObservedLotLabel))]);
            return TypedResults.Ok(Result(await service.InspectReturnAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                request.Binding, input, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("InspectAfterSalesReturn");
        group.MapPost("/operations/query", async (string id, string returnId, OrderDraftRecoveryDto request, HttpContext http, AfterSalesCaseService service, CancellationToken ct) => {
            Private(http); if (request.Purpose != "operation-recovery") throw ReturnedStockInspectionPolicy.Invalid();
            var value = await service.RecoverInspectionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Key, ct);
            return TypedResults.Ok(new ReturnInspectionRecoveryDto(value is null ? "not_confirmed" : "committed", value is null ? null : Result(value)));
        }).WithName("RecoverAfterSalesReturnInspection");
    }
    private static ReturnInspectionDto View(AfterSalesReturnInspectionView value) => new(value.CaseId, value.InvoiceId, Number(value.Revision), value.Binding,
        new(value.Receipt.Id, Number(value.Receipt.Revision), value.Receipt.ReceivedAt, value.Receipt.PublicNote, [.. value.Receipt.Lines.Select(line => new ReturnedStockLineDto(new(line.ReceiptId), new(line.SkuId), Number(line.ReceivedBaseUnits), line.ObservedCondition))]),
        value.Inspection is null ? null : new(value.Inspection.InspectedAt, value.Inspection.PublicNote, [.. value.Inspection.Lines.Select(line => new ReturnInspectionAssessmentLineDto(line.ReceiptId, line.SkuId, Number(line.InspectedBaseUnits), line.Identity, line.Condition))]));
    private static ReturnInspectionResultDto Result(BsonDocument value) => new(new(value["returnId"].AsInt64), new(value["caseId"].AsInt64), new(value["invoiceId"].AsInt64), Number(value["revision"].AsInt64),
        new(value["inspectedAt"].ToUniversalTime()), value["state"].AsString, value["operation"].AsString);
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
