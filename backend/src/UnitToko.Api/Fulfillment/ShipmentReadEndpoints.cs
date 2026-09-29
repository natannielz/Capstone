using System.Globalization;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;

public sealed record ShipmentReadLineDto(LongId SkuId, string SkuCode, string ProductName, CatalogDefinitionDto Definition, int Packages, string BaseUnits);
public sealed record ShipmentReadDto(LongId Id, string Revision, LongId OrderId, LongId SuborderId, LongId NumberEntity, LongId NumberChannel,
    string NumberYear, string Number, DateTimeOffset DispatchedAt, string BuyerName, OrderDeliveryDto Delivery, LongId CourierId, string CourierName, ShipmentReadLineDto[] Lines);
public sealed record ShipmentReadPageDto(ShipmentReadDto[] Items, bool HasMore);

public static class ShipmentReadEndpoints
{
    public static void MapShipmentReadEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/shipments").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (PreparationPageRequest request, HttpContext http, ShipmentQueryService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Limit != 25) throw Invalid();
            var page = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ShipmentReadPageDto([.. page.Items.Select(View)], page.HasMore));
        }).WithName("ListShipments");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, ShipmentQueryService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadShipment");
    }
    private static ShipmentReadDto View(ShipmentReadView row) => new(row.Id, Number(row.Revision), row.OrderId, row.SuborderId, row.NumberEntity, row.NumberChannel,
        Number(row.NumberYear), Number(row.Number), row.DispatchedAt, row.BuyerName, new(row.Delivery.RecipientName, row.Delivery.Phone, row.Delivery.Address,
            row.Delivery.City, row.Delivery.PostalCode, row.Delivery.Instructions), row.CourierId, row.CourierName,
        [.. row.Lines.Select(line => new ShipmentReadLineDto(line.SkuId, line.SkuCode, line.ProductName, CatalogResponses.From(line.Definition), line.Packages, Number(line.BaseUnits)))]);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Purpose(string value) { if (value != "shipment-read") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_shipment_read_request", "Use the current scoped shipment list or detail");
}
