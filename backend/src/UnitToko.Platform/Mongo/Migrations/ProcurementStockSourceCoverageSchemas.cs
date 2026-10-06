using MongoDB.Bson;
using UnitToko.Platform.Procurement;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementStockSourceCoverageSchemas
{
    internal static readonly string[] Collections = [ProcurementStockSourceCoverage.Heads, ProcurementStockSourceCoverage.Transitions];
    private static BsonDocument Number(long minimum = 1) => S.Number(minimum, long.MaxValue - 2);
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Number() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == ProcurementStockSourceCoverage.Heads) {
            fields.Add("setupDecisionId", Number()); fields.Add("location", S.Location()); fields.Add("revision", Number());
            fields.Add("lastTransitionId", Number()); fields.Add("lastHash", Hash()); fields.Add("updatedAt", new BsonDocument("bsonType", "date")); fields.Add("fence", Number(0));
        } else if (name == ProcurementStockSourceCoverage.Transitions) {
            fields.Add("poolId", Number()); fields.Add("revision", Number()); fields.Add("previousId", Nullable(Number())); fields.Add("previousHash", Nullable(Hash()));
            fields.Add("sourceKind", S.Choice(["setup", .. ProcurementStockSourceCoverage.Sources.Select(source => source.Kind)]));
            fields.Add("sourceId", Number()); fields.Add("sourceHash", Hash()); fields.Add("auditId", Number()); fields.Add("location", S.Location());
            fields.Add("recordedAt", new BsonDocument("bsonType", "date"));
            // The private historical configuration is authenticated by the maintained setup/
            // revision/alias owner. Schema shape alone cannot establish source authority.
            fields.Add("configuration", Nullable(StockSourceSuspensionSchemas.Validator()["$and"][0]["$jsonSchema"]["properties"]["configuration"].DeepClone().AsBsonDocument));
            fields.Add("suspensionRevision", Number(0)); fields.Add("poolRevision", Number()); fields.Add("beforePoolFence", Number(0));
            fields.Add("afterPoolFence", Number(0)); fields.Add("transitionHash", Hash());
        } else throw new ArgumentException("Unknown Procurement coverage owner", nameof(name));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange(name == ProcurementStockSourceCoverage.Heads ? "updatedAt" : "recordedAt") });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        ProcurementStockSourceCoverage.Heads => [],
        ProcurementStockSourceCoverage.Transitions => [
            ("ux_procurement_coverage_pool_revision", new BsonDocument { { "poolId", 1 }, { "revision", 1 } }, true),
            ("ux_procurement_coverage_causal_membership", new BsonDocument { { "poolId", 1 }, { "sourceKind", 1 }, { "sourceId", 1 } }, true)],
        _ => throw new ArgumentException("Unknown Procurement coverage owner", nameof(name)) };
}
