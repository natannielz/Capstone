using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderDeliveryDto([property: JsonRequired] string RecipientName, [property: JsonRequired] string Phone,
    [property: JsonRequired] string Address, [property: JsonRequired] string City, [property: JsonRequired] string? PostalCode,
    [property: JsonRequired] string? Instructions);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderDraftInputDto([property: JsonRequired] OrderContextDto Context, [property: JsonRequired] OrderPackageSelectionDto[] Selections,
    [property: JsonRequired] OrderDeliveryDto? Delivery, [property: JsonRequired] string? PurchaseReference)
{
    internal OrderDraftInput Domain() {
        if (Context is null || Selections is null || Selections.Length is < 1 or > 100 || Selections.Any(line => line is null)) throw new DomainValidationException("invalid_order_draft", "Choose explicit packages");
        return OrderDraftPolicy.Copy(new(Context.Domain(), [.. Selections.Select(line => new OrderPackageSelection(line.SkuId, line.DefinitionId,
            CatalogInputs.PositiveVersion(line.DefinitionVersion), line.PriceId, CatalogInputs.PositiveVersion(line.PriceVersion), line.Packages))],
            Delivery is null ? null : new(Delivery.RecipientName, Delivery.Phone, Delivery.Address, Delivery.City, Delivery.PostalCode, Delivery.Instructions), PurchaseReference));
    }
    internal static OrderDraftInputDto From(OrderDraftInput input) => new(OrderContextDto.From(input.Context),
        [.. input.Selections.Select(line => new OrderPackageSelectionDto(line.SkuId, line.DefinitionId, Number(line.DefinitionVersion), line.PriceId, Number(line.PriceVersion), line.Packages))],
        input.Delivery is null ? null : new(input.Delivery.RecipientName, input.Delivery.Phone, input.Delivery.Address, input.Delivery.City, input.Delivery.PostalCode, input.Delivery.Instructions), input.PurchaseReference);
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderDraftSaveDto([property: JsonRequired] LongId? DraftId, [property: JsonRequired] OrderDraftInputDto Input, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record OrderDraftListDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderContextDto Context, LongId? BeforeId = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderDraftRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Key);
public sealed record OrderDraftDto(LongId Id, string Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, OrderDraftInputDto Input);
public sealed record OrderDraftPageDto(OrderDraftDto[] Items, bool HasMore);
public sealed record OrderDraftResultDto(LongId DraftId, LongId VersionId, string Revision, DateTimeOffset RecordedAt);
public sealed record OrderDraftReceiptDto(string Status, OrderDraftResultDto? Result);

public static class OrderDraftEndpoints
{
    public static void MapOrderDraftEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/ordering/drafts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (OrderDraftListDto request, HttpContext http, [FromServices] OrderDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Context is null) throw Invalid();
            var result = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Context.Domain(), request.BeforeId?.Value, request.Limit, ct);
            return TypedResults.Ok(new OrderDraftPageDto([.. result.Items.Select(From)], result.HasMore));
        }).WithName("OrderDraftList");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, [FromServices] OrderDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); var result = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Revision); return TypedResults.Ok(From(result));
        }).WithName("OrderDraftRead");
        group.MapPost("", async (OrderDraftSaveDto request, HttpContext http, [FromServices] OrderDraftService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null) throw Invalid();
            try {
                var result = Result(await service.SaveAsync(http.Features.Get<ResolvedActor>()!.Actor, request.DraftId?.Value,
                    AccessRequestMetadata.ExpectedVersion(http.Request), request.Input.Domain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
                http.Response.Headers.ETag = '"' + result.Revision + '"'; return TypedResults.Ok(result);
            } catch (DomainValidationException error) when (error.Code == "order_selection_changed") {
                throw new PersistenceConflictException(error.Code, "Package or price changed; reload and review");
            }
        }).WithName("SaveOrderDraft");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, [FromServices] OrderDraftService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var result = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new OrderDraftReceiptDto(result is null ? "not_confirmed" : "committed", result is null ? null : Result(result)));
        }).WithName("RecoverOrderDraftSave");
    }
    private static OrderDraftDto From(OrderDraftView value) => new(value.Id, OrderDraftInputDto.Number(value.Revision), value.CreatedAt, value.UpdatedAt, OrderDraftInputDto.From(value.Input));
    private static OrderDraftResultDto Result(BsonDocument value) => new(new(value["draftId"].AsInt64), new(value["versionId"].AsInt64),
        OrderDraftInputDto.Number(value["revision"].AsInt64), DateTimeOffset.FromUnixTimeMilliseconds(value["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
    private static void Purpose(string value) { if (value != "order-preparation") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_order_draft", "Choose the draft and current branch");
}
