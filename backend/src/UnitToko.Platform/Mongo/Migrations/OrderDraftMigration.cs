using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>New draft storage, installed only while writers are quiescent. No predecessor business data is reinterpreted.</summary>
internal sealed class OrderDraftMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 21;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, bool NeedsKey);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var targets = new List<Target>();
        foreach (var name in OrderDraftSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            // There was no authorized draft writer before version 21. Partial DDL resumes only with empty new stores.
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unexpected draft data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
            var required = OrderDraftSchemas.Validator(name); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            var key = OrderDraftSchemas.Key(name);
            bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") || index.GetValue("sparse", false).AsBoolean ||
                index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var indexCollation) && !Simple(indexCollation);
            if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Invalid("retention or uniqueness");
            if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_order_draft_" + name).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Invalid("draft revision index");
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            targets.Add(new(name, definition is not null, validator, matching.Length == 0 && name == OrderDraftBson.Versions));
        }
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (target.NeedsKey) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(OrderDraftSchemas.Key(target.Name), new CreateIndexOptions { Unique = true, Name = "ux_order_draft_" + target.Name }), cancellationToken: ct);
            }
        }
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static InvalidOperationException Invalid(string reason) => new("Order draft migration found incompatible " + reason + "; review before retrying");
}
