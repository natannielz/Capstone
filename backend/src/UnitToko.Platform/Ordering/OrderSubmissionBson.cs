using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Ordering;

internal static class OrderSubmissionBson
{
    internal const string Orders = "orders", Suborders = "orderSuborders", Reservations = "reservations", Operation = "ordering.order.submit";
    internal static BsonDocument Reference(OrderPolicyReference value) => new() { { "id", value.Id }, { "revision", value.Revision } };
    internal static OrderPolicyReference Reference(BsonValue value) => new(value["id"].AsInt64, value["revision"].AsInt64);
    internal static BsonDocument Terms(OrderCommercialTerms value) => new() {
        { "policy", Reference(value.Policy) }, { "taxPolicy", Reference(value.TaxPolicy) }, { "creditDecision", Reference(value.CreditDecision) },
        { "spendingPolicy", Reference(value.SpendingPolicy) }, { "spending", new BsonDocument { { "mode", value.Spending.Mode.ToString() },
            { "threshold", value.Spending.Threshold.HasValue ? new BsonDecimal128(value.Spending.Threshold.Value.Value) : BsonNull.Value } } },
        { "buyer", new BsonDocument { { "version", Reference(value.Buyer.Version) }, { "name", value.Buyer.Name }, { "address", value.Buyer.Address } } },
        { "deliveryVersion", Reference(value.Delivery.Version) }, { "tax", new BsonDecimal128(value.Tax.Value) },
        { "charges", new BsonDecimal128(value.Charges.Value) }, { "total", new BsonDecimal128(value.Total.Value) } };
    internal static BsonDocument Quote(OrderCatalogQuote value) => new() {
        { "currency", value.Currency }, { "catalogSubtotal", new BsonDecimal128(value.CatalogSubtotal.Value) },
        { "lines", new BsonArray(value.Lines.Select(line => OrderLineBson.With(new BsonDocument {
            { "familyId", line.FamilyId.Value }, { "familyName", line.FamilyName }, { "publicId", line.PublicId.ToString("D") },
            { "skuCode", line.SkuCode }, { "owner", CatalogRepository.Owner(line.FulfillmentOwner) },
            { "definition", CatalogRepository.Definition(line.Definition) }, { "price", CatalogPriceBson.Fact(line.Price) },
            { "packages", line.Packages }, { "baseUnits", line.BaseUnits }, { "subtotal", new BsonDecimal128(line.CatalogSubtotal.Value) } }, line.LineRevision))) } };
    internal static OrderCatalogQuote Quote(BsonDocument value) => new(value["lines"].AsBsonArray.Select(line => new OrderQuotedLine(
        new(line["familyId"].AsInt64), line["familyName"].AsString, Guid.Parse(line["publicId"].AsString), line["skuCode"].AsString,
        CatalogRepository.Owner(line["owner"].AsBsonDocument), CatalogRepository.Definition(line["definition"].AsBsonDocument),
        CatalogPriceBson.Price(line["price"].AsBsonDocument, true), line["packages"].AsInt32, line["baseUnits"].AsInt64,
        new(Decimal128.ToDecimal(line["subtotal"].AsDecimal128)), OrderLineBson.Revision(line))).ToImmutableArray(), value["currency"].AsString,
        new(Decimal128.ToDecimal(value["catalogSubtotal"].AsDecimal128)));
    internal static BsonDocument Allocation(OrderStockAllocation value) => OrderLineBson.With(new() {
        { "skuId", value.SkuId }, { "authority", Reference(value.SourceAuthority) }, { "poolId", value.Pool.Id }, { "poolRevision", value.Pool.Revision },
        { "balanceId", value.Change.Id }, { "balanceRevision", value.Change.ExpectedRevision }, { "definitionId", value.Change.Key.DefinitionId.Value },
        { "lotId", value.Change.Key.LotId.Value }, { "packages", value.Change.Packages }, { "location", new BsonDocument {
            { "organizationId", value.Pool.Location.OrganizationId.Value }, { "shopId", value.Pool.Location.ShopId.Value },
            { "warehouseId", value.Pool.Location.WarehouseId.Value }, { "channelId", value.Pool.Location.ChannelId.Value } } } }, value.LineRevision);
    internal static OrderStockAllocation Allocation(BsonDocument value) {
        var location = InventoryGraph.Location(value["location"].AsBsonDocument); var poolId = value["poolId"].AsInt64;
        return new(value["skuId"].AsInt64, Reference(value["authority"]), new(poolId, location, value["poolRevision"].AsInt64),
            new(value["balanceId"].AsInt64, poolId, new(location, new(value["skuId"].AsInt64), new(value["definitionId"].AsInt64), new(value["lotId"].AsInt64)),
                value["balanceRevision"].AsInt64, InventoryBalanceAction.Reserve, value["packages"].AsInt64), OrderLineBson.Revision(value));
    }
    // The review binds accepted commercial facts, party/address versions and explicit source owner.
    // Lot balance revisions are server concurrency guards; changing a lot cannot alter a reviewed SKU/owner/price.
    internal static string Review(OrderDraftInput input, OrderCatalogQuote quote, OrderCommercialTerms terms) =>
        CatalogRepository.Digest(new BsonDocument { { "input", OrderDraftBson.Input(input) }, { "quote", Quote(quote) }, { "terms", Terms(terms) } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "orderId", row["_id"] }, { "revision", row["revision"] },
        { "suborderIds", row["suborderIds"].DeepClone() }, { "recordedAt", row["orderedAt"] }, { "spendingState", row["initialSpendingState"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "ordering.order.submitted" },
        { "actorId", row["createdBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "operation", Operation }, { "requestKey", row["requestKey"] },
        { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["orderedAt"] }, { "context", row["input"]["context"].DeepClone() }, { "result", Result(row) } };
    internal static string SnapshotHash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not ("snapshotHash" or "fence"))));
}
