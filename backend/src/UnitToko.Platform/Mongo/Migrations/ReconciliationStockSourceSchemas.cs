using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ReconciliationStockSourceSchemas
{
    internal static readonly string[] Collections = [ReconciliationStockSourceRecords.Heads, ReconciliationStockSourceRecords.Transitions];
    private static BsonDocument Number(long minimum = 1) => S.Number(minimum, long.MaxValue - 2);
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", Number() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == ReconciliationStockSourceRecords.Heads ? "reconciliation.stocksource.head" : "reconciliation.stocksource.transition") } };
        if (name == ReconciliationStockSourceRecords.Heads) {
            fields.Add("location", S.Location()); fields.Add("setupDecisionId", Number()); fields.Add("revision", Number());
            fields.Add("lastTransitionId", Number()); fields.Add("lastHash", Hash()); fields.Add("updatedAt", Date()); fields.Add("fence", Number(0));
        } else if (name == ReconciliationStockSourceRecords.Transitions) {
            fields.Add("poolId", Number()); fields.Add("revision", Number()); fields.Add("previousId", Nullable(Number())); fields.Add("previousHash", Nullable(Hash()));
            fields.Add("sourceKind", S.Choice("setup", "proposal", "decision")); fields.Add("sourceId", Number()); fields.Add("sourceHash", Hash());
            fields.Add("sourceAuditId", Number()); fields.Add("actorId", Number()); fields.Add("actorGrantVersion", Number()); fields.Add("recordedAt", Date());
            fields.Add("poolRevision", S.Number(1, long.MaxValue)); fields.Add("beforePoolFence", S.Number(0, long.MaxValue)); fields.Add("afterPoolFence", S.Number(0, long.MaxValue));
            fields.Add("proposalId", Nullable(Number())); fields.Add("beforeOpen", Nullable(new BsonDocument("bsonType", "bool")));
            fields.Add("afterOpen", Nullable(new BsonDocument("bsonType", "bool"))); fields.Add("transitionHash", Hash());
        } else throw new ArgumentException("Unknown reconciliation producer owner", nameof(name));
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        ReconciliationStockSourceRecords.Heads => [],
        ReconciliationStockSourceRecords.Transitions => [
            ("ux_reconciliation_source_pool_revision", new() { { "poolId", 1 }, { "revision", 1 } }, true),
            ("ux_reconciliation_source_event", new() { { "poolId", 1 }, { "sourceKind", 1 }, { "sourceId", 1 } }, true)],
        _ => throw new ArgumentException("Unknown reconciliation producer owner", nameof(name))
    };
}
