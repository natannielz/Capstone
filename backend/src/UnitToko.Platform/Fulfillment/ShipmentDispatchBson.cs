using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal static class ShipmentDispatchBson
{
    internal const string Collection = "shipments", Operation = "fulfillment.shipment.dispatch";
    internal static BsonArray Lots(ImmutableArray<ShipmentLotSelection> selected) => new(selected.Select(line =>
        new BsonDocument { { "reservationId", line.ReservationId.Value }, { "packages", line.Packages } }));
    internal static ImmutableArray<ShipmentLotSelection> Lots(BsonArray selected) => ShipmentDispatchPolicy.Copy([.. selected.Select(line =>
        new ShipmentLotSelection(new(line["reservationId"].AsInt64), line["packages"].AsInt32))]);
    internal static BsonDocument Admission(ShipmentDispatchAdmission value) => new() {
        { "seller", new BsonDocument { { "version", OrderSubmissionBson.Reference(value.Seller.Version) }, { "name", value.Seller.Name }, { "address", value.Seller.Address } } },
        { "operatingPolicy", OrderSubmissionBson.Reference(value.OperatingPolicy) },
        { "validUntil", value.ValidUntil.UtcDateTime },
        { "lots", new BsonArray(value.Lots.Select(lot => new BsonDocument { { "reservationId", lot.ReservationId },
            { "sourceAuthority", OrderSubmissionBson.Reference(lot.SourceAuthority) }, { "costBasis", OrderSubmissionBson.Reference(lot.CostBasis) },
            { "packageCost", new BsonDecimal128(lot.PackageCost.Value) } })) } };
    internal static ShipmentDispatchAdmission Admission(BsonDocument value) => new(new(OrderSubmissionBson.Reference(value["seller"]["version"]),
        value["seller"]["name"].AsString, value["seller"]["address"].AsString), OrderSubmissionBson.Reference(value["operatingPolicy"]),
        [.. value["lots"].AsBsonArray.Select(lot => new ShipmentLotAuthority(lot["reservationId"].AsInt64, OrderSubmissionBson.Reference(lot["sourceAuthority"]),
            OrderSubmissionBson.Reference(lot["costBasis"]), new Money(Decimal128.ToDecimal(lot["packageCost"].AsDecimal128))))], OrderDraftBson.Time(value["validUntil"]));
    internal static object Body(long preparationId, long preparationRevision, long courierId, string review, ImmutableArray<ShipmentLotSelection> selected) =>
        new { preparationId, preparationRevision, courierId, review, selected };
    internal static string Review(BsonDocument preparation, long courierId, long courierGrantVersion, long courierProfileVersion,
        string courierName, BsonArray selected, BsonArray stockEffects, BsonDocument admission) => CatalogRepository.Digest(new BsonDocument {
            { "preparationVersionId", preparation["_id"] }, { "source", preparation["source"] }, { "lines", preparation["lines"] },
            { "courierId", courierId }, { "courierGrantVersion", courierGrantVersion }, { "courierProfileVersion", courierProfileVersion }, { "courierName", courierName },
            { "pickedLots", selected }, { "stockEffects", stockEffects }, { "admission", admission } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "shipmentId", row["_id"] }, { "revision", row["revision"] },
        { "orderId", row["orderId"] }, { "suborderId", row["suborderId"] }, { "recordedAt", row["recordedAt"] },
        { "numberEntity", row["numberEntity"] }, { "numberChannel", row["numberChannel"] }, { "numberYear", row["numberYear"] }, { "number", row["number"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "fulfillment.shipment.dispatched" },
        { "operation", Operation }, { "actorId", row["dispatchedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", CatalogRepository.Digest(row) },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
}
