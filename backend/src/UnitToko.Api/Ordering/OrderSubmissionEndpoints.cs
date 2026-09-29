using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderCheckoutDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderDraftInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderSubmitDto([property: JsonRequired] OrderDraftInputDto Input, [property: JsonRequired] string Review, [property: JsonRequired] string Reason);
public sealed record OrderCheckoutReviewDto(OrderDraftInputDto Input, string Review, DateTimeOffset CalculatedAt, string Currency, Money CatalogSubtotal,
    Money Tax, Money Charges, Money Total, string SpendingState, OrderQuoteLineDto[] Lines);
public sealed record OrderSubmitResultDto(LongId OrderId, string Revision, LongId[] SuborderIds, DateTimeOffset RecordedAt, string SpendingState);
public sealed record OrderSubmitRecoveryDto(string Status, OrderSubmitResultDto? Result);
public sealed record SubmittedOrderDto(OrderSubmitResultDto Submission, OrderDraftInputDto Input, string Currency,
    Money CatalogSubtotal, Money Tax, Money Charges, Money Total, OrderQuoteLineDto[] Lines, OrderApprovalProgressDto Progress);
public sealed record OrderApprovalProgressDto(string Revision, string SpendingState, LongId[] AcceptedSuborders, LongId[] RejectedSuborders, bool Cancelled);
public sealed record SubmittedOrderPageDto(SubmittedOrderDto[] Items, bool HasMore);

public static class OrderSubmissionEndpoints
{
    public static void MapOrderSubmissionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/ordering/orders").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/preview/query", async (OrderCheckoutDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw Invalid();
            var result = await Selection(() => service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Input.Domain(), ct));
            return TypedResults.Ok(new OrderCheckoutReviewDto(OrderDraftInputDto.From(result.Input), result.Review, result.CalculatedAt, result.Catalog.Currency, result.Catalog.CatalogSubtotal,
                result.Tax, result.Charges, result.Total, result.SpendingState.ToString(), Lines(result.Catalog)));
        }).WithName("OrderCheckoutPreview");
        group.MapPost("", async (OrderSubmitDto request, HttpContext http, [FromServices] OrderSubmissionService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null || AccessRequestMetadata.ExpectedVersion(http.Request) != 0) throw Invalid();
            var result = await Selection(() => service.SubmitAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Input.Domain(), request.Review,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
            http.Response.Headers.ETag = "\"1\""; return TypedResults.Ok(Result(result));
        }).WithName("SubmitBranchOrder");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new OrderSubmitRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverBranchOrder");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(From(await service.ReadOwnAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadOwnBranchOrder");
        group.MapPost("/query", async (OrderDraftListDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Context is null || request.Limit != 25) throw Invalid();
            var result = await service.ListOwnAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Context.Domain(), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SubmittedOrderPageDto([.. result.Items.Select(From)], result.HasMore));
        }).WithName("ListOwnBranchOrders");
    }
    private static async Task<T> Selection<T>(Func<Task<T>> action) {
        try { return await action(); } catch (DomainValidationException error) when (error.Code is "order_selection_changed" or "order_delivery_changed") {
            throw new PersistenceConflictException(error.Code, "Review the current package, price and delivery address");
        }
    }
    internal static OrderQuoteLineDto[] Lines(OrderCatalogQuote quote) => [.. quote.Lines.Select(line => new OrderQuoteLineDto(line.FamilyId, line.FamilyName,
        line.PublicId.ToString("D"), line.SkuCode, CatalogResponses.From(line.Definition), CatalogPriceResponses.Price(line.Price)!, line.Packages,
        line.BaseUnits.ToString(CultureInfo.InvariantCulture), line.CatalogSubtotal))];
    private static SubmittedOrderDto From(SubmittedOrderView row) => new(new(row.Id, OrderDraftInputDto.Number(row.Revision), [.. row.SuborderIds], row.RecordedAt, row.InitialSpendingState),
        OrderDraftInputDto.From(row.Input), row.Catalog.Currency, row.Catalog.CatalogSubtotal, row.Tax, row.Charges, row.Total, Lines(row.Catalog),
        new(OrderDraftInputDto.Number(row.Progress.Revision), row.Progress.SpendingState.ToString(), [.. row.Progress.AcceptedSuborders], [.. row.Progress.RejectedSuborders], row.Progress.Cancelled));
    private static OrderSubmitResultDto Result(BsonDocument row) => new(new(row["orderId"].AsInt64), OrderDraftInputDto.Number(row["revision"].AsInt64),
        [.. row["suborderIds"].AsBsonArray.Select(value => new LongId(value.AsInt64))], DateTimeOffset.FromUnixTimeMilliseconds(row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch), row["spendingState"].AsString);
    private static void Purpose(string value) { if (value != "order-preparation") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_order_request", "Choose a branch and review this exact order before submission");
}
