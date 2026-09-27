using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v7 adds only the durable historical business-read audit collection. It seeds no business or authorization data.
internal sealed class HistoricalReadMigration(IMongoDatabase database) : IMigrationStep
{
    internal const string Collection = "businessReadEvents";
    public int Version => 7;
    private sealed record RequiredIndex(string Name, BsonDocument Key);
    private static RequiredIndex[] Indexes() =>
    [
        new("ix_actorId_createdAt", new() { { "actorId", 1 }, { "createdAt", 1 } }),
        new("ix_targetScope.organizationId_targetScope.branchId_createdAt", new()
            { { "targetScope.organizationId", 1 }, { "targetScope.branchId", 1 }, { "createdAt", 1 } })
    ];

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        await lease.RenewAsync(token);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions
            { Filter = new BsonDocument("name", Collection) }, token)).SingleOrDefaultAsync(token);
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if ((definition is not null && definition.GetValue("type", "collection") != "collection") ||
            options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            (options.TryGetValue("collation", out var collation) && !SimpleCollation(collation)))
            throw new InvalidOperationException("Historical-read migration found incompatible collection options in businessReadEvents; reconcile them before retrying");
        var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var required = HistoricalReadSchemas.Validator();
        var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous :
            new BsonDocument("$and", new BsonArray { previous, required });
        var rows = database.GetCollection<BsonDocument>(Collection).WithReadConcern(ReadConcern.Majority);
        if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
            throw new InvalidOperationException("Historical-read migration found incompatible records in businessReadEvents; reconcile them before retrying");
        var existing = definition is null ? [] : await (await rows.Indexes.ListAsync(token)).ToListAsync(token);
        if (existing.Any(index => index.Contains("expireAfterSeconds")) || definition is not null && !existing.Any(IsIdentityIndex))
            throw IncompatibleIndex();
        var create = new List<RequiredIndex>();
        foreach (var planned in Indexes())
        {
            var equivalent = false;
            foreach (var index in existing)
            {
                var sameName = index["name"] == planned.Name;
                var sameKey = index["key"].AsBsonDocument.Equals(planned.Key);
                if (!sameName && !sameKey) continue;
                if (!sameKey || index.GetValue("unique", false).AsBoolean || UnsafeIndexOptions(index)) throw IncompatibleIndex();
                equivalent = true;
            }
            if (!equivalent) create.Add(planned);
        }
        var maximum = await rows.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", -1))
            .Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(token);
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("_id", Collection)).SingleOrDefaultAsync(token);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < 0))
            throw new InvalidOperationException("Historical-read migration found an incompatible idCounters sequence for businessReadEvents; reconcile its high-water mark before retrying");

        // Record, options, retention, index, and counter preflight all complete before any mutation.
        await lease.RenewAsync(token);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { definition is null ? "create" : "collMod", Collection }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
        }, cancellationToken: token);
        if (maximum is not null)
        {
            await lease.RenewAsync(token);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(
                new BsonDocument("_id", Collection), Builders<BsonDocument>.Update.Max("value", maximum["_id"].AsInt64),
                new UpdateOptions { IsUpsert = true }, token);
        }
        foreach (var index in create)
        {
            await lease.RenewAsync(token);
            await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key,
                new CreateIndexOptions { Name = index.Name, Unique = false }), cancellationToken: token);
        }
    }

    private static bool IsIdentityIndex(BsonDocument index) => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) &&
        (index["name"] == "_id_" || index.GetValue("unique", false).AsBoolean) && !UnsafeIndexOptions(index);
    private static bool UnsafeIndexOptions(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.TryGetValue("collation", out var value) && !SimpleCollation(value);
    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static InvalidOperationException IncompatibleIndex() => new("Historical-read migration found an incompatible index in businessReadEvents; reconcile its definition before retrying");
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) ||
        validator.TryGetValue("$and", out var value) && value.IsBsonArray && value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
}
