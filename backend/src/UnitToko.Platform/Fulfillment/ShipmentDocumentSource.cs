using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal sealed record ShipmentDocumentSource(ResourceScope Resource, BsonDocument Snapshot);

public sealed partial class ShipmentQueryService
{
    // Only the fulfillment owner calls this, inside the caller's transaction. Every fact comes
    // from the accepted order/dispatch, not current address/catalog masters or client input.
    internal async Task<ShipmentDocumentSource> DocumentSourceAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long shipmentId, string permission, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? history = null) {
        if (permission is not (Permissions.ShipmentRead or Permissions.ShipmentPrepare or Permissions.DeliveryAttempt or Permissions.DeliveryEvidence or Permissions.ReceiptAccept or Permissions.InvoiceRead or Permissions.InvoiceIssue or Permissions.CaseRead or Permissions.CaseRequest or Permissions.CaseManage) || shipmentId <= 0) throw ShipmentDispatchRecords.Invalid();
        var scoped = await Rows("shipments").Find(tx, F.Eq("_id", shipmentId) & ScopeQuery.Create(Query(actor, snapshot, permission))).SingleOrDefaultAsync(ct)
            ?? throw ShipmentDispatchRecords.Invalid();
        history ??= orders.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, clock, tx);
        var row = await new ShipmentDispatchRecords(database, clock, history, orders.StockSourceLiveRead).ReadAsync(tx, shipmentId, ct);
        return new(Resource(scoped), await DocumentFactsAsync(actor, tx, row, ct, history));
    }
    private async Task<BsonDocument> DocumentFactsAsync(ActorContext actor, IClientSessionHandle tx, BsonDocument row, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? history = null) {
        history ??= orders.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, clock, tx);
        var sourceOrders = orders.WithDispatchHistory(tx, history);
        var shipmentId = row["_id"].AsInt64;
        var buyer = await sourceOrders.RequireShipmentHistoryAsync(tx, actor, row, ct);
        var source = ShipmentPreparationBson.Source(row["source"].AsBsonDocument);
        var facts = new BsonDocument { { "shipmentId", shipmentId }, { "shipmentRevision", row["revision"] }, { "dispatchHash", CatalogRepository.Digest(row) },
            { "orderId", row["orderId"] }, { "suborderId", row["suborderId"] }, { "numberEntity", row["numberEntity"] }, { "numberChannel", row["numberChannel"] },
            { "numberYear", row["numberYear"] }, { "number", row["number"] }, { "dispatchedAt", row["recordedAt"] },
            { "seller", row["admission"]["seller"].DeepClone() }, { "buyer", buyer }, { "delivery", row["source"]["delivery"].DeepClone() },
            { "courierId", row["courierId"] }, { "courierName", row["courierName"] },
            { "lines", new BsonArray(ShipmentPreparationBson.Lines(row["lines"].AsBsonArray).Select(selected => {
                var line = source.Lines.Single(line => line.SkuId == selected.SkuId && line.LineRevision == selected.LineRevision);
                return OrderLineBson.With(new BsonDocument { { "skuId", line.SkuId.Value }, { "skuCode", line.SkuCode }, { "productName", line.ProductName },
                    { "definition", CatalogRepository.Definition(line.Package) }, { "packages", selected.Packages },
                    { "baseUnits", checked((long)selected.Packages * line.Package.BaseUnitsPerPackage) } }, line.LineRevision);
            })) } };
        return facts;
    }
}
