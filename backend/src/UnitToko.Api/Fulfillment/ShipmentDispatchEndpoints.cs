using System.Collections.Immutable;
using System.Globalization;
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
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ShipmentLotDto([property: JsonRequired] LongId ReservationId, [property: JsonRequired] int Packages);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispatchSelectionDto([property: JsonRequired] LongId PreparationId, [property: JsonRequired] string PreparationRevision,
    [property: JsonRequired] LongId CourierId, [property: JsonRequired] ShipmentLotDto[] Lots);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispatchPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] DispatchSelectionDto Selection);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispatchCommandDto([property: JsonRequired] DispatchSelectionDto Selection, [property: JsonRequired] string Review, [property: JsonRequired] string Reason);
public sealed record DispatchQueueItemDto(LongId PreparationId, string PreparationRevision, FulfillmentOrderDto Source, ShipmentPackageDto[] Lines, bool CanDispatch);
public sealed record DispatchQueueDto(DispatchQueueItemDto[] Items, bool HasMore);
public sealed record DispatchReservedLotDto(LongId ReservationId, LongId SkuId, LongId DefinitionId, LongId LotId, int RemainingPackages, DateOnly? ExpiresOn);
public sealed record DispatchPreparationDto(LongId PreparationId, string PreparationRevision, FulfillmentOrderDto Source, ShipmentPackageDto[] Lines, DispatchReservedLotDto[] Lots);
public sealed record DispatchCourierDto(LongId UserId, string DisplayName);
public sealed record DispatchCouriersDto(DispatchCourierDto[] Items, bool HasMore);
public sealed record DispatchReviewDto(DispatchPreparationDto Preparation, LongId CourierId, string CourierName, ShipmentLotDto[] Selected, string Review);
public sealed record DispatchResultDto(LongId ShipmentId, string Revision, LongId OrderId, LongId SuborderId, DateTimeOffset RecordedAt,
    LongId NumberEntity, LongId NumberChannel, string NumberYear, string Number);
public sealed record DispatchRecoveryDto(string Status, DispatchResultDto? Result);

public static class ShipmentDispatchEndpoints
{
    public static void MapShipmentDispatchEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/dispatches").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (PreparationPageRequest request, HttpContext http, ShipmentDispatchService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Limit != 25) throw Invalid();
            var page = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new DispatchQueueDto([.. page.Items.Select(row => new DispatchQueueItemDto(row.PreparationId, Number(row.PreparationRevision),
                ShipmentPreparationEndpoints.Source(row.Source), Packages(row.Lines), row.CanDispatch))], page.HasMore));
        }).WithName("ListShipmentDispatchQueue");
        group.MapPost("/{suborderId}/query", async (string suborderId, OrderReadDto request, HttpContext http, ShipmentDispatchService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReadPreparationAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, ct);
            return TypedResults.Ok(Preparation(row));
        }).WithName("ReadShipmentDispatchPreparation");
        group.MapPost("/{suborderId}/couriers/query", async (string suborderId, PreparationPageRequest request, HttpContext http, ShipmentDispatchService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Limit != 25) throw Invalid();
            var page = await service.ListCouriersAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new DispatchCouriersDto([.. page.Items.Select(row => new DispatchCourierDto(row.UserId, row.DisplayName))], page.HasMore));
        }).WithName("ListShipmentDispatchCouriers");
        group.MapPost("/{suborderId}/preview/query", async (string suborderId, DispatchPreviewDto request, HttpContext http, ShipmentDispatchService service, CancellationToken ct) => {
            Purpose(request.Purpose); var (selection, lots, revision) = Selection(request.Selection);
            var row = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, selection.PreparationId.Value, revision,
                selection.CourierId.Value, lots, ct);
            return TypedResults.Ok(new DispatchReviewDto(Preparation(row.Preparation), row.CourierId, row.CourierName,
                [.. row.Selected.Select(lot => new ShipmentLotDto(lot.ReservationId, lot.Packages))], row.Review));
        }).WithName("PreviewShipmentDispatch");
        group.MapPost("/{suborderId}", async (string suborderId, DispatchCommandDto request, HttpContext http, ShipmentDispatchService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var (selection, lots, revision) = Selection(request.Selection); if (AccessRequestMetadata.ExpectedVersion(http.Request) != revision) throw Invalid();
            var row = await service.DispatchAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(suborderId).Value, selection.PreparationId.Value, revision,
                selection.CourierId.Value, lots, request.Review, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["revision"].AsInt64); return TypedResults.Ok(Result(row));
        }).WithName("DispatchShipment");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, ShipmentDispatchService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new DispatchRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverShipmentDispatch");
    }
    private static (DispatchSelectionDto Selection, ImmutableArray<ShipmentLotSelection> Lots, long Revision) Selection(DispatchSelectionDto input) {
        if (input is null || input.Lots is null || input.Lots.Length is < 1 or > 100 || input.Lots.Any(lot => lot is null)) throw Invalid();
        return (input, [.. input.Lots.Select(lot => new ShipmentLotSelection(lot.ReservationId, lot.Packages))], LongId.Parse(input.PreparationRevision).Value);
    }
    private static DispatchPreparationDto Preparation(ShipmentDispatchPreparation row) => new(row.PreparationId, Number(row.PreparationRevision),
        ShipmentPreparationEndpoints.Source(row.Source), Packages(row.Lines), [.. row.Lots.Select(lot => new DispatchReservedLotDto(lot.Reservation.ReservationId,
            lot.Reservation.SkuId, lot.Reservation.DefinitionId, lot.Reservation.LotId, lot.Reservation.RemainingPackages, lot.ExpiresOn))]);
    private static ShipmentPackageDto[] Packages(ImmutableArray<ShipmentPackageSelection> lines) => [.. lines.Select(line => new ShipmentPackageDto(line.SkuId, line.Packages))];
    private static DispatchResultDto Result(BsonDocument row) => new(new(row["shipmentId"].AsInt64), Number(row["revision"].AsInt64), new(row["orderId"].AsInt64),
        new(row["suborderId"].AsInt64), DateTimeOffset.FromUnixTimeMilliseconds(row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch),
        new(row["numberEntity"].AsInt64), new(row["numberChannel"].AsInt64), Number(row["numberYear"].AsInt64), Number(row["number"].AsInt64));
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "shipment-dispatch") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_shipment_dispatch_request", "Confirm the current preparation, reserved lots and assigned courier");
}
