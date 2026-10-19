using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Ordering;

/// <summary>Ordering-owned order-progress checkpoint seal store (migration 117). Each seal binds an order's history prefix
/// and the derived replay state at that checkpoint as MAC-covered facts with a facts version.</summary>
internal static class OrderProgressSealSchemas
{
    internal const string Collection = "orderProgressSeals", Kind = "ordering.orderProgress";
    // Bump when replay semantics change: older facts are then treated as absent (full replay, re-seal), never trusted.
    internal const int FactsVersion = 1;

    internal static BsonDocument Validator() => ProvenanceSealSchemas.Validator(Kind, facts: true);

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        var definition = await AccountContactStorage.DefinitionAsync(database, Collection, ct);
        var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(Collection).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        if (definition is null || !AccountContactStorage.SafeDefinition(definition, Validator()) ||
            !AccountContactStorage.SafeIndexes(indexes, ProvenanceSealSchemas.Keys) ||
            ProvenanceSealSchemas.Keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
            throw new InvalidOperationException("Order progress seal storage is unavailable");
    }
}
