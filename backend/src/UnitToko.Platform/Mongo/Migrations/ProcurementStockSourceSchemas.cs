using MongoDB.Bson;
using UnitToko.Platform.Procurement;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementStockSourceSchemas
{
    internal static readonly string[] Collections = [ProcurementStockSourceRecords.Heads, ProcurementStockSourceRecords.Transitions];
    private static BsonDocument Number(long minimum = 1) => S.Number(minimum, long.MaxValue - 2);
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", Number() }, { "schemaVersion", new BsonDocument {
            { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == ProcurementStockSourceRecords.Heads) {
            fields.Add("kind", S.Choice("procurement.heldsource.head")); fields.Add("location", S.Location());
            fields.Add("setupDecisionId", Number()); fields.Add("revision", Number()); fields.Add("lastTransitionId", Number());
            fields.Add("lastHash", Hash()); fields.Add("updatedAt", new BsonDocument("bsonType", "date")); fields.Add("fence", Number(0));
        } else if (name == ProcurementStockSourceRecords.Transitions) {
            fields.Add("kind", S.Choice("procurement.heldsource.transition")); fields.Add("poolId", Number()); fields.Add("revision", Number());
            fields.Add("previousId", Nullable(Number())); fields.Add("previousHash", Nullable(Hash()));
            fields.Add("sourceKind", S.Choice("setup", "heldReceipt")); fields.Add("sourceId", Number()); fields.Add("sourceHash", Hash());
            fields.Add("sourceAuditId", Number()); fields.Add("actorId", Number()); fields.Add("actorGrantVersion", Number());
            fields.Add("recordedAt", new BsonDocument("bsonType", "date")); fields.Add("poolRevision", Number());
            fields.Add("beforePoolFence", Number(0)); fields.Add("afterPoolFence", Number(0));
            fields.Add("receiptId", Nullable(Number())); fields.Add("lotId", Nullable(Number())); fields.Add("balanceId", Nullable(Number()));
            fields.Add("transitionHash", Hash());
        } else throw new ArgumentException("Unknown procurement held-custody producer owner", nameof(name));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange(name == ProcurementStockSourceRecords.Heads ? "updatedAt" : "recordedAt") });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        ProcurementStockSourceRecords.Heads => [],
        ProcurementStockSourceRecords.Transitions => [
            ("ux_procurement_heldsource_pool_revision", new() { { "poolId", 1 }, { "revision", 1 } }, true),
            ("ux_procurement_heldsource_event", new() { { "sourceKind", 1 }, { "sourceId", 1 } }, true)],
        _ => throw new ArgumentException("Unknown procurement held-custody producer owner", nameof(name))
    };
}
