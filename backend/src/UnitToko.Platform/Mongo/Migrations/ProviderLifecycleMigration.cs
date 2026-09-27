using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v5 records immutable provider events, admission holds and shared subject fences. It never infers
// historical provider incidents from blocked recovery intents or changes existing accounts.
internal sealed class ProviderLifecycleMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 5;
    private sealed record RequiredIndex(string Collection, string Name, BsonDocument Key, bool Unique = false);
    private static RequiredIndex[] Indexes() =>
    [
        new("identitySecurityEvents", "ux_issuer_eventId", new() { { "issuer", 1 }, { "eventId", 1 } }, true),
        new("identitySecurityEvents", "ix_issuer_subjectHash_occurredAt", new() { { "issuer", 1 }, { "subjectHash", 1 }, { "occurredAt", 1 } }),
        new("identitySecurityEvents", "ix_userId_receivedAt", new() { { "userId", 1 }, { "receivedAt", 1 } }),
        new("identityAdmissionHolds", "ix_userId_status", new() { { "userId", 1 }, { "status", 1 } })
    ];

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var definitions = new List<(string Name, BsonDocument Validator, bool Exists)>();
        var indexesToCreate = new List<RequiredIndex>();
        foreach (var (name, required) in ProviderLifecycleSchemas.Collections())
        {
            await lease.RenewAsync(token);
            var collection = await (await database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = collection?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if ((collection is not null && collection.GetValue("type", "collection") != "collection") ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                (options.TryGetValue("collation", out var collation) && !SimpleCollation(collation)))
                throw new InvalidOperationException($"Provider lifecycle migration found incompatible collection options in {name}; reconcile them before retrying");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = Preserve(previous, required);
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                throw new InvalidOperationException($"Provider lifecycle migration found incompatible records in {name}; reconcile approved records before retrying");
            definitions.Add((name, validator, collection is not null));

            var existing = collection is null ? [] : await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(token)).ToListAsync(token);
            // Expiring replay identities or active holds silently weakens the provider lifecycle.
            if (existing.Any(index => index.Contains("expireAfterSeconds"))) throw IncompatibleIndex(name);
            foreach (var planned in Indexes().Where(index => index.Collection == name))
            {
                var equivalent = false;
                foreach (var index in existing)
                {
                    var sameName = index["name"] == planned.Name;
                    var sameKey = index["key"].AsBsonDocument.Equals(planned.Key);
                    if (!sameName && !sameKey) continue;
                    if (!sameKey || index.GetValue("unique", false).AsBoolean != planned.Unique ||
                        index.GetValue("sparse", false).AsBoolean || index.Contains("partialFilterExpression") ||
                        index.TryGetValue("collation", out var indexCollation) && !SimpleCollation(indexCollation))
                        throw IncompatibleIndex(name);
                    equivalent = true;
                }
                if (!equivalent) indexesToCreate.Add(planned);
            }
        }

        await lease.RenewAsync(token);
        var duplicates = await database.GetCollection<BsonDocument>("identitySecurityEvents").WithReadConcern(ReadConcern.Majority)
            .Aggregate<BsonDocument>(new BsonDocument[]
            {
                new("$group", new BsonDocument { { "_id", new BsonDocument { { "issuer", "$issuer" }, { "eventId", "$eventId" } } }, { "count", new BsonDocument("$sum", 1) } }),
                new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))), new("$limit", 1)
            }).AnyAsync(token);
        if (duplicates) throw new InvalidOperationException("Provider lifecycle migration found duplicate provider event identities in identitySecurityEvents; reconcile approved records before retrying");

        // All shape, uniqueness and index compatibility checks precede collection/index changes.
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
                new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: token);
        }
    }

    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static InvalidOperationException IncompatibleIndex(string collection) => new(
        $"Provider lifecycle migration found an incompatible index in {collection}; reconcile its definition before retrying");
    private static BsonDocument Preserve(BsonDocument previous, BsonDocument required) => previous.ElementCount == 0 ? required :
        Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) ||
        validator.TryGetValue("$and", out var value) && value.IsBsonArray &&
        value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
}
