using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class PaymentDraftMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 30;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, bool CreateIndex);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<Target>();
        foreach (var name in PaymentDraftSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted payment declarations");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Invalid("collection options");
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct); var key = PaymentDraftSchemas.Key(name);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Invalid("retention or uniqueness");
            var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == "ux_payment_declaration_revision").ToArray();
            if (matching.Any(index => !index["key"].Equals(key) || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity/revision index");
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = PaymentDraftSchemas.Validator(name);
            installs.Add(new(name, definition is not null, previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new("$and", new BsonArray { previous, required }), matching.Length == 0 && name == PaymentDraftBson.Versions));
        }
        var prefix = new BsonRegularExpression(@"^billing\.payment\.draft(?:[.:]|$)");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("payments", new()), ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("operation", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) }))
        }) if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted legacy payment/effect");
        foreach (var install in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { install.Exists ? "collMod" : "create", install.Name }, { "validator", install.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (install.CreateIndex) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(install.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(PaymentDraftSchemas.Key(install.Name),
                    new CreateIndexOptions { Unique = true, Name = "ux_payment_declaration_revision" }), cancellationToken: ct);
            }
        }
    }
    private static InvalidOperationException Invalid(string reason) => new("Payment declaration migration preflight failed: " + reason);
}
