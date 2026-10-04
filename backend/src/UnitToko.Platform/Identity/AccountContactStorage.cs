using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

internal static class AccountContactStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in AccountContactSchemas.Collections)
        {
            var definition = await DefinitionAsync(database, name, ct) ?? throw AccountContactRecords.Broken();
            if (!SafeDefinition(definition, AccountContactSchemas.Validator(name))) throw AccountContactRecords.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = AccountContactSchemas.Keys(name);
            if (!SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => Matches(row, key))))
                throw AccountContactRecords.Broken();
        }
    }

    internal static Task<BsonDocument?> DefinitionAsync(IMongoDatabase database, string name, CancellationToken ct) =>
        ReadDefinitionAsync(database, name, ct);
    private static async Task<BsonDocument?> ReadDefinitionAsync(IMongoDatabase database, string name, CancellationToken ct) =>
        await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct))
            .SingleOrDefaultAsync(ct);

    internal static bool SafeDefinition(BsonDocument definition, BsonDocument required)
    {
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        return definition.GetValue("type", "") == "collection" && !options.GetValue("capped", false).AsBoolean &&
            !options.Contains("timeseries") && !options.Contains("expireAfterSeconds") && !options.Contains("encryptedFields") &&
            options.GetValue("validationLevel", "") == "strict" && options.GetValue("validationAction", "") == "error" &&
            (!options.TryGetValue("collation", out var collation) || InvoiceStorage.Simple(collation)) &&
            options.TryGetValue("validator", out var validator) && validator.IsBsonDocument &&
            InventorySchemas.Contains(validator.AsBsonDocument, required);
    }
    internal static bool Matches(BsonDocument row, (string Name, BsonDocument Key, bool Unique) key) =>
        row.GetValue("name", "") == key.Name && row.GetValue("key", BsonNull.Value) == key.Key &&
        row.GetValue("unique", false).AsBoolean == key.Unique && !InvoiceStorage.Unsafe(row) &&
        !row.Contains("partialFilterExpression");
    internal static bool SafeIndexes(IReadOnlyList<BsonDocument> indexes,
        (string Name, BsonDocument Key, bool Unique)[] required) =>
        indexes.Any(row => row.GetValue("name", "") == "_id_" && row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)) &&
        indexes.All(row => !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression") &&
            (row.GetValue("name", "") == "_id_" || required.Any(key => Matches(row, key))));
}
