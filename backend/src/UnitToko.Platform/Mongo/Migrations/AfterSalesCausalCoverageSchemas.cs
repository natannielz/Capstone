using MongoDB.Bson;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class AfterSalesCausalCoverageSchemas
{
    internal static readonly string[] Collections = [AfterSalesStockSourceRecords.CausalHeads, AfterSalesStockSourceRecords.CausalTransitions];
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", S.Number(1, long.MaxValue - 2) },
            { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == AfterSalesStockSourceRecords.CausalHeads ? "aftersales.causal.head" : "aftersales.causal.transition") } };
        var hash = PaymentSubmissionSchemas.Hash(); var date = new BsonDocument("bsonType", "date");
        BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
        if (name == AfterSalesStockSourceRecords.CausalHeads) {
            fields.Add("location", S.Location()); fields.Add("setupDecisionId", S.Number(1)); fields.Add("revision", S.Number(1, long.MaxValue - 2));
            fields.Add("lastTransitionId", S.Number(1)); fields.Add("lastHash", hash); fields.Add("updatedAt", date); fields.Add("fence", S.Number(0, long.MaxValue - 2));
        } else if (name == AfterSalesStockSourceRecords.CausalTransitions) {
            fields.Add("poolId", S.Number(1)); fields.Add("revision", S.Number(1, long.MaxValue - 2)); fields.Add("previousId", Nullable(S.Number(1)));
            fields.Add("previousHash", Nullable(hash)); fields.Add("sourceKind", S.Choice("setup", "request", "decision")); fields.Add("sourceId", S.Number(1));
            fields.Add("sourceHash", hash); fields.Add("sourceAuditId", S.Number(1)); fields.Add("actorId", S.Number(1)); fields.Add("actorGrantVersion", S.Number(1));
            fields.Add("recordedAt", date); fields.Add("poolRevision", S.Number(1)); fields.Add("beforePoolFence", S.Number(0)); fields.Add("afterPoolFence", S.Number(0));
            fields.Add("caseId", Nullable(S.Number(1))); fields.Add("origin", S.Choice("setup", "retained", "live")); fields.Add("transitionHash", hash);
        } else throw new ArgumentException("Unknown AfterSales causal owner", nameof(name));
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        AfterSalesStockSourceRecords.CausalHeads => [],
        AfterSalesStockSourceRecords.CausalTransitions => [
            ("ux_aftersales_causal_pool_revision", new() { { "poolId", 1 }, { "revision", 1 } }, true),
            ("ux_aftersales_causal_event", new() { { "poolId", 1 }, { "sourceKind", 1 }, { "sourceId", 1 } }, true)],
        _ => throw new ArgumentException("Unknown AfterSales causal owner", nameof(name))
    };
}
