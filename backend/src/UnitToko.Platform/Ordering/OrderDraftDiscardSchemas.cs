using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Ordering;

/// <summary>Ordering-owned discard state for unsubmitted order drafts (migration 118). One row per currently discarded draft
/// (`_id` = draftId); restore removes it and the purge deletes the draft once `restorableUntil` has passed. Audit keeps the history.</summary>
internal static class OrderDraftDiscardSchemas
{
    internal const string Collection = "orderDraftDiscards", Kind = "ordering.draft.discard";
    internal static readonly TimeSpan Window = TimeSpan.FromDays(30);
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] Keys = [
        ("discardedBy_requestKey", new BsonDocument { { "discardedBy", 1 }, { "requestKey", 1 } }, true),
        ("restorableUntil", new BsonDocument("restorableUntil", 1), false)];

    internal static BsonDocument Validator()
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(Kind) }, { "discardedBy", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "context", OrderDraftSchemas.Context() }, { "revision", S.Number(1) },
            { "discardedAt", new BsonDocument("bsonType", "date") }, { "restorableUntil", new BsonDocument("bsonType", "date") },
            { "requestKey", S.Text(128) }, { "bodyHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } } } };
        S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("discardedAt"), S.DateRange("restorableUntil"),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray {
                "$restorableUntil", new BsonDocument("$add", new BsonArray { "$discardedAt", (long)Window.TotalMilliseconds }) })) });
    }

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        var definition = await AccountContactStorage.DefinitionAsync(database, Collection, ct);
        var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(Collection).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is null || !AccountContactStorage.SafeDefinition(definition, Validator()) || !AccountContactStorage.SafeIndexes(indexes, Keys) ||
            Keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
            throw new InvalidOperationException("Order draft discard storage is unavailable");
    }
}
