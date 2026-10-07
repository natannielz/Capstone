using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Inventory;

internal static class PackConversionSchemas
{
    internal static readonly string[] Collections = [PackConversionBson.Conversions];
    private static BsonDocument Number(long min = 1) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Hash() => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Validator() {
        var scope = StockCountSchemas.Validator("stockCountObservations")["$and"][0]["$jsonSchema"]["properties"]["location"].DeepClone();
        var fields = new BsonDocument {
            { "_id", Number() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice("pack.conversion") }, { "phase", S.Choice("preparing", "committed") },
            { "input", S.Object(new() { { "location", scope }, { "ruleId", Number() }, { "ruleVersion", Number() }, { "sourceBalanceId", Number() },
                { "steps", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } } }) },
            { "expected", S.Object(new() { { "sourceBalanceRevision", Number() }, { "poolRevision", Number() }, { "poolFence", Number(0) }, { "lotValueRevision", Number() } }) },
            { "quantities", S.Object(new() { { "sourcePackages", Number() }, { "destinationPackages", Number() }, { "baseUnits", Number() } }) },
            { "policy", S.Choice(LotValuePolicy.ProportionalFloorPolicy) }, { "binding", Hash() },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", S.Object(new() {
                { "userId", Number() }, { "personId", Number() }, { "verification", S.Choice("Verified") }, { "version", Number() } }) } } },
            { "actorId", Number() }, { "actorGrantVersion", Number(0) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash() },
            { "reason", S.Text(1000) }, { "correlationId", S.Text(128) }, { "auditId", Number() }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "causeHash", Hash() }, { "snapshotHash", Hash() }, { "movementId", Nullable(Number()) },
            { "valueTransitionId", Nullable(Number()) }, { "valueTransitionHash", Nullable(Hash()) }, { "transferredValue", Nullable(S.Text(32)) }
        };
        foreach (var snapshot in new[] { "rule", "catalog", "poolSource", "sourceLot", "sourceValue", "before", "after", "destinationLot", "destinationBefore", "destinationAfter" })
            fields.Add(snapshot, new BsonDocument("bsonType", "object"));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)),
            new BsonDocument("$or", new BsonArray {
                new BsonDocument { { "phase", "preparing" }, { "movementId", BsonNull.Value }, { "valueTransitionId", BsonNull.Value },
                    { "valueTransitionHash", BsonNull.Value }, { "transferredValue", BsonNull.Value } },
                new BsonDocument { { "phase", "committed" }, { "movementId", new BsonDocument("$type", "long") },
                    { "valueTransitionId", new BsonDocument("$type", "long") }, { "valueTransitionHash", new BsonDocument("$type", "string") },
                    { "transferredValue", new BsonDocument { { "$type", "string" }, { "$regex", @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } } }
                }
            }) });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys() => [
        ("pack_conversion_actor_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
        ("pack_conversion_source_history", new() { { "input.sourceBalanceId", 1 }, { "_id", -1 } }, false),
        ("pack_conversion_destination", new("destinationBefore._id", 1), true)
    ];
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        var definition = await AccountContactStorage.DefinitionAsync(database, PackConversionBson.Conversions, ct);
        if (definition is null || !AccountContactStorage.SafeDefinition(definition, Validator()) ||
            !OrderStockSourceStorage.SafeIndexes((await (await database.GetCollection<BsonDocument>(PackConversionBson.Conversions).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray(), Keys()))
            throw PackConversionBson.Broken();
    }
}
