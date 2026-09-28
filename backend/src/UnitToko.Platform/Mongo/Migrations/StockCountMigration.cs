using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

// Draft v14. The integration owner registers it only after the evidence migration.
internal sealed class StockCountMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 14;
    private sealed record RequiredIndex(string Name, BsonDocument Key, bool Unique = false, BsonDocument? Partial = null);
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, List<RequiredIndex> Create, long Maximum);
    private static RequiredIndex[] Indexes(string name) => name switch
    {
        StockCountBson.Collection =>
        [
            new("ux_stock_count_request", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
            new("ix_stock_count_location", new() { { "location.organizationId", 1 }, { "location.shopId", 1 },
                { "location.channelId", 1 }, { "location.warehouseId", 1 }, { "_id", 1 } })
        ],
        "auditEvents" => [new("ux_stock_count_audit_observation", new("observationId", 1), true, new("kind", StockCountBson.AuditKind))],
        "idempotencyRecords" => [],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var targets = new List<Target>();
        foreach (var name in StockCountSchemas.Collections)
        {
            await lease.RenewAsync(token);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
            var required = StockCountSchemas.Validator(name);
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous :
                new BsonDocument("$and", new BsonArray { previous, required });
            var collection = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await collection.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                throw Invalid("preexisting records");
            var existing = definition is null ? [] : await (await collection.Indexes.ListAsync(token)).ToListAsync(token);
            if (existing.Any(index => index.Contains("expireAfterSeconds")) ||
                definition is not null && !existing.Any(index => index["name"] == "_id_" &&
                    index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && !Unsafe(index)))
                throw Invalid("identity or TTL index");
            if (name == StockCountBson.Collection)
            {
                // Extra uniqueness could reject valid later counts; shared module constraints remain untouched.
                var expectedUnique = Indexes(name).Single(index => index.Unique);
                if (existing.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean &&
                    (!index["key"].AsBsonDocument.Equals(expectedUnique.Key) || Unsafe(index))))
                    throw Invalid("additional observation uniqueness");
            }
            var create = new List<RequiredIndex>();
            foreach (var index in Indexes(name))
            {
                var found = false;
                foreach (var old in existing)
                {
                    var sameKey = old["key"].AsBsonDocument.Equals(index.Key);
                    var samePartial = index.Partial is null ? !old.Contains("partialFilterExpression") :
                        old.TryGetValue("partialFilterExpression", out var partial) && CatalogSchemas.SemanticEquals(partial, index.Partial);
                    if (old["name"] != index.Name && !(sameKey && samePartial)) continue;
                    var check = old.DeepClone().AsBsonDocument;
                    if (samePartial) check.Remove("partialFilterExpression");
                    if (!sameKey || !samePartial || old.GetValue("unique", false).AsBoolean != index.Unique || Unsafe(check))
                        throw Invalid("required index");
                    found = true;
                }
                if (index.Unique)
                {
                    var group = new BsonDocument(index.Key.Names.Select(field => new BsonElement(field.Replace('.', '_'), "$" + field)));
                    BsonDocument[] pipeline =
                    [
                        new("$match", index.Partial ?? new()),
                        new("$group", new BsonDocument { { "_id", group }, { "count", new BsonDocument("$sum", 1) } }),
                        new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))), new("$limit", 1)
                    ];
                    if (await collection.Aggregate<BsonDocument>(pipeline).AnyAsync(token)) throw Invalid("duplicate keys");
                }
                if (!found) create.Add(index);
            }
            if (name == "idempotencyRecords") RequireReceiptIndex(existing);
            long maximum = 0;
            if (name != "idempotencyRecords")
            {
                var high = await collection.Find(new BsonDocument("_id", new BsonDocument("$type", "long")))
                    .Sort(new BsonDocument("_id", -1)).Limit(1).FirstOrDefaultAsync(token);
                maximum = high?["_id"].AsInt64 ?? 0;
                var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
                    .Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(token);
                if (maximum == long.MaxValue || counter is not null && (!counter.TryGetValue("value", out var value) ||
                    !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator high-water mark");
            }
            targets.Add(new(name, definition is not null, validator, create, maximum));
        }
        await StockCountMigrationGraph.ValidateAsync(database, lease, token);
        // No target DDL or sequence mutation before every shape, index, graph and allocator passed.
        foreach (var target in targets)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: token);
        }
        foreach (var target in targets)
        {
            if (target.Maximum > 0)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority)
                    .UpdateOneAsync(new BsonDocument("_id", target.Name), Builders<BsonDocument>.Update.Max("value", target.Maximum),
                        new UpdateOptions { IsUpsert = true }, token);
            }
            foreach (var index in target.Create)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions<BsonDocument>
                        { Name = index.Name, Unique = index.Unique, PartialFilterExpression = index.Partial }), cancellationToken: token);
            }
        }
    }
    private static void RequireReceiptIndex(IEnumerable<BsonDocument> indexes)
    {
        var key = new BsonDocument { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } };
        var legacyPartial = new BsonDocument("$and", new BsonArray(key.Names.Select(field => new BsonDocument(field, new BsonDocument("$exists", true)))));
        if (!indexes.Any(index =>
        {
            if (!index["key"].AsBsonDocument.Equals(key) || !index.GetValue("unique", false).AsBoolean) return false;
            var copy = index.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("partialFilterExpression", out var partial) && CatalogSchemas.SemanticEquals(partial, legacyPartial)) copy.Remove("partialFilterExpression");
            return !Unsafe(copy);
        })) throw Invalid("actor receipt uniqueness");
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") ||
        index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static bool Contains(BsonDocument existing, BsonDocument required) => CatalogSchemas.SemanticEquals(existing, required) ||
        existing.TryGetValue("$and", out var and) && and.IsBsonArray && and.AsBsonArray.Any(value => value.IsBsonDocument && Contains(value.AsBsonDocument, required));
    internal static InvalidOperationException Invalid(string reason) =>
        new($"Stock-count migration found incompatible {reason}; review records before retrying");
}
