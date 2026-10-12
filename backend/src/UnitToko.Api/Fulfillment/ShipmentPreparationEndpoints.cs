using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Fulfillment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreparationPageRequest([property: JsonRequired] string Purpose, LongId? BeforeId = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ShipmentPackageDto([property: JsonRequired] LongId SkuId, [property: JsonRequired] int Packages, string LineRevision = "0");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SavePreparationDto([property: JsonRequired] string Review, [property: JsonRequired] ShipmentPackageDto[] Lines, [property: JsonRequired] string Reason,
    OperationalScanSelectionDto[]? ScanSelections = null);
public sealed record FulfillmentOrderLineDto(LongId SkuId, string PublicId, string SkuCode, string ProductName, CatalogDefinitionDto Definition, int OrderedPackages, int RemainingPackages, string LineRevision = "0");
public sealed record FulfillmentOrderDto(LongId OrderId, LongId SuborderId, string Revision, string Review, string BuyerName,
    OrderDeliveryDto Delivery, FulfillmentOrderLineDto[] Lines, bool CanPrepare, DateTimeOffset OrderedAt);
public sealed record FulfillmentOrdersDto(FulfillmentOrderDto[] Items, bool HasMore);
public sealed record PreparationDto(LongId Id, string Revision, DateTimeOffset RecordedAt, FulfillmentOrderDto AcceptedSource, ShipmentPackageDto[] Lines, bool CurrentSourceMatches);
public sealed record PreparationReadDto(PreparationDto? Preparation);
public sealed record PreparationResultDto(LongId PreparationId, LongId VersionId, string Revision, LongId OrderId, LongId SuborderId, DateTimeOffset RecordedAt);
public sealed record PreparationRecoveryDto(string Status, PreparationResultDto? Result);

public static class ShipmentPreparationEndpoints
{
    public static void MapShipmentPreparationEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/preparations").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/sources/query", async (PreparationPageRequest request, HttpContext http, OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Limit != 25) throw Invalid();
            var page = await service.ListFulfillmentOrdersAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new FulfillmentOrdersDto([.. page.Items.Select(Source)], page.HasMore));
        }).WithName("ListShipmentPreparationSources");
        group.MapPost("/sources/{suborderId}/query", async (string suborderId, OrderReadDto request, HttpContext http, OrderSubmissionService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Source(await service.ReadFulfillmentOrderAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, ct)));
        }).WithName("ReadShipmentPreparationSource");
        group.MapPost("/{suborderId}/query", async (string suborderId, OrderReadDto request, HttpContext http, ShipmentPreparationService service, CancellationToken ct) => {
            Purpose(request.Purpose); var saved = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, ct);
            return TypedResults.Ok(new PreparationReadDto(saved is null ? null : new(saved.Id, Number(saved.Revision), saved.RecordedAt, Source(saved.AcceptedSource),
                [.. saved.Lines.Select(line => new ShipmentPackageDto(line.SkuId, line.Packages, OrderDraftInputDto.Number(line.LineRevision)))], saved.CurrentSourceMatches)));
        }).WithName("ReadShipmentPreparation");
        group.MapPost("/{suborderId}", async (string suborderId, SavePreparationDto request, HttpContext http, ShipmentPreparationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Lines is null || request.Lines.Length is < 1 or > 100 || request.Lines.Any(line => line is null)) throw Invalid();
            var result = await service.SaveAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Review,
                request.Lines.Select(line => new ShipmentPackageSelection(line.SkuId, line.Packages, OrderLineInputs.Revision(line.LineRevision))).ToImmutableArray(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct, OperationalScanSelectionDto.DomainAll(request.ScanSelections));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result["revision"].AsInt64); return TypedResults.Ok(Result(result));
        }).WithName("SaveShipmentPreparation");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentPreparationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var receipt = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new PreparationRecoveryDto(receipt is null ? "not_confirmed" : "committed", receipt is null ? null : Result(receipt)));
        }).WithName("RecoverShipmentPreparation");
    }
    internal static FulfillmentOrderDto Source(FulfillmentOrderView source) => new(source.OrderId, source.SuborderId, Number(source.Revision), source.Review,
        source.BuyerName, new(source.Delivery.RecipientName, source.Delivery.Phone, source.Delivery.Address, source.Delivery.City, source.Delivery.PostalCode, source.Delivery.Instructions),
        [.. source.Lines.Select(line => new FulfillmentOrderLineDto(line.SkuId, line.PublicId.ToString("D"), line.SkuCode, line.ProductName, CatalogResponses.From(line.Package), line.OrderedPackages, line.RemainingPackages,
            OrderDraftInputDto.Number(line.LineRevision)))], source.CanPrepare, source.OrderedAt);
    private static PreparationResultDto Result(BsonDocument value) => new(new(value["preparationId"].AsInt64), new(value["versionId"].AsInt64), Number(value["revision"].AsInt64),
        new(value["orderId"].AsInt64), new(value["suborderId"].AsInt64), DateTimeOffset.FromUnixTimeMilliseconds(value["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "shipment-preparation") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_shipment_preparation_request", "Review the current accepted store order and package quantities");
}
