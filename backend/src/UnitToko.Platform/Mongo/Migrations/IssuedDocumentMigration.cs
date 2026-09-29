using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Install only the new generated-document owner. Existing evidence, dispatch history,
/// document numbering and migrations 1–24 remain unchanged. No historical copy is invented.</summary>
internal sealed class IssuedDocumentMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 25;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        const string name = IssuedDocumentStore.Collection; await lease.RenewAsync(ct);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted document data");
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
            options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
        var required = IssuedDocumentSchemas.Validator(); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
        var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        var key = IssuedDocumentSchemas.Key(); const string indexName = "ux_issued_document_source";
        if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key))) throw Invalid("retention or extra uniqueness");
        if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
        var matching = indexes.Where(index => index["name"] == indexName || index["key"].Equals(key)).ToArray();
        if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Invalid("required index");
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
        var prefix = new BsonRegularExpression(@"^fulfillment\.surat-jalan(?:[.:]|$)");
        foreach (var (collection, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("operation", prefix), new BsonDocument("kind", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) }))
        }) {
            await lease.RenewAsync(ct);
            if (await database.GetCollection<BsonDocument>(collection).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted document effects");
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { definition is null ? "create" : "collMod", name }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        if (matching.Length == 0) await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
            new CreateIndexOptions { Unique = true, Name = indexName }), cancellationToken: ct);
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") ||
        index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static InvalidOperationException Invalid(string reason) => new("Issued document migration preflight failed: " + reason);
}
