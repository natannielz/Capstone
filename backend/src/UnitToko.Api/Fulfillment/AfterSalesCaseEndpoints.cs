using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AfterSalesCaseLineDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId SkuId, [property: JsonRequired] string ReportedBaseUnits);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AfterSalesCaseRequestDto([property: JsonRequired] LongId InvoiceId, [property: JsonRequired] string Binding, [property: JsonRequired] string Issue,
    [property: JsonRequired] ImmutableArray<AfterSalesCaseLineDto> Lines, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AfterSalesCaseQueryDto([property: JsonRequired] string Purpose, LongId? BeforeId);
public sealed record AfterSalesSourceLineDto(LongId OrderId, LongId ShipmentId, LongId ReceiptId, LongId SkuId, string SkuCode, string ProductName,
    string PackageLabel, int BaseUnitsPerPackage, string BaseUnitCode, string AcceptedBaseUnits);
public sealed record AfterSalesSourceDto(LongId InvoiceId, string Binding, string Number, string BuyerName, string SellerName, DateTimeOffset IssuedAt, ImmutableArray<AfterSalesSourceLineDto> Lines);
public sealed record AfterSalesCaseDto(LongId Id, string Revision, DateTimeOffset RequestedAt, string State, string Issue, string ReportedProblem, AfterSalesSourceDto Source, ImmutableArray<AfterSalesCaseLineDto> Lines);
public sealed record AfterSalesCaseResultDto(LongId CaseId, LongId InvoiceId, string Revision, DateTimeOffset RequestedAt, string Operation);
public sealed record AfterSalesCaseRecoveryDto(string Status, AfterSalesCaseResultDto? Result);
public sealed record AfterSalesCasePageDto(ImmutableArray<AfterSalesCaseDto> Items, bool HasMore);
public static class AfterSalesCaseEndpoints
{
    public static void MapAfterSalesCaseEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/cases").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/sales/{invoiceId}/query", async (string invoiceId, OrderReadDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http); if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw AfterSalesCasePolicy.Invalid();
            return TypedResults.Ok(Source(await service.SourceAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(invoiceId).Value, ct)));
        }).WithName("ReadAfterSalesCaseSource");
        group.MapPost("", async (AfterSalesCaseRequestDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); if (AccessRequestMetadata.ExpectedVersion(http.Request) != 0) throw AfterSalesCasePolicy.Invalid();
            if (request.Lines.IsDefaultOrEmpty || request.Lines.Length > AfterSalesCasePolicy.MaximumLines || request.Lines.Any(line => line is null)) throw AfterSalesCasePolicy.Invalid();
            var input = new AfterSalesCaseInput(request.InvoiceId.Value, request.Binding, request.Issue, [.. request.Lines.Select(line => new AfterSalesCaseLine(line.ReceiptId.Value, line.SkuId.Value, LongId.Parse(line.ReportedBaseUnits).Value))]);
            return TypedResults.Ok(Result(await service.RequestAsync(http.Features.Get<ResolvedActor>()!.Actor, input, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RequestAfterSalesCase");
        group.MapPost("/query", async (AfterSalesCaseQueryDto request, HttpContext http, AfterSalesCaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http); var page = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new AfterSalesCasePageDto([.. page.Items.Select(View)], page.HasMore));
        }).WithName("ListAfterSalesCases");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, AfterSalesCaseService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http); if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw AfterSalesCasePolicy.Invalid();
            http.Response.Headers.ETag = "\"1\""; return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadAfterSalesCase");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, AfterSalesCaseService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw AfterSalesCasePolicy.Invalid(); Private(http);
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct); return TypedResults.Ok(new AfterSalesCaseRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverAfterSalesCaseRequest");
    }
    private static AfterSalesSourceDto Source(AfterSalesSourceView source) => new(source.InvoiceId, source.Binding, source.Number, source.BuyerName, source.SellerName, source.IssuedAt,
        [.. source.Lines.Select(line => new AfterSalesSourceLineDto(line.OrderId, line.ShipmentId, line.ReceiptId, line.SkuId, line.SkuCode, line.ProductName, line.PackageLabel,
            line.BaseUnitsPerPackage, line.BaseUnitCode, Number(line.AcceptedBaseUnits)))]);
    private static AfterSalesCaseDto View(AfterSalesCaseView view) => new(view.Id, Number(view.Revision), view.RequestedAt, view.State, view.Issue, view.ReportedProblem, Source(view.Source),
        [.. view.Lines.Select(line => new AfterSalesCaseLineDto(new(line.ReceiptId), new(line.SkuId), Number(line.ReportedBaseUnits)))]);
    private static AfterSalesCaseResultDto Result(BsonDocument row) => new(new(row["caseId"].AsInt64), new(row["invoiceId"].AsInt64), Number(row["revision"].AsInt64), new(row["requestedAt"].ToUniversalTime()), row["operation"].AsString);
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void Purpose(string purpose) { if (purpose != "after-sales-case") throw AfterSalesCasePolicy.Invalid(); }
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
