using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Ordering;

/// <summary>F-030: which saved draft each order came from (migration 123). One row per order (`_id` = orderId), written in the
/// order's own transaction. A draft with a row here has become an order: it is no longer listed (so it is never the keranjang
/// again) and cannot be saved. The unique `draftId` index makes one order the only consumer of a draft. `successorId` is the new
/// draft that carries what the order left unordered (null when it left nothing). Reading it by id stays possible so the
/// checkout that placed it can finish its other channel groups and replay a lost answer.</summary>
internal static class OrderDraftSubmissionSchemas
{
    internal const string Collection = "orderDraftSubmissions", Kind = "ordering.draft.submission";
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] Keys = [
        ("draftId", new BsonDocument("draftId", 1), true)];

    internal static BsonDocument Validator()
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(Kind) }, { "draftId", S.Number(1) }, { "draftRevision", S.Number(1) }, { "successorId", new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1 } } }, { "createdBy", S.Number(1) },
            { "skuIds", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "uniqueItems", true }, { "items", S.Number(1) } } },
            { "recordedAt", new BsonDocument("bsonType", "date") } };
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt") });
    }

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        var definition = await AccountContactStorage.DefinitionAsync(database, Collection, ct);
        var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(Collection).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is null || !AccountContactStorage.SafeDefinition(definition, Validator()) || !AccountContactStorage.SafeIndexes(indexes, Keys) ||
            Keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
            throw new InvalidOperationException("Order draft submission storage is unavailable");
    }
}
