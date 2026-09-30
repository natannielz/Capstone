using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentReviewActionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 43;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); var name = PaymentReviewActionRecords.Versions;
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(name);
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
        var key = PaymentReviewActionSchemas.Key(); var indexName = "ux_" + name;
        var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
            definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad(name);
        var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == indexName).ToArray();
        if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad(name);
        var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Bad(name);
        var prefix = new BsonRegularExpression(@"^billing\.payment\.review\.manage(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad(table);
        var required = PaymentReviewActionSchemas.Validator(); var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var validator = current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new BsonDocument("$and", new BsonArray { current, required });
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { definition is null ? "create" : "collMod", name }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        if (matching.Length == 0) await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Name = indexName, Unique = true }), cancellationToken: ct);
    }
    private static InvalidOperationException Bad(string name) => new("Payment review action migration preflight failed: " + name);
}
