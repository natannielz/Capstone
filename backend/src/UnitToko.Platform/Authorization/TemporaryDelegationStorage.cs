using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Authorization;

internal static class TemporaryDelegationStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in TemporaryDelegationSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw TemporaryDelegationRecords.Invalid();
            if (!AccountContactStorage.SafeDefinition(definition, TemporaryDelegationSchemas.Validator(name))) throw TemporaryDelegationRecords.Invalid();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = TemporaryDelegationSchemas.Keys(name);
            if (!AccountContactStorage.SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))))
                throw TemporaryDelegationRecords.Invalid();
        }
    }
}
