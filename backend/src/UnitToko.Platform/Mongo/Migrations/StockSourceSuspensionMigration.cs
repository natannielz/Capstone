using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Installs stop history after complete historical preflight. It never activates or drains a source.</summary>
internal sealed class StockSourceSuspensionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 18;
    private const string Collection = StockSourceSuspensionSchemas.Collection;
    private sealed record Index(string Collection, string Name, BsonDocument Key, bool Unique = false);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        BsonDocument validator; bool exists; var create = new List<Index>();
        var maxima = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(StockSourceAliasSchemas.Collections))
            {
                await lease.RenewAsync(ct);
                var setup = StockSourceSetupSchemas.Collections.Contains(name);
                var revision = StockSourceRevisionSchemas.Collections.Contains(name);
                await InventoryRepository.RequireStorageAsync(database, name,
                    setup ? StockSourceSetupSchemas.Validator(name) : revision ? StockSourceRevisionSchemas.Validator(name) : StockSourceAliasSchemas.Validator(name),
                    setup ? StockSourceSetupSchemas.Key(name) : revision ? StockSourceRevisionSchemas.Key(name) : StockSourceAliasSchemas.Key(name), ct);
                if (name != "inventoryPoolRegistrations") maxima.Add(name, await MaximumAsync(name, ct));
            }
            var definition = await DefinitionAsync(Collection, ct); exists = definition is not null;
            var previous = definition?["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument ?? new();
            var required = StockSourceSuspensionSchemas.Validator();
            validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new("$and", new BsonArray { previous, required });
            var rows = Rows(Collection);
            if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Invalid("preexisting suspension records");
            var indexes = exists ? await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct) : [];
            var expected = new Index(Collection, "ux_stock_source_suspension_revision", StockSourceSuspensionSchemas.Key(), true);
            if (exists) RequireIdentity(indexes);
            if (indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean &&
                (!index["key"].Equals(expected.Key) || Unsafe(index)))) throw Invalid("additional suspension uniqueness");
            PlanIndex(indexes, expected, create);
            BsonDocument[] duplicates =
            [
                new("$group", new BsonDocument { { "_id", new BsonDocument { { "poolId", "$poolId" }, { "revision", "$suspensionRevision" } } }, { "count", new BsonDocument("$sum", 1) } }),
                new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))), new("$limit", 1)
            ];
            if (await rows.Aggregate<BsonDocument>(duplicates).AnyAsync(ct)) throw Invalid("duplicate suspension version");
            foreach (var lookup in StockSourceSuspensionSchemas.LookupIndexes())
            {
                await lease.RenewAsync(ct);
                if (await DefinitionAsync(lookup.Collection, ct) is null) throw Invalid("missing provenance collection");
                var provenance = await (await Rows(lookup.Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
                RequireIdentity(provenance);
                PlanIndex(provenance, new(lookup.Collection, lookup.Name, lookup.Key), create);
            }
            await RequireReceiptIndexAsync(ct);
            maxima.Add(Collection, await MaximumAsync(Collection, ct)); maxima.Add("auditEvents", await MaximumAsync("auditEvents", ct));
            foreach (var (name, maximum) in maxima) await RequireCounterAsync(name, maximum, ct);
            await StockSourceSuspensionMigrationSourceGraph.ValidateAsync(database, lease, ct);
            await StockSourceAliasMigrationGraph.ValidateAsync(database, lease, ct);
            await StockSourceSuspensionMigrationGraph.ValidateAsync(database, lease, ct);
        }
        catch (Exception exception) when (exception is UnitToko.Modules.Contracts.DomainValidationException or UnitToko.Platform.Authorization.AuthorizationDeniedException or
            InvalidCastException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        { throw Invalid("historical graph or storage"); }

        // MigrationGate owns the deployment lease. The deployment runbook quiesces application writers.
        // No target DDL or allocator advancement occurs before every prerequisite and historical check above.
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { exists ? "collMod" : "create", Collection }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
        }, cancellationToken: ct);
        foreach (var index in create)
        {
            await lease.RenewAsync(ct);
            await Rows(index.Collection).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
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
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private async Task<BsonDocument?> DefinitionAsync(string name, CancellationToken ct)
    {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null) return null;
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") ||
            options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
        return definition;
    }
    private static void RequireIdentity(List<BsonDocument> indexes)
    {
        if (indexes.Any(index => index.Contains("expireAfterSeconds")) || !indexes.Any(index => index["name"] == "_id_" &&
            index["key"].Equals(new BsonDocument("_id", 1)) && !Unsafe(index))) throw Invalid("identity or retention index");
    }
    private static void PlanIndex(List<BsonDocument> indexes, Index expected, List<Index> create)
    {
        var found = false;
        foreach (var index in indexes)
        {
            var same = index["key"].Equals(expected.Key);
            if (!same && index["name"] != expected.Name) continue;
            if (!same || index.GetValue("unique", false).AsBoolean != expected.Unique || Unsafe(index)) throw Invalid("required index");
            found = true;
        }
        if (!found) create.Add(expected);
    }
    private async Task<long> MaximumAsync(string name, CancellationToken ct)
    {
        var row = await Rows(name).Find(new BsonDocument("_id", new BsonDocument("$type", "long"))).Sort(new BsonDocument("_id", -1)).Limit(1).FirstOrDefaultAsync(ct);
        return row?["_id"].AsInt64 ?? 0;
    }
    private async Task RequireCounterAsync(string name, long maximum, CancellationToken ct)
    {
        var row = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (maximum == long.MaxValue || row is not null && (!row.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue))
            throw Invalid("allocator high-water mark");
    }
    private async Task RequireReceiptIndexAsync(CancellationToken ct)
    {
        var indexes = await (await Rows("idempotencyRecords").Indexes.ListAsync(ct)).ToListAsync(ct);
        var key = new BsonDocument { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } };
        var legacy = new BsonDocument("$and", new BsonArray(key.Names.Select(field => new BsonDocument(field, new BsonDocument("$exists", true)))));
        if (!indexes.Any(index =>
        {
            if (!index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean) return false;
            var copy = index.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("partialFilterExpression", out var partial) && CatalogSchemas.SemanticEquals(partial, legacy)) copy.Remove("partialFilterExpression");
            return !Unsafe(copy);
        })) throw Invalid("receipt uniqueness");
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !Simple(collation);
    internal static InvalidOperationException Invalid(string reason) => new($"Stock-source suspension migration found incompatible {reason}; review records before retrying");
}