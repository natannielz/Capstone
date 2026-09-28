using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Installs source settings revision history after validating the complete creation/revision graph.
/// No source activation, handoff acceptance, quantities or operational writer authority are inferred.</summary>
internal sealed class StockSourceRevisionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 16;
    private sealed record Index(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, Index[] Create, long Maximum);
    private static Index[] Indexes(string name) => name switch
    {
        "stockSourceRevisionProposals" => [
            new("ix_stock_source_revision_location", new()
            { { "input.location.organizationId", 1 }, { "input.location.shopId", 1 }, { "input.location.warehouseId", 1 }, { "input.location.channelId", 1 }, { "_id", 1 } }),
            new("ix_stock_source_revision_pool", new() { { "poolId", 1 }, { "_id", 1 } })],
        "stockSourceRevisionDecisions" => [new("ux_stock_source_revision_decision", StockSourceRevisionSchemas.Key(name), true)],
        "stockSourceRevisions" => [
            new("ux_stock_source_revision_version", StockSourceRevisionSchemas.Key(name), true),
            new("ix_stock_source_applied_proposal", new("proposalId", 1)),
            new("ix_stock_source_applied_decision", new("decisionId", 1))],
        _ => throw Invalid("collection")
    };
    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        List<Target> targets;
        long auditMaximum;
        var originalMaxima = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            targets = [];
            foreach (var name in StockSourceSetupSchemas.Collections)
            {
                await lease.RenewAsync(token);
                await InventoryRepository.RequireStorageAsync(database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), token);
                if (name == "inventoryPoolRegistrations") continue;
                var maximum = await MaximumAsync(name, token);
                await RequireCounterAsync(name, maximum, token);
                originalMaxima.Add(name, maximum);
            }
            foreach (var name in StockSourceRevisionSchemas.Collections)
            {
                await lease.RenewAsync(token);
                var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
                var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
                if (definition is not null && definition.GetValue("type", "") != "collection" ||
                    options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                    options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
                var required = StockSourceRevisionSchemas.Validator(name);
                var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
                var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous :
                    new BsonDocument("$and", new BsonArray { previous, required });
                var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
                if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token))
                    throw Invalid("preexisting records");
                var existing = definition is null ? [] : await (await rows.Indexes.ListAsync(token)).ToListAsync(token);
                if (existing.Any(index => index.Contains("expireAfterSeconds")) ||
                    definition is not null && !existing.Any(index => index["name"] == "_id_" &&
                        index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && !Unsafe(index)))
                    throw Invalid("identity or TTL index");
                var indexes = Indexes(name);
                if (existing.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean &&
                    !indexes.Any(expected => expected.Unique && index["key"].AsBsonDocument.Equals(expected.Key) && !Unsafe(index))))
                    throw Invalid("additional uniqueness");
                var create = new List<Index>();
                foreach (var expected in indexes)
                {
                    var found = false;
                    foreach (var old in existing)
                    {
                        var sameKey = old["key"].AsBsonDocument.Equals(expected.Key);
                        if (old["name"] != expected.Name && !sameKey) continue;
                        if (!sameKey || old.GetValue("unique", false).AsBoolean != expected.Unique || Unsafe(old))
                            throw Invalid("required index");
                        found = true;
                    }
                    if (expected.Unique)
                    {
                        var key = new BsonDocument(expected.Key.Names.Select(field => new BsonElement(field.Replace('.', '_'), "$" + field)));
                        BsonDocument[] pipeline =
                        [
                            new("$group", new BsonDocument { { "_id", key }, { "count", new BsonDocument("$sum", 1) } }),
                            new("$match", new BsonDocument("count", new BsonDocument("$gt", 1))), new("$limit", 1)
                        ];
                        if (await rows.Aggregate<BsonDocument>(pipeline).AnyAsync(token)) throw Invalid("duplicate revision or decision");
                    }
                    if (!found) create.Add(expected);
                }
                var maximum = await MaximumAsync(name, token);
                await RequireCounterAsync(name, maximum, token);
                targets.Add(new(name, definition is not null, validator, create.ToArray(), maximum));
            }
            auditMaximum = await MaximumAsync("auditEvents", token);
            await RequireCounterAsync("auditEvents", auditMaximum, token);
            await RequireReceiptIndexAsync(token);
            await StockSourceRevisionMigrationGraph.ValidateAsync(database, lease, token);
        }
        catch (Exception exception) when (exception is UnitToko.Modules.Contracts.DomainValidationException or
            UnitToko.Platform.Authorization.AuthorizationDeniedException or InvalidCastException or KeyNotFoundException or FormatException or ArgumentException)
        { throw Invalid("historical record graph"); }

        // All collection, index, graph and allocator checks finish before target DDL or sequence changes.
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
            await AdvanceAsync(target.Name, target.Maximum, lease, token);
            foreach (var index in target.Create)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: token);
            }
        }
        foreach (var (name, maximum) in originalMaxima) await AdvanceAsync(name, maximum, lease, token);
        await AdvanceAsync("auditEvents", auditMaximum, lease, token);
    }
    private async Task<long> MaximumAsync(string name, CancellationToken ct)
    {
        var highest = await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("_id", new BsonDocument("$type", "long"))).Sort(new BsonDocument("_id", -1)).Limit(1).FirstOrDefaultAsync(ct);
        return highest?["_id"].AsInt64 ?? 0;
    }
    private async Task RequireCounterAsync(string name, long maximum, CancellationToken ct)
    {
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (maximum == long.MaxValue || counter is not null && (!counter.TryGetValue("value", out var value) ||
            !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator high-water mark");
    }
    private async Task AdvanceAsync(string name, long maximum, MigrationLease lease, CancellationToken ct)
    {
        if (maximum == 0) return;
        await lease.RenewAsync(ct);
        await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(
            new BsonDocument("_id", name), Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
    }
    private async Task RequireReceiptIndexAsync(CancellationToken ct)
    {
        var indexes = await (await database.GetCollection<BsonDocument>("idempotencyRecords").Indexes.ListAsync(ct)).ToListAsync(ct);
        var key = new BsonDocument { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } };
        var legacyPartial = new BsonDocument("$and", new BsonArray(key.Names.Select(field => new BsonDocument(field, new BsonDocument("$exists", true)))));
        if (!indexes.Any(index =>
        {
            if (!index["key"].AsBsonDocument.Equals(key) || !index.GetValue("unique", false).AsBoolean) return false;
            var copy = index.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("partialFilterExpression", out var partial) && CatalogSchemas.SemanticEquals(partial, legacyPartial))
                copy.Remove("partialFilterExpression");
            return !Unsafe(copy);
        }) || indexes.Any(index => index.Contains("expireAfterSeconds"))) throw Invalid("receipt uniqueness or TTL");
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.ElementCount == 1 && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") ||
        index.TryGetValue("collation", out var collation) && !Simple(collation);
    internal static InvalidOperationException Invalid(string reason) => new($"Stock-source revision migration found incompatible {reason}; review records before retrying");
}
