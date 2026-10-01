using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentReviewExplanationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 46;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); var name = PaymentReviewActionRecords.Versions;
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) || options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error") throw Bad();
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority); var old = PaymentReviewActionSchemas.Validator();
        if (await rows.Find(new BsonDocument("$nor", new BsonArray { old })).Limit(1).AnyAsync(ct)) throw Bad();
        var next = PaymentManualReviewMigration.ExtendValidator(options.GetValue("validator", new BsonDocument()).AsBsonDocument, old, PaymentReviewExplanationSchemas.Validator(), name);
        var key = PaymentReviewActionSchemas.Key(); var indexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !index["key"].Equals(key)) ||
            !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1))) ||
            !indexes.Any(index => index["key"].Equals(key) && index.GetValue("unique", false).AsBoolean)) throw Bad();
        var largest = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Limit(1).SingleOrDefaultAsync(ct); var maximum = largest?["_id"].AsInt64 ?? 0;
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is null ? maximum != 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 == long.MaxValue) throw Bad();
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            var reader = new PaymentReviewActionRecords(database);
            // ponytail: full histories are rechecked per event; batch verified sources if measured migration volume requires it.
            using var actions = await rows.Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await actions.MoveNextAsync(ct)) foreach (var row in actions.Current) {
                await lease.RenewAsync(ct); _ = await reader.LatestAsync(tx, row["declarationId"].AsInt64, null, ct);
            }
            var prefix = new BsonRegularExpression(@"^billing\.payment\.review\.manage(?:[.:]|$)");
            foreach (var (table, field, resultField) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
                using var effects = await database.GetCollection<BsonDocument>(table).Find(tx, new BsonDocument(field, prefix)).ToCursorAsync(ct);
                while (await effects.MoveNextAsync(ct)) foreach (var effect in effects.Current) {
                    await lease.RenewAsync(ct);
                    if (effect[field] != PaymentReviewActionRecords.Operation || !effect.TryGetValue(resultField, out var result) || !result.IsBsonDocument ||
                        !result.AsBsonDocument.TryGetValue("actionId", out var actionId) || !actionId.IsInt64 || actionId.AsInt64 <= 0) throw Bad();
                    var action = await rows.Find(tx, new BsonDocument("_id", actionId)).SingleOrDefaultAsync(ct) ?? throw Bad();
                    _ = await reader.LatestAsync(tx, action["declarationId"].AsInt64, null, ct);
                    if (table == "auditEvents" && !InvoiceRecords.Equal(effect, PaymentReviewActionRecords.Audit(action)) ||
                        table == "idempotencyRecords" && (effect.GetValue("actorId", BsonNull.Value) != action["actorId"] || effect.GetValue("key", "") != action["requestKey"]) ||
                        table == "outboxEvents" && effect.GetValue("sourceKey", "") != PaymentReviewActionRecords.Operation + ":" + actionId.AsInt64) throw Bad();
                    if (table == "outboxEvents") {
                        _ = await PaymentReviewNotifications.SourceAsync(database, tx, effect, ct);
                        _ = await PaymentReviewNotifications.ReadAsync(database, tx, action, ct);
                    }
                }
            }
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", next }, { "validationLevel", "strict" },
            { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static InvalidOperationException Bad() => new("Payment public explanation migration preflight failed");
}
