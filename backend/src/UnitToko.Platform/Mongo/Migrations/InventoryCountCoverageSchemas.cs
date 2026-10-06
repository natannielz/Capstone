using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class InventoryCountCoverageSchemas
{
    internal static readonly string[] Collections = [InventoryCountCoverage.Heads, InventoryCountCoverage.Members];
    internal static BsonDocument Validator(string name)
    {
        var id = S.Number(1, long.MaxValue - 2); var hash = PaymentSubmissionSchemas.Hash();
        var fields = new BsonDocument { { "_id", id },
            { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == InventoryCountCoverage.Heads ? "inventory.countcoverage.head" : "inventory.countcoverage.member") } };
        BsonDocument Equal(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
        var expressions = new BsonArray();
        if (name == InventoryCountCoverage.Heads) {
            fields.Add("writerSet", S.Choice("stock-count-v101")); fields.Add("memberCount", S.Number(0, long.MaxValue - 3));
            fields.Add("revision", id); fields.Add("membershipHash", hash); fields.Add("fence", S.Number(0, long.MaxValue - 2));
            expressions.Add(Equal("$revision", new BsonDocument("$add", new BsonArray { "$memberCount", 1L })));
        } else if (name == InventoryCountCoverage.Members) {
            fields.Add("observationId", id); fields.Add("organizationId", id);
            fields.Add("location", StockCountSchemas.Validator(StockCountBson.Collection)["$and"][0]["$jsonSchema"]["properties"]["location"].DeepClone());
            fields.Add("poolId", new BsonDocument("anyOf", new BsonArray { id, new BsonDocument("bsonType", "null") }));
            fields.Add("origin", S.Choice("retained", "live")); fields.Add("observationHash", hash); fields.Add("observationFingerprint", hash);
            fields.Add("observationBodyHash", hash); fields.Add("observationAuditId", id); fields.Add("memberHash", hash);
            expressions.Add(Equal("$_id", "$observationId")); expressions.Add(Equal("$organizationId", "$location.organizationId"));
        } else throw new ArgumentException("Unknown inventory count coverage owner", nameof(name));
        var names = new BsonDocument("$map", new BsonDocument { { "input", new BsonDocument("$objectToArray", "$$ROOT") }, { "as", "field" }, { "in", "$$field.k" } });
        expressions.Add(Equal(new BsonDocument("$size", names), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { names, new BsonArray() }))));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", expressions)) });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        InventoryCountCoverage.Heads => [],
        InventoryCountCoverage.Members => [
            ("ux_inventory_count_coverage_observation", new("observationId", 1), true),
            ("ix_inventory_count_coverage_organization", new() { { "organizationId", 1 }, { "_id", 1 } }, false),
            ("ix_inventory_count_coverage_pool", new() { { "poolId", 1 }, { "_id", 1 } }, false)],
        _ => throw new ArgumentException("Unknown inventory count coverage owner", nameof(name))
    };
}
