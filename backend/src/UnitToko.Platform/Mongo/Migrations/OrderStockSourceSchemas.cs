using MongoDB.Bson;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Ordering producer state only. These records grant no activation or stock release.</summary>
internal static class OrderStockSourceSchemas
{
    internal static readonly string[] Collections = [OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions];
    private static BsonDocument Number(long min = 1) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument State() => S.Object(new() { { "revision", Number() }, { "remaining", Number(0) },
        { "released", Number(0) }, { "dispatched", Number(0) } });
    private static BsonDocument Effect() => S.Object(new() { { "reservationId", Number() }, { "orderId", Number() },
        { "suborderId", Number() }, { "allocation", OrderSubmissionSchemas.Allocation() },
        { "before", Nullable(State()) }, { "after", State() } });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", Number() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", new BsonDocument("enum", new BsonArray { name == OrderStockSourceRecords.Heads ? "ordering.stocksource.head" : "ordering.stocksource.transition" }) } };
        if (name == OrderStockSourceRecords.Heads) {
            fields.Add("location", StockSourceSetupSchemas.Location()); fields.Add("setupDecisionId", Number());
            fields.Add("revision", Number()); fields.Add("lastTransitionId", Number()); fields.Add("lastHash", Hash());
            fields.Add("updatedAt", Date()); fields.Add("fence", Number(0));
        } else if (name == OrderStockSourceRecords.Transitions) {
            fields.Add("poolId", Number()); fields.Add("revision", Number()); fields.Add("previousId", Nullable(Number()));
            fields.Add("previousHash", Nullable(Hash())); fields.Add("sourceKind", new BsonDocument("enum", new BsonArray { "setup", "submission", "decision", "shipment" }));
            fields.Add("sourceId", Number()); fields.Add("sourceHash", Hash()); fields.Add("sourceAuditId", Number());
            fields.Add("actorId", Number()); fields.Add("actorGrantVersion", Number()); fields.Add("recordedAt", Date());
            fields.Add("effects", new BsonDocument { { "bsonType", "array" }, { "minItems", 0 }, { "maxItems", 100 }, { "items", Effect() } });
            fields.Add("transitionHash", Hash());
        } else throw new ArgumentException("Unknown Ordering source owner", nameof(name));
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        OrderStockSourceRecords.Heads => [],
        OrderStockSourceRecords.Transitions => [
            ("ux_ordering_source_pool_revision", new() { { "poolId", 1 }, { "revision", 1 } }, true),
            ("ux_ordering_source_event", new() { { "poolId", 1 }, { "sourceKind", 1 }, { "sourceId", 1 } }, true)],
        _ => throw new ArgumentException("Unknown Ordering source owner", nameof(name))
    };
}
