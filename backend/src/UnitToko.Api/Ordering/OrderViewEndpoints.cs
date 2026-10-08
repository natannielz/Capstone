using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderHistoryFilterDto([property: JsonRequired] OrderContextDto? Context, [property: JsonRequired] string Sort) {
    internal OrderHistoryFilter Domain() { var value = new OrderHistoryFilter(Context?.Domain(), Sort); value.Validate(); return value; }
    internal static OrderHistoryFilterDto From(OrderHistoryFilter value) => new(value.Context is null ? null : OrderContextDto.From(value.Context), value.Sort);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FilteredOrderRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderHistoryFilterDto Filter, string? Cursor = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderViewListRequestDto([property: JsonRequired] string Purpose, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderViewChangeDto([property: JsonRequired] string Action, [property: JsonRequired] LongId? ViewId,
    [property: JsonRequired] string? Name, [property: JsonRequired] OrderHistoryFilterDto? Filter, [property: JsonRequired] string Reason);
public sealed record FilteredOrderPageDto(SubmittedOrderDto[] Items, bool HasMore, string? NextCursor);
public sealed record SavedOrderViewDto(LongId Id, string Revision, string Name, OrderHistoryFilterDto Filter, DateTimeOffset UpdatedAt);
public sealed record SavedOrderViewPageDto(SavedOrderViewDto[] Items, LongId? NextId);
public sealed record OrderViewResultDto(LongId ViewId, LongId VersionId, string Revision, string Action, DateTimeOffset RecordedAt);
public sealed record OrderViewRecoveryDto(string Status, OrderViewResultDto? Result);

public static class OrderViewEndpoints
{
    public static void MapOrderViewEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/order-views").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/contexts/query", async (OrderReadDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); var result = await service.HistoryContextsAsync(http.Features.Get<ResolvedActor>()!.Actor, ct);
            return TypedResults.Ok(new OrderContextsDto([.. result.Select(value => new OrderContextOptionDto(OrderContextDto.From(value.Context), value.OrganizationName, value.BranchName, value.DivisionName))]));
        }).WithName("OwnOrderHistoryContexts");
        group.MapPost("/orders/query", async (FilteredOrderRequestDto request, HttpContext http, [FromServices] OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Filter is null) throw OrderHistoryFilter.Invalid();
            var result = await service.ListOwnFilteredAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Filter.Domain(), request.Cursor, ct);
            return TypedResults.Ok(new FilteredOrderPageDto([.. result.Items.Select(OrderSubmissionEndpoints.From)], result.HasMore, result.NextCursor));
        }).WithName("FilterOwnBranchOrders");
        group.MapPost("/query", async (OrderViewListRequestDto request, HttpContext http, [FromServices] OrderViewService service, CancellationToken ct) => {
            Purpose(request.Purpose); var result = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new SavedOrderViewPageDto([.. result.Items.Select(From)], result.NextId is { } id ? new(id) : null));
        }).WithName("ListSavedOrderViews");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, [FromServices] OrderViewService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(From(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadSavedOrderView");
        group.MapPost("", async (OrderViewChangeDto request, HttpContext http, [FromServices] OrderViewService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var result = await service.ChangeAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Action, request.ViewId?.Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.Name, request.Filter?.Domain(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            var dto = Result(result); http.Response.Headers.ETag = "\"" + dto.Revision + "\""; return TypedResults.Ok(dto);
        }).WithName("ChangeSavedOrderView");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, [FromServices] OrderViewService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw OrderHistoryFilter.Invalid();
            var result = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new OrderViewRecoveryDto(result is null ? "not_confirmed" : "committed", result is null ? null : Result(result)));
        }).WithName("RecoverSavedOrderView");
    }
    private static void Purpose(string value) { if (value != "order-history") throw OrderHistoryFilter.Invalid(); }
    private static SavedOrderViewDto From(SavedOrderView value) => new(value.Id, OrderDraftInputDto.Number(value.Revision), value.Name, OrderHistoryFilterDto.From(value.Filter), value.UpdatedAt);
    private static OrderViewResultDto Result(BsonDocument value) => new(new(value["viewId"].AsInt64), new(value["versionId"].AsInt64),
        OrderDraftInputDto.Number(value["revision"].AsInt64), value["action"].AsString, DateTimeOffset.FromUnixTimeMilliseconds(value["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
}
