using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.PurchaseOrderInboundBindingBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PurchaseOrderInboundBindingSchemas
{
    internal static readonly string[] Collections = [B.Selections, B.Decisions];
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Selections => [new("proposalId", 1)], B.Decisions => [new("proposalId", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1, long.MaxValue - 2) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "proposalId", S.Number(1, long.MaxValue - 2) }, { "proposalHash", PaymentSubmissionSchemas.Hash() },
            { "location", ProcurementRequestSchemas.Location() }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "bodyHash", PaymentSubmissionSchemas.Hash() }, { "auditId", S.Number(1) }, { "requestKey", S.Text(128) },
            { "recordedAt", new BsonDocument("bsonType", "date") }, { "snapshotHash", PaymentSubmissionSchemas.Hash() } };
        var sources = new BsonDocument { { "bsonType", "array" }, { "maxItems", 8 }, { "items", ProcurementStockReceiptSchemas.PoolSource() } };
        var checks = new BsonArray();
        if (name == B.Selections) {
            fields.Add("allocations", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", S.Object(new() {
                { "requestSkuId", S.Number(1) }, { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } },
                { "poolId", S.Number(1, long.MaxValue - 2) }, { "poolRevision", S.Number(1, long.MaxValue - 2) } }) } });
            sources.Add("minItems", 1); fields.Add("receivingSources", sources); checks.Add(Eq("$_id", "$proposalId"));
            var allocations = SafeArray("$allocations");
            checks.Add(Eq(allocations, new BsonDocument("$sortArray", new BsonDocument { { "input", allocations }, { "sortBy", new BsonDocument { { "requestSkuId", 1 }, { "poolId", 1 } } } })));
            checks.Add(Unique(Map(allocations, "allocation", new BsonDocument { { "sku", "$$allocation.requestSkuId" }, { "pool", "$$allocation.poolId" } })));
            checks.Add(new BsonDocument("$setEquals", new BsonArray { Map(allocations, "allocation", "$$allocation.poolId"),
                Map(SafeArray("$receivingSources"), "source", "$$source.poolId") }));
        } else {
            fields.AddRange(new BsonDocument { { "selectionHash", PaymentSubmissionSchemas.Hash() }, { "decision", S.Choice("approve", "reject") },
                { "decisionHash", PaymentSubmissionSchemas.Hash() }, { "orderId", new BsonDocument("anyOf", new BsonArray {
                    S.Number(1, long.MaxValue - 2), new BsonDocument("bsonType", "null") }) }, { "receivingSources", sources } });
            checks.Add(Eq(Eq("$decision", "reject"), Eq("$orderId", BsonNull.Value)));
            checks.Add(Eq(Eq("$decision", "reject"), Eq(new BsonDocument("$size", "$receivingSources"), 0)));
        }
        var poolIds = Map(SafeArray("$receivingSources"), "source", "$$source.poolId");
        checks.Add(Unique(poolIds)); checks.Add(Eq(poolIds, new BsonDocument("$sortArray", new BsonDocument { { "input", poolIds }, { "sortBy", 1 } })));
        checks.Add(new BsonDocument("$allElementsTrue", new BsonArray { Map(SafeArray("$receivingSources"), "source", new BsonDocument("$and", new BsonArray {
            Eq("$$source.requestedLocation", "$location"), Eq("$$source.policyView.policy.poolId", "$$source.poolId"),
            Eq("$$source.policyView.proposal.poolRevision", "$$source.configuration.source.poolRevision") })) }));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)),
            new BsonDocument("$expr", new BsonDocument("$and", checks)), S.DateRange("recordedAt") });
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument SafeArray(string path) => new("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument Unique(BsonValue values) => Eq(new BsonDocument("$size", values), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { values, new BsonArray() })));
}
