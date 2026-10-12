using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal static class ShipmentPreparationBson
{
    internal const string Heads = "shipmentPreparations", Versions = "shipmentPreparationVersions", Operation = "fulfillment.preparation.save";
    internal static BsonArray Lines(ImmutableArray<ShipmentPackageSelection> value) => new(value.Select(line => OrderLineBson.With(new BsonDocument { { "skuId", line.SkuId.Value }, { "packages", line.Packages } }, line.LineRevision)));
    internal static ImmutableArray<ShipmentPackageSelection> Lines(BsonArray value) => ShipmentPreparationPolicy.Copy(value.Select(line =>
        new ShipmentPackageSelection(new(line["skuId"].AsInt64), line["packages"].AsInt32, OrderLineBson.Revision(line))).ToImmutableArray());
    internal static BsonDocument Source(FulfillmentOrderView source) => new() {
        { "orderId", source.OrderId.Value }, { "suborderId", source.SuborderId.Value }, { "revision", source.Revision }, { "review", source.Review },
        { "buyerName", source.BuyerName }, { "delivery", new BsonDocument { { "recipientName", source.Delivery.RecipientName }, { "phone", source.Delivery.Phone },
            { "address", source.Delivery.Address }, { "city", source.Delivery.City }, { "postalCode", Text(source.Delivery.PostalCode) }, { "instructions", Text(source.Delivery.Instructions) } } },
        { "orderedAt", source.OrderedAt.UtcDateTime }, { "canPrepare", source.CanPrepare },
        { "lines", new BsonArray(source.Lines.Select(line => OrderLineBson.With(new BsonDocument { { "skuId", line.SkuId.Value }, { "publicId", line.PublicId.ToString("D") },
            { "skuCode", line.SkuCode }, { "productName", line.ProductName }, { "definition", CatalogRepository.Definition(line.Package) },
            { "orderedPackages", line.OrderedPackages }, { "remainingPackages", line.RemainingPackages } }, line.LineRevision))) } };
    internal static FulfillmentOrderView Source(BsonDocument row) {
        var delivery = row["delivery"].AsBsonDocument;
        var value = new FulfillmentOrderView(new(row["orderId"].AsInt64), new(row["suborderId"].AsInt64), row["revision"].AsInt64, row["review"].AsString,
            row["buyerName"].AsString, new(delivery["recipientName"].AsString, delivery["phone"].AsString, delivery["address"].AsString, delivery["city"].AsString,
                NullText(delivery["postalCode"]), NullText(delivery["instructions"])), row["lines"].AsBsonArray.Select(line => new FulfillmentOrderLine(new(line["skuId"].AsInt64),
                    Guid.ParseExact(line["publicId"].AsString, "D"), line["skuCode"].AsString, line["productName"].AsString, CatalogRepository.Definition(line["definition"].AsBsonDocument),
                    line["orderedPackages"].AsInt32, line["remainingPackages"].AsInt32, OrderLineBson.Revision(line))).ToImmutableArray(), row["canPrepare"].AsBoolean, OrderDraftBson.Time(row["orderedAt"]));
        if (!CatalogSchemas.SemanticEquals(row, Source(value)) || value.Lines.Select(line => (line.SkuId, line.LineRevision)).Distinct().Count() != value.Lines.Length ||
            value.Lines.Any(line => line.Package.SkuId != line.SkuId || line.OrderedPackages is < 1 or > DomainValues.MaximumLineQuantity ||
                line.RemainingPackages < 0 || line.RemainingPackages > line.OrderedPackages)) throw Invalid();
        return value;
    }
    internal static BsonDocument Scope(ResourceScope value) => new() {
        { "buyingOrganizationId", Number(value.BuyingOrganizationId) }, { "branchId", Number(value.BranchId) }, { "divisionId", Number(value.DivisionId) },
        { "fulfillmentOrganizationId", Number(value.FulfillmentOrganizationId) }, { "shopId", Number(value.ShopId) }, { "warehouseId", Number(value.WarehouseId) },
        { "channelId", Number(value.ChannelId) }, { "createdBy", Number(value.CreatedBy) } };
    internal static ResourceScope Scope(BsonDocument row) => new(BuyingOrganizationId: NullNumber(row["buyingOrganizationId"]), BranchId: NullNumber(row["branchId"]),
        DivisionId: NullNumber(row["divisionId"]), FulfillmentOrganizationId: NullNumber(row["fulfillmentOrganizationId"]), ShopId: NullNumber(row["shopId"]),
        WarehouseId: NullNumber(row["warehouseId"]), ChannelId: NullNumber(row["channelId"]), CreatedBy: NullNumber(row["createdBy"]));
    internal static BsonDocument Result(BsonDocument version) => new() { { "preparationId", version["preparationId"] }, { "versionId", version["_id"] },
        { "revision", version["revision"] }, { "orderId", version["source"]["orderId"] }, { "suborderId", version["source"]["suborderId"] }, { "recordedAt", version["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument version) => new() { { "_id", version["auditId"] }, { "schemaVersion", 1 }, { "kind", "fulfillment.preparation.saved" },
        { "actorId", version["preparedBy"] }, { "actorGrantVersion", version["actorGrantVersion"] }, { "operation", Operation }, { "requestKey", version["requestKey"] },
        { "bodyHash", version["bodyHash"] }, { "snapshotHash", CatalogRepository.Digest(version) }, { "reason", version["reason"] }, { "correlationId", version["correlationId"] },
        { "createdAt", version["recordedAt"] }, { "result", Result(version) } };
    private static BsonValue Number(long? value) => value.HasValue ? new BsonInt64(value.Value) : BsonNull.Value;
    private static long? NullNumber(BsonValue value) => value.IsBsonNull ? null : value.AsInt64;
    private static BsonValue Text(string? value) => value is null ? BsonNull.Value : new BsonString(value);
    private static string? NullText(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    internal static AuthorizationDeniedException Invalid() => new("shipment_preparation_unavailable");
}
