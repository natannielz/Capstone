using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>New private saved-view storage, installed only while writers are quiescent. No predecessor business data is reinterpreted.</summary>
internal sealed class OrderViewMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 112;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, bool NeedsKey, bool NeedsRequest);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Invalid("predecessor111 or migration112 lease");
        var targets = new List<Target>();
        foreach (var name in OrderViewSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            // There was no authorized saved-view writer before version 112. Partial DDL resumes only with empty new stores.
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unexpected saved-view data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
            var required = OrderViewSchemas.Validator(name); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            var key = OrderViewSchemas.Key(name); var requestKey = OrderViewSchemas.RequestKey();
            bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") || index.GetValue("sparse", false).AsBoolean ||
                index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var indexCollation) && !Simple(indexCollation);
            if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key) && !(name == OrderViewBson.Versions && index["key"].Equals(requestKey)))) throw Invalid("retention or uniqueness");
            if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_order_view_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Invalid("saved-view revision index");
            if (name == OrderViewBson.Versions && indexes.Any(index => (index["key"].Equals(requestKey) || index["name"] == "ux_order_view_request") &&
                (!index["key"].Equals(requestKey) || !index.GetValue("unique", false).AsBoolean))) throw Invalid("request index");
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            targets.Add(new(name, definition is not null, validator, matching.Length == 0, name == OrderViewBson.Versions && !indexes.Any(index => index["key"].Equals(requestKey))));
        }
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (target.NeedsRequest) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(OrderViewSchemas.RequestKey(), new CreateIndexOptions { Unique = true, Name = "ux_order_view_request" }), cancellationToken: ct);
            }
            if (target.NeedsKey) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(OrderViewSchemas.Key(target.Name), new CreateIndexOptions { Unique = true, Name = "ux_order_view_" + target.Name }), cancellationToken: ct);
            }
        }
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static InvalidOperationException Invalid(string reason) => new("Order saved-view migration found incompatible " + reason + "; review before retrying");
}
