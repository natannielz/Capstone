using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ReplenishmentSchemas
{
    private static BsonDocument Nullable(BsonDocument value) { value["bsonType"] = new BsonArray { value["bsonType"], "null" }; return value; }
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Boolean => new("bsonType", "bool");
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } };
    private static BsonDocument Array(BsonDocument item, int maximum) => new() { { "bsonType", "array" }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Scope(bool warehouse) => S.Object(new() {
        { "kind", warehouse ? S.Choice("fulfillmentWarehouse") : S.Choice("fulfillmentShop", "fulfillmentWarehouse") },
        { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "warehouseId", warehouse ? S.Number(1) : Nullable(S.Number(1)) }, { "channelId", S.Number(1) } });
    internal static BsonDocument Validator()
    {
        var inputs = S.Object(new() { { "dailyDemandPerDay", Nullable(new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) } }) },
            { "leadTimeDays", Nullable(new() { { "bsonType", "int" }, { "minimum", 0 } }) }, { "safetyStockPackages", Nullable(S.Number(0)) },
            { "reorderPointPackages", Nullable(S.Number(0)) }, { "purchaseMinimumPackages", S.Number(1) }, { "purchaseIncrementPackages", S.Number(1) } });
        var definition = S.Object(new() { { "owner", Scope(false) }, { "skuId", S.Number(1) }, { "skuRevision", S.Number(1) },
            { "familyId", S.Number(1) }, { "familyRevision", S.Number(1) }, { "categoryId", S.Number(1) }, { "categoryRevision", S.Number(1) },
            { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) }, { "skuCode", S.Text(64) }, { "physicalItemKey", S.Text(128) },
            { "packageLabel", S.Text(160) }, { "packageKind", S.Choice("loose", "sealed", "other") }, { "baseUnitCode", S.Text(64) },
            { "baseUnitsPerPackage", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } } } });
        var advice = S.Object(new() { { "ruleVersion", S.Choice(UnitToko.Modules.Inventory.ReplenishmentPolicy.Version) }, { "observedAt", Date },
            { "horizon", Nullable(Date) }, { "availableAfterCommitments", Nullable(S.Number(long.MinValue)) }, { "thresholdAlert", Nullable(Boolean) },
            { "eligibleSupplyPackages", Nullable(S.Number(0)) }, { "inventoryPosition", Nullable(S.Number(long.MinValue)) },
            { "requiredStockPackages", Nullable(S.Number(0)) }, { "suggestedPurchasePackages", Nullable(S.Number(0)) },
            { "warnings", Array(S.Text(100), 64) }, { "supplyDecisions", Array(S.Object(new() { { "reference", S.Text(160) }, { "state", S.Text(80) } }), 1000) } });
        return new("$jsonSchema", S.Object(new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(ReplenishmentRecords.Operation) }, { "input", S.Object(new() { { "location", Scope(true) }, { "poolId", S.Number(1) },
                { "expectedPoolRevision", S.Number(1) }, { "skuId", S.Number(1) }, { "expectedSkuRevision", S.Number(1) }, { "definitionId", S.Number(1) },
                { "inputs", inputs }, { "inputReference", S.Text(500) } }) }, { "definition", definition },
            { "stock", S.Object(new() { { "currentAuthority", Boolean }, { "usableBeforeReservations", Nullable(S.Number(0)) },
                { "reservedPackages", Nullable(S.Number(0)) }, { "additionalUnreservedCommitments", Nullable(S.Number(0)) } }) },
            { "supplyCoverageComplete", Boolean }, { "supplies", Array(S.Object(new() { { "reference", S.Text(160) }, { "packages", S.Number(1) },
                { "expectedAt", Nullable(Date) }, { "verified", Boolean } }), 1000) }, { "sourceWarnings", Array(S.Text(100), 16) },
            // Producer snapshots retain their own versioned shapes and are authenticated by their owning readers.
            { "provenance", S.Object(new() { { "stock", new BsonDocument("bsonType", "object") }, { "supply", new BsonDocument("bsonType", "object") } }) },
            { "sourceHash", Hash }, { "advice", advice }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "issuer", S.Text(2048) }, { "subject", S.Text(256) }, { "recordedAt", Date }, { "requestKey", S.Text(128) },
            { "bodyHash", Hash }, { "reason", S.Text(1000) }, { "correlationId", S.Text(128) }, { "auditId", S.Number(1) } }));
    }
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] Keys = [
        ("ux_replenishment_actor_request", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
        ("ix_replenishment_location_sku", new() { { "input.location", 1 }, { "input.skuId", 1 }, { "_id", -1 } }, false) ];
}
