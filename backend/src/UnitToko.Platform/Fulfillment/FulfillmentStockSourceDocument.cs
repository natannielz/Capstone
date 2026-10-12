using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentQueryService
{
    // Only the owning producer calls this; the caller owns current disclosure authority.
    // Reuses the genuine accepted dispatch and complete original Ordering graph without an actor.
    internal async Task<ShipmentDocumentSource> FulfillmentStockSourceDocumentAsync(IClientSessionHandle tx,
        long shipmentId, CancellationToken ct)
    {
        var row = await new ShipmentDispatchRecords(database, clock, orders.StockSourceHistory, orders.StockSourceLiveRead).ReadAsync(tx, shipmentId, ct);
        var order = await orders.ReadStockSourceGraphAsync(tx, row["orderId"].AsInt64, ct);
        if (!order["suborderIds"].AsBsonArray.Contains(row["suborderId"])) throw ShipmentDispatchRecords.Invalid();
        var source = ShipmentPreparationBson.Source(row["source"].AsBsonDocument);
        var facts = new BsonDocument { { "shipmentId", shipmentId }, { "shipmentRevision", row["revision"] }, { "dispatchHash", CatalogRepository.Digest(row) },
            { "orderId", row["orderId"] }, { "suborderId", row["suborderId"] }, { "numberEntity", row["numberEntity"] }, { "numberChannel", row["numberChannel"] },
            { "numberYear", row["numberYear"] }, { "number", row["number"] }, { "dispatchedAt", row["recordedAt"] },
            { "seller", row["admission"]["seller"].DeepClone() }, { "buyer", order["terms"]["buyer"].DeepClone() },
            { "delivery", row["source"]["delivery"].DeepClone() }, { "courierId", row["courierId"] }, { "courierName", row["courierName"] },
            { "lines", new BsonArray(ShipmentPreparationBson.Lines(row["lines"].AsBsonArray).Select(selected => {
                var line = source.Lines.Single(line => line.SkuId == selected.SkuId && line.LineRevision == selected.LineRevision);
                return OrderLineBson.With(new BsonDocument { { "skuId", line.SkuId.Value }, { "skuCode", line.SkuCode }, { "productName", line.ProductName },
                    { "definition", CatalogRepository.Definition(line.Package) }, { "packages", selected.Packages },
                    { "baseUnits", checked((long)selected.Packages * line.Package.BaseUnitsPerPackage) } }, line.LineRevision);
            })) } };
        return new(Resource(row), facts);
    }
}
