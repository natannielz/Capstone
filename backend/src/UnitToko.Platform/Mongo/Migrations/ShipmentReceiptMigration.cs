using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Install the receipt owner only after preflighting both collections and all prior effects.
/// No historical receipt is inferred from dispatch, a document, or a courier's statement.</summary>
internal sealed class ShipmentReceiptMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 26;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Create, BsonDocument Validator, bool Index, bool TimeIndex)>();
        foreach (var name in ShipmentReceiptSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted receipt data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
            var required = ShipmentReceiptSchemas.Validator(name); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            var key = ShipmentReceiptSchemas.Key(name); var indexName = "ux_" + name;
            if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Invalid("retention or extra uniqueness");
            if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
            var matching = indexes.Where(index => index["name"] == indexName || index["key"].Equals(key)).ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Invalid("required index");
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            var timeIndex = false;
            if (name == ShipmentReceiptSchemas.Collections[0]) {
                var timeKey = ShipmentReceiptSchemas.ChallengeTimeKey();
                var timeMatches = indexes.Where(index => index["name"] == "ix_receipt_challenge_shipment_time" || index["key"].Equals(timeKey)).ToArray();
                if (timeMatches.Any(index => !index["key"].Equals(timeKey) || index.GetValue("unique", false).AsBoolean)) throw Invalid("challenge range index");
                timeIndex = timeMatches.Length == 0;
            }
            installs.Add((name, definition is null, validator, matching.Length == 0, timeIndex));
        }
        var prefix = new BsonRegularExpression(@"^fulfillment\.receipt(?:[.:]|$)");
        foreach (var (collection, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("operation", prefix), new BsonDocument("kind", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) }))
        }) {
            await lease.RenewAsync(ct);
            if (await database.GetCollection<BsonDocument>(collection).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted receipt effects");
        }
        foreach (var install in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { install.Create ? "create" : "collMod", install.Name }, { "validator", install.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (install.Index) await database.GetCollection<BsonDocument>(install.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(ShipmentReceiptSchemas.Key(install.Name), new CreateIndexOptions { Unique = true, Name = "ux_" + install.Name }), cancellationToken: ct);
            if (install.TimeIndex) await database.GetCollection<BsonDocument>(install.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(ShipmentReceiptSchemas.ChallengeTimeKey(), new CreateIndexOptions { Name = "ix_receipt_challenge_shipment_time" }), cancellationToken: ct);
        }
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") ||
        index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static InvalidOperationException Invalid(string reason) => new("Shipment receipt migration preflight failed: " + reason);
}
