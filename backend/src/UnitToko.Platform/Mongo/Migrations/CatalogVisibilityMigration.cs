using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v6 adds approved catalog assignments and their ownership anchors. Existing SKUs remain unchanged.
internal sealed class CatalogVisibilityMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 6;
    private sealed record RequiredIndex(string Collection, string Name, BsonDocument Key);
    private static RequiredIndex[] Indexes() =>
    [
        new("catalogEntitlements", "ix_userId_membershipId_status_effectiveAt_assortmentId", new()
            { { "userId", 1 }, { "membershipId", 1 }, { "status", 1 }, { "effectiveAt", 1 }, { "assortmentId", 1 } }),
        new("catalogAssortments", "ix_status", new("status", 1))
    ];

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var definitions = new List<(string Name, BsonDocument Validator, bool Exists)>();
        var indexesToCreate = new List<RequiredIndex>();
        foreach (var (name, required) in CatalogVisibilitySchemas.Collections())
        {
            await lease.RenewAsync(token);
            var collection = await (await database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = collection?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if ((collection is not null && collection.GetValue("type", "collection") != "collection") ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                (options.TryGetValue("collation", out var collation) && !SimpleCollation(collation)))
                throw new InvalidOperationException($"Catalog visibility migration found incompatible collection options in {name}; reconcile them before retrying");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = Preserve(previous, required);
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                throw new InvalidOperationException($"Catalog visibility migration found incompatible records in {name}; reconcile approved records before retrying");
            definitions.Add((name, validator, collection is not null));

            var existing = collection is null ? [] : await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(token)).ToListAsync(token);
            // Assignment history and assortment anchors must not silently expire.
            if (existing.Any(index => index.Contains("expireAfterSeconds"))) throw IncompatibleIndex(name);
            if (collection is not null && !existing.Any(IsIdentityIndex)) throw IncompatibleIndex(name);
            foreach (var planned in Indexes().Where(index => index.Collection == name))
            {
                var equivalent = false;
                foreach (var index in existing)
                {
                    var sameName = index["name"] == planned.Name;
                    var sameKey = index["key"].AsBsonDocument.Equals(planned.Key);
                    if (!sameName && !sameKey) continue;
                    if (!sameKey || index.GetValue("unique", false).AsBoolean || UnsafeIndexOptions(index))
                        throw IncompatibleIndex(name);
                    equivalent = true;
                }
                if (!equivalent) indexesToCreate.Add(planned);
            }
        }

        // Both collections are preflighted before any collection, validator or index mutation.
        foreach (var (name, validator, exists) in definitions)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { exists ? "collMod" : "create", name }, { "validator", validator }, { "validationLevel", "strict" },
                { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: token);
        }
        foreach (var index in indexesToCreate)
        {
            await lease.RenewAsync(token);
            await database.GetCollection<BsonDocument>(index.Collection).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = false }), cancellationToken: token);
        }
    }

    private static bool IsIdentityIndex(BsonDocument index) => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) &&
        (index["name"] == "_id_" || index.GetValue("unique", false).AsBoolean) && !UnsafeIndexOptions(index);
    private static bool UnsafeIndexOptions(BsonDocument index) => index.GetValue("sparse", false).AsBoolean ||
        index.GetValue("hidden", false).AsBoolean || index.Contains("partialFilterExpression") ||
        index.TryGetValue("collation", out var collation) && !SimpleCollation(collation);
    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static InvalidOperationException IncompatibleIndex(string collection) => new(
        $"Catalog visibility migration found an incompatible index in {collection}; reconcile its definition before retrying");
    private static BsonDocument Preserve(BsonDocument previous, BsonDocument required) => previous.ElementCount == 0 ? required :
        Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) ||
        validator.TryGetValue("$and", out var value) && value.IsBsonArray &&
        value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
}
