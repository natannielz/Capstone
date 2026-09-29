using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Forward installation for the new preparation owner. Existing orders, stock and generic
/// shipments remain untouched. Unaccepted preparation data/effects require explicit conversion.</summary>
internal sealed class ShipmentPreparationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 23;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, bool CreateIndex);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var targets = new List<Target>();
        foreach (var name in ShipmentPreparationSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted preparation data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
            var required = ShipmentPreparationSchemas.Validator(name); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            var key = ShipmentPreparationSchemas.Key(name); var indexName = name == ShipmentPreparationBson.Heads ? "ux_preparation_suborder" : "ux_preparation_version";
            if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Invalid("retention or extra uniqueness");
            if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
            var matching = indexes.Where(index => index["name"] == indexName || index["key"].Equals(key)).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Invalid("required index");
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            targets.Add(new(name, definition is not null, validator, matching.Length == 0));
        }
        var prefix = new BsonRegularExpression(@"^fulfillment\.preparation(?:[.:]|$)");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("operation", prefix), new BsonDocument("kind", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) })),
            ("movements", new("$or", new BsonArray { new BsonDocument("source.namespace", prefix), new BsonDocument("sourceEvent", prefix) }))
        }) {
            await lease.RenewAsync(ct);
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted preparation effects");
        }
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (target.CreateIndex) await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                ShipmentPreparationSchemas.Key(target.Name), new CreateIndexOptions { Unique = true, Name = target.Name == ShipmentPreparationBson.Heads ? "ux_preparation_suborder" : "ux_preparation_version" }), cancellationToken: ct);
        }
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value["locale"] == "simple";
    private static bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") ||
        index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static InvalidOperationException Invalid(string reason) => new("Shipment preparation migration preflight failed: " + reason);
}
