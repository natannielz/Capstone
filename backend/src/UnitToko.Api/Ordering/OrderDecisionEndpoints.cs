using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderReviewListDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Stage, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderReviewReadDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Stage, [property: JsonRequired] LongId? SuborderId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderDecisionDto([property: JsonRequired] string Stage, [property: JsonRequired] LongId? SuborderId,
    [property: JsonRequired] string Review, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderDecisionRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Stage, [property: JsonRequired] string Key);
public sealed record OrderReviewDto(LongId OrderId, LongId? SuborderId, string Stage, string Revision, string Review, OrderContextDto Context,
    string BuyerName, OrderDeliveryDto Delivery, string Currency, Money CatalogSubtotal, Money? Tax, Money? Charges, Money? Total,
    OrderQuoteLineDto[] Lines, string SpendingState, string? StoreState, bool CanDecide, DateTimeOffset OrderedAt);
public sealed record OrderReviewPageDto(OrderReviewDto[] Items, bool HasMore);
public sealed record OrderDecisionResultDto(LongId DecisionId, LongId OrderId, LongId? SuborderId, string Stage, string Revision, string State, DateTimeOffset RecordedAt);
public sealed record OrderDecisionReceiptDto(string Status, OrderDecisionResultDto? Result);

public static class OrderDecisionEndpoints
{
    public static void MapOrderDecisionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/reviews").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (OrderReviewListDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.ReviewListAsync(http.Features.Get<ResolvedActor>()!.Actor, Stage(request.Stage), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new OrderReviewPageDto([.. page.Items.Select(From)], page.HasMore));
        }).WithName("OrderReviewList");
        group.MapPost("/{id}/query", async (string id, OrderReviewReadDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var value = await service.ReviewOrderAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.SuborderId?.Value, Stage(request.Stage), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(value.Revision); return TypedResults.Ok(From(value));
        }).WithName("OrderReviewRead");
        group.MapPost("/{id}/decisions", async (string id, OrderDecisionDto request, HttpContext http, [FromServices] OrderSubmissionService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var value = await service.DecideOrderAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.SuborderId?.Value, Stage(request.Stage),
                AccessRequestMetadata.ExpectedVersion(http.Request), request.Review, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            var result = Result(value); http.Response.Headers.ETag = '"' + result.Revision + '"'; return TypedResults.Ok(result);
        }).WithName("RecordOrderDecision");
        group.MapPost("/operations/query", async (OrderDecisionRecoveryDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var row = await service.RecoverOrderDecisionAsync(http.Features.Get<ResolvedActor>()!.Actor, Stage(request.Stage), request.Key, ct);
            return TypedResults.Ok(new OrderDecisionReceiptDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverOrderDecision");
    }
    private static OrderReviewDto From(OrderReviewView row) => new(new(row.OrderId), row.SuborderId.HasValue ? new LongId(row.SuborderId.Value) : null,
        row.Stage.ToString(), OrderDraftInputDto.Number(row.Revision), row.Review, OrderContextDto.From(row.Context), row.BuyerName,
        new(row.Delivery.RecipientName, row.Delivery.Phone, row.Delivery.Address, row.Delivery.City, row.Delivery.PostalCode, row.Delivery.Instructions),
        row.Catalog.Currency, row.Catalog.CatalogSubtotal, row.Tax, row.Charges, row.Total, OrderSubmissionEndpoints.Lines(row.Catalog),
        row.SpendingState.ToString(), row.StoreState, row.CanDecide, row.OrderedAt);
    private static OrderDecisionResultDto Result(BsonDocument row) => new(new(row["decisionId"].AsInt64), new(row["orderId"].AsInt64),
        row["suborderId"].IsBsonNull ? null : new LongId(row["suborderId"].AsInt64), row["stage"].AsString, OrderDraftInputDto.Number(row["revision"].AsInt64),
        row["state"].AsString, DateTimeOffset.FromUnixTimeMilliseconds(row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
    private static OrderReviewStage Stage(string value) => value switch { "Spending" => OrderReviewStage.Spending, "Store" => OrderReviewStage.Store, _ => throw Invalid() };
    private static void Purpose(string value) { if (value != "order-review") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_order_review", "Choose the assigned order review stage");
}
