using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Fulfillment;

internal sealed record InvoiceReceiptSource(ResourceScope Resource, BsonDocument Snapshot);
public sealed partial class ShipmentQueryService
{
    internal async Task<ImmutableArray<long>> InvoiceShipmentsAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long? beforeId, CancellationToken ct) {
        if (beforeId is <= 0) throw ShipmentDispatchRecords.Invalid();
        var query = ScopeQuery.Create(Query(actor, authority, Permissions.InvoiceIssue)); if (beforeId.HasValue) query &= F.Lt("_id", beforeId.Value);
        var rows = await Rows("shipments").Find(tx, query).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(ct);
        await FenceAsync(actor, authority, tx, rows.Select(Resource).ToArray(), ct, Permissions.InvoiceIssue);
        return [.. rows.Select(row => row["_id"].AsInt64)];
    }
    internal async Task<BsonDocument> InvoiceCommercialAsync(ActorContext actor, IClientSessionHandle tx, long shipmentId, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? history = null) {
        history ??= orders.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, clock, tx);
        var sourceOrders = orders.WithDispatchHistory(tx, history);
        var shipment = await new ShipmentDispatchRecords(database, clock, history, sourceOrders.StockSourceLiveRead).ReadAsync(tx, shipmentId, ct);
        return await sourceOrders.InvoiceCommercialAsync(tx, actor, shipment, ct);
    }
    // Internal financial provenance only: never returned as an API projection. Billing needs
    // prior allocations across the original order to conserve quantities/tax without granting
    // the current issuer visibility of another store's invoices or its operational workspace.
    internal async Task<ShipmentDocumentSource> InvoiceHistoryAsync(ActorContext actor, IClientSessionHandle tx, long shipmentId, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? history = null) {
        history ??= orders.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, clock, tx);
        var row = await new ShipmentDispatchRecords(database, clock, history, orders.StockSourceLiveRead).ReadAsync(tx, shipmentId, ct);
        return new(Resource(row), await DocumentFactsAsync(actor, tx, row, ct, history));
    }
}
public sealed partial class ShipmentReceiptService
{
    internal async Task RequireInvoiceHistoryAsync(ActorContext actor, IClientSessionHandle tx, InvoiceReceiptSource expected, CancellationToken ct) {
        var history = shipments.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, shipments.StockSourceClock, tx);
        var source = await shipments.InvoiceHistoryAsync(actor, tx, expected.Snapshot["shipment"]["shipmentId"].AsInt64, ct, history);
        var receipt = await records.FindReceiptAsync(tx, source.Snapshot["shipmentId"].AsInt64, ct) ?? throw ShipmentReceiptRecords.Invalid();
        await VerifyReceiptAsync(tx, receipt, source, ct);
        if (!Mongo.Migrations.CatalogSchemas.SemanticEquals(InvoiceReceiptSnapshot(receipt), expected.Snapshot["receipt"]) ||
            !Mongo.Migrations.CatalogSchemas.SemanticEquals(source.Snapshot, expected.Snapshot["shipment"]) ||
            !Mongo.Migrations.CatalogSchemas.SemanticEquals(ShipmentPreparationBson.Scope(source.Resource), ShipmentPreparationBson.Scope(expected.Resource)) ||
            !Mongo.Migrations.CatalogSchemas.SemanticEquals(await shipments.InvoiceCommercialAsync(actor, tx, source.Snapshot["shipmentId"].AsInt64, ct, history), expected.Snapshot["commercial"])) throw ShipmentReceiptRecords.Invalid();
    }
    internal Task InvoiceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal Task<ImmutableArray<long>> InvoiceShipmentsAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long? beforeId, CancellationToken ct) =>
        shipments.InvoiceShipmentsAsync(actor, authority, tx, beforeId, ct);
    internal async Task<InvoiceReceiptSource?> InvoiceSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long shipmentId, string permission, CancellationToken ct) {
        if (permission is not (Permissions.InvoiceRead or Permissions.InvoiceIssue or Permissions.CaseRead or Permissions.CaseRequest or Permissions.CaseManage)) throw ShipmentReceiptRecords.Invalid();
        var history = shipments.StockSourceHistory ?? new ShipmentDispatchValueRecords.HistoricalRead(database, shipments.StockSourceClock, tx);
        var shipment = await shipments.DocumentSourceAsync(actor, authority, tx, shipmentId, permission, ct, history);
        var receipt = await records.FindReceiptAsync(tx, shipmentId, ct);
        if (receipt is null) { await shipments.FenceAsync(actor, authority, tx, [shipment.Resource], ct, permission); return null; }
        await VerifyReceiptAsync(tx, receipt, shipment, ct);
        var snapshot = new BsonDocument { { "shipment", shipment.Snapshot.DeepClone() }, { "receipt", InvoiceReceiptSnapshot(receipt) },
            { "commercial", await shipments.InvoiceCommercialAsync(actor, tx, shipmentId, ct, history) } };
        return new(shipment.Resource, snapshot);
    }
    private static BsonDocument InvoiceReceiptSnapshot(BsonDocument receipt) => new() {
        { "id", receipt["_id"] }, { "recordHash", CatalogRepository.Digest(receipt) }, { "recordedAt", receipt["recordedAt"] },
        { "recipientName", receipt["recipientName"] }, { "documentId", receipt["documentId"] }, { "documentHash", receipt["documentHash"] },
        { "lines", new BsonArray(receipt["lines"].AsBsonArray.Where(line => line["acceptedBaseUnits"].AsInt64 > 0).Select(line => line.DeepClone())) } };
    internal Task InvoiceFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, IEnumerable<ResourceScope> resources, string permission, CancellationToken ct) =>
        shipments.FenceAsync(actor, authority, tx, resources.ToArray(), ct, permission);
}
