using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentReplacementMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 44;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); var name = PaymentDraftBson.Versions;
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad(name);
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) || options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error") throw Bad(name);
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        if (await rows.Find(new BsonDocument("$nor", new BsonArray { PaymentDraftSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Bad(name);
        BsonDocument extended;
        try { extended = PaymentManualReviewMigration.ExtendValidator(options.GetValue("validator", new BsonDocument()).AsBsonDocument, PaymentDraftSchemas.Validator(name), PaymentReplacementSchemas.Validator(), name); }
        catch (InvalidOperationException) { throw Bad(name); }
        var indexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct); var key = PaymentReplacementSchemas.Key(); var partial = PaymentReplacementSchemas.Partial();
        var ordinary = PaymentDraftSchemas.Key(name);
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(ordinary) && !index["key"].Equals(key)) ||
            !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1))) ||
            !indexes.Any(index => index["key"].Equals(ordinary) && index.GetValue("unique", false).AsBoolean && !index.Contains("partialFilterExpression"))) throw Bad(name);
        var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == PaymentReplacementSchemas.IndexName).ToArray();
        if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean || !index.GetValue("partialFilterExpression", BsonNull.Value).Equals(partial)) ||
            indexes.Any(index => index.Contains("partialFilterExpression") && !index["key"].Equals(key))) throw Bad(name);
        var prefix = new BsonRegularExpression(@"^billing\.payment\.replacement(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad(table);
        foreach (var table in PaymentDraftSchemas.Collections) {
            var largest = await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Limit(1).SingleOrDefaultAsync(ct);
            var maximum = largest?["_id"].AsInt64 ?? 0;
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", table)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum != 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 == long.MaxValue) throw Bad(table);
        }
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            var reader = new PaymentDraftRecords(database);
            using var cursor = await rows.Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) _ = await reader.VersionAsync(tx, row["_id"].AsInt64, ct);
            using var heads = await database.GetCollection<BsonDocument>(PaymentDraftBson.Heads).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await heads.MoveNextAsync(ct)) foreach (var head in heads.Current) _ = await reader.HeadAsync(tx, head["recordedBy"].AsInt64, head["_id"].AsInt64, ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", extended }, { "validationLevel", "strict" },
            { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        if (matching.Length == 0) await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
            new CreateIndexOptions<BsonDocument> { Name = PaymentReplacementSchemas.IndexName, Unique = true, PartialFilterExpression = partial }), cancellationToken: ct);
    }
    private static InvalidOperationException Bad(string name) => new("Payment replacement migration preflight failed: " + name);
}
