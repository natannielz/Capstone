using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Inventory;

internal static class StockReleaseSchemas
{
    internal static readonly string[] Collections = [StockReleaseBson.Proposals, StockReleaseBson.Decisions];
    private static BsonDocument Number(long min = 1) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Digest() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Number() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == StockReleaseBson.Proposals ? "stock.release.proposal" : "stock.release.decision") },
            { "input", S.Object(new() { { "location", S.Location() }, { "receiptId", Number() }, { "qualityAllocationId", Number() },
                { "valuationAllocationId", Number() }, { "packages", Number() }, { "policy", S.Choice(LotValuePolicy.ProportionalFloorPolicy) } }) },
            { "expected", S.Object(new() { { "balanceRevision", Number() }, { "poolRevision", Number() }, { "poolFence", Number(0) },
                { "heldValueRevision", Number(0) }, { "lotValueRevision", Number(0) } }) },
            { "source", new BsonDocument("bsonType", "object") }, { "before", new BsonDocument("bsonType", "object") },
            { "after", new BsonDocument("bsonType", "object") }, { "binding", Digest() },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 2 }, { "items", new BsonDocument("bsonType", "object") } } },
            { "actorId", Number() }, { "actorGrantVersion", Number() }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "requestKey", S.Text(128) }, { "bodyHash", Digest() }, { "reason", S.Text(1000) }, { "correlationId", S.Text(128) },
            { "auditId", Number() }, { "snapshotHash", Digest() } };
        if (name == StockReleaseBson.Decisions) {
            fields.Add("proposalId", Number()); fields.Add("proposalHash", Digest()); fields.Add("decision", S.Choice("approve", "reject"));
            fields.Add("phase", S.Choice("preparing", "committed")); fields.Add("causeHash", Digest());
            fields.Add("movementId", Nullable(Number())); fields.Add("valueTransitionId", Nullable(Number())); fields.Add("valueTransitionHash", Nullable(Digest()));
            fields.Add("transferredValue", Nullable(new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?:0|[1-9][0-9]*)\.[0-9]{2}\z" } }));
        } else if (name != StockReleaseBson.Proposals) throw new ArgumentException("Unknown held-release owner", nameof(name));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt") });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        StockReleaseBson.Proposals => [("ux_stock_release_proposer_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        StockReleaseBson.Decisions => [("ux_stock_release_proposal_decision", new("proposalId", 1), true),
            ("ux_stock_release_reviewer_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        _ => throw new ArgumentException("Unknown held-release owner", nameof(name)) };
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw StockReleaseBson.Broken();
            if (!AccountContactStorage.SafeDefinition(definition, Validator(name))) throw StockReleaseBson.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct); var required = Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw StockReleaseBson.Broken();
        }
    }
}
