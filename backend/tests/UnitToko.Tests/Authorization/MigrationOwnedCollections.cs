using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

/// <summary>Collections the registered migration chain owns, taken from a clean full migration of a scratch
/// database. Resumed-chain tests assert their DDL only targets these, without a hand-kept list that every
/// later migration would outgrow.</summary>
internal static class MigrationOwnedCollections
{
    private static Task<IReadOnlySet<string>>? names;
    private static readonly object gate = new();

    internal static Task<IReadOnlySet<string>> NamesAsync(IMongoClient client)
    {
        lock (gate) return names ??= LoadAsync(client);
    }

    private static async Task<IReadOnlySet<string>> LoadAsync(IMongoClient client)
    {
        var name = "ut_owned_collections_" + Guid.NewGuid().ToString("N");
        try {
            var database = client.GetDatabase(name);
            await new CollectionMigrations(database, new SystemClock()).ApplyAsync();
            return (await (await database.ListCollectionNamesAsync()).ToListAsync()).Where(collection => !collection.StartsWith("system.", StringComparison.Ordinal)).ToHashSet();
        } finally { await client.DropDatabaseAsync(name); }
    }
}
