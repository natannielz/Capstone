using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Forward-only installation of reviewed prices; deployment must quiesce writers.</summary>
internal sealed class CatalogPriceMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 20;
    private sealed record Index(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, List<Index> Create);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    internal static InvalidOperationException Invalid(string reason) => new($"Catalog-price migration found incompatible {reason}; review records before retrying");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var targets = new List<Target>(); var maxima = new Dictionary<string, long>();
        try
        {
            foreach (var name in CatalogSchemas.Collections)
                await RequireCatalogStorageAsync(name, ct);
            foreach (var name in CatalogPriceSchemas.Collections)
            {
                await lease.RenewAsync(ct);
                var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
                var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
                if (definition is not null && definition.GetValue("type", "") != "collection" ||
                    options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                    options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
                var required = CatalogPriceSchemas.Validator(name);
                var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
                var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous :
                    new BsonDocument("$and", new BsonArray { previous, required });
                if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Invalid("preexisting price records");
                var existing = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
                if (definition is not null) RequireIdentity(existing);
                var key = CatalogPriceSchemas.Key(name);
                if (existing.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean &&
                    (!index["key"].Equals(key) || Unsafe(index)))) throw Invalid("additional uniqueness");
                var indexes = new List<Index>();
                if (!key.Equals(new BsonDocument("_id", 1))) indexes.Add(new("ux_catalog_price_" + name, key, true));
                if (name is CatalogPriceBson.Proposals or CatalogPriceBson.Decisions)
                    indexes.Add(new("ix_catalog_price_sku_" + name, new() { { "skuId", 1 }, { "_id", 1 } }));
                var create = new List<Index>();
                foreach (var index in indexes)
                {
                    var matches = existing.Where(old => old["name"] == index.Name || old["key"].Equals(index.Key)).ToArray();
                    if (matches.Any(old => !old["key"].Equals(index.Key) || old.GetValue("unique", false).AsBoolean != index.Unique || Unsafe(old)))
                        throw Invalid("required index");
                    if (matches.Length == 0) create.Add(index);
                    if (index.Unique)
                    {
                        var grouping = new BsonDocument(key.Names.Select(field => new BsonElement(field, "$" + field)));
                        BsonDocument[] pipeline = [new("$group", new BsonDocument { { "_id", grouping }, { "count", new BsonDocument("$sum", 1) } }),
                            new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))), new("$limit", 1)];
                        if (await Rows(name).Aggregate<BsonDocument>(pipeline).AnyAsync(ct)) throw Invalid("duplicate decision or version");
                    }
                }
                targets.Add(new(name, definition is not null, validator, create));
                if (name != CatalogPriceBson.Heads) maxima.Add(name, await MaximumAsync(name, ct));
            }
            RequireIdentity(await (await Rows("auditEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
            await RequireReceiptIndexAsync(ct);
            maxima.Add("auditEvents", await MaximumAsync("auditEvents", ct));
            foreach (var (name, maximum) in maxima)
            {
                var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
                if (maximum == long.MaxValue || counter is not null && (!counter.TryGetValue("value", out var value) ||
                    !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator high-water mark");
            }
            await new CatalogPriceMigrationGraph(database).ValidateAsync(lease, ct);
        }
        catch (Exception error) when (error is UnitToko.Modules.Contracts.DomainValidationException or
            UnitToko.Platform.Authorization.AuthorizationDeniedException or InvalidCastException or KeyNotFoundException or
            FormatException or ArgumentException or OverflowException)
        { throw Invalid("historical graph or storage"); }

        // Finish all graph/index/counter preflight before the first target mutation. Each DDL step is resumable.
        foreach (var target in targets)
        {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: ct);
        }
        foreach (var target in targets) foreach (var index in target.Create)
        {
            await lease.RenewAsync(ct);
            await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima)
        {
            if (maximum == 0) continue;
            await lease.RenewAsync(ct);
            await Rows("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }

    private async Task<long> MaximumAsync(string name, CancellationToken ct) =>
        (await Rows(name).Find(new BsonDocument("_id", new BsonDocument("$type", "long"))).Sort(new BsonDocument("_id", -1)).FirstOrDefaultAsync(ct))?["_id"].AsInt64 ?? 0;
    private async Task RequireCatalogStorageAsync(string name, CancellationToken ct)
    {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || definition.GetValue("type", "") != "collection") throw Invalid("catalog storage");
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            options.TryGetValue("collation", out var collation) && !Simple(collation) ||
            !options.TryGetValue("validator", out var validator) || !validator.IsBsonDocument ||
            !InventorySchemas.Contains(validator.AsBsonDocument, CatalogSchemas.Validator(name))) throw Invalid("catalog validation");
        // Catalog owns several legitimate uniqueness constraints. Pricing only needs its stable primary key;
        // the single-key inventory guard would incorrectly reject those existing catalog indexes.
        RequireIdentity(await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
    }
    private async Task RequireReceiptIndexAsync(CancellationToken ct)
    {
        var indexes = await (await Rows("idempotencyRecords").Indexes.ListAsync(ct)).ToListAsync(ct); RequireIdentity(indexes);
        var key = new BsonDocument { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } };
        var legacy = new BsonDocument("$and", new BsonArray(key.Names.Select(field => new BsonDocument(field, new BsonDocument("$exists", true)))));
        if (!indexes.Any(index => {
            if (!index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean) return false;
            var copy = index.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("partialFilterExpression", out var partial) && CatalogSchemas.SemanticEquals(partial, legacy)) copy.Remove("partialFilterExpression");
            return !Unsafe(copy);
        })) throw Invalid("receipt uniqueness");
    }
    private static void RequireIdentity(List<BsonDocument> indexes)
    {
        if (indexes.Any(index => index.Contains("expireAfterSeconds")) || !indexes.Any(index => index["name"] == "_id_" &&
            index["key"].Equals(new BsonDocument("_id", 1)) && !Unsafe(index))) throw Invalid("identity or retention index");
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var value) && !Simple(value);
}
