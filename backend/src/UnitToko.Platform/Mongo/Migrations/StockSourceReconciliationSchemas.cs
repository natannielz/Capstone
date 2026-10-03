using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class StockSourceReconciliationSchemas
{
    internal static readonly string[] Collections = [B.Proposals, B.Decisions];
    private static BsonDocument Id(long min = 1) => S.Number(min, long.MaxValue - 1);
    private static BsonDocument Qty() => S.Number(0, long.MaxValue);
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Optional(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Rows(BsonDocument item, int minimum = 0) => new() {
        { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", 1000 }, { "items", S.Object(item) } };
    private static BsonDocument Observation() => S.Object(new() {
        { "observedAt", Date() }, { "sourceReference", S.Text(160) },
        { "entries", Rows(new() { { "balanceId", Id() }, { "packages", Qty() } }) } });
    private static BsonDocument BaselineBalance() => S.Object(new() {
        { "balanceId", Id() }, { "receiptId", Id() }, { "lotId", Id() }, { "location", S.Location() },
        { "skuId", Id() }, { "skuCode", S.Text(160) }, { "definitionId", Id() }, { "definitionVersion", Id() },
        { "packageLabel", S.Text(160) }, { "packageKind", S.Text(160) }, { "baseUnitCode", S.Text(160) },
        { "baseUnitsPerPackage", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } } },
        { "lotReference", S.Text(160) }, { "expiresOn", Optional(new BsonDocument("bsonType", "string")) },
        { "onHand", Qty() }, { "reserved", Qty() }, { "held", Qty() }, { "expired", Qty() }, { "version", Id() } });
    internal static BsonDocument Baseline() => S.Object(new() {
        { "poolId", Id() }, { "poolRevision", Id() }, { "poolFence", Id(0) }, { "poolLabel", S.Text(160) },
        { "physicalKey", S.Text(64) }, { "ownerLocation", S.Location() }, { "policyId", Id() },
        { "policyRevision", Id() }, { "reconciliationProcedureReference", S.Text(160) },
        { "configurationHash", Hash() }, { "maximumAgeMilliseconds", Id() }, { "latestSourceAt", Date() },
        { "balances", Rows(new() { { "balanceId", Id() }, { "receiptId", Id() }, { "lotId", Id() }, { "location", S.Location() },
            { "skuId", Id() }, { "skuCode", S.Text(160) }, { "definitionId", Id() }, { "definitionVersion", Id() },
            { "packageLabel", S.Text(160) }, { "packageKind", S.Text(160) }, { "baseUnitCode", S.Text(160) },
            { "baseUnitsPerPackage", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } } },
            { "lotReference", S.Text(160) }, { "expiresOn", Optional(new BsonDocument("bsonType", "string")) },
            { "onHand", Qty() }, { "reserved", Qty() }, { "held", Qty() }, { "expired", Qty() }, { "version", Id() } }) },
        { "capturedAt", Date() }, { "baselineHash", Hash() }, { "binding", Hash() } });
    internal static BsonDocument[] Keys(string collection) => collection switch {
        B.Proposals => [new BsonDocument("_id", 1), new BsonDocument { { "actorId", 1 }, { "requestKey", 1 } },
            new BsonDocument("auditId", 1) ],
        B.Decisions => [new BsonDocument("proposalId", 1), new BsonDocument { { "actorId", 1 }, { "requestKey", 1 } },
            new BsonDocument("auditId", 1) ],
        _ => throw B.Unavailable() };
    internal static BsonDocument Validator(string collection)
    {
        var fields = new BsonDocument { { "_id", Id() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(collection == B.Proposals ? "stock.source.reconciliation.proposal" : "stock.source.reconciliation.decision") },
            { "poolId", Id() }, { "revision", S.Number(1, 1) }, { "binding", Hash() }, { "actorId", Id() }, { "actorGrantVersion", Id() },
            { "recordedAt", Date() }, { "auditId", Id() }, { "reason", S.Text(1000) }, { "correlationId", S.Text(128) },
            { "requestKey", S.Text(128) }, { "bodyHash", Hash() }, { "snapshotHash", Hash() } };
        if (collection == B.Proposals)
        {
            fields.Add("baseline", Baseline()); fields.Add("observation", Observation());
            fields.Add("differences", Rows(new() { { "balanceId", Id() }, { "baselinePackages", Qty() }, { "observedPackages", Qty() } }));
            fields.Add("committedPoolFence", Id());
        }
        else if (collection == B.Decisions)
        {
            fields.Add("proposalId", Id()); fields.Add("proposalHash", Hash());
            fields.Add("decision", S.Choice("confirm", "reject")); fields.Add("observation", Optional(Observation()));
            fields.Add("baselineHash", Optional(Hash()));
        }
        else throw B.Unavailable();
        return new BsonDocument("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$poolId", collection == B.Proposals ? "$baseline.poolId" : "$poolId" })) });
    }
}
