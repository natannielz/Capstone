using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentNotificationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 45;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); const string name = "outboxEvents";
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) || options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error") throw Bad();
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        PaymentEvidenceMigration.RequireIndexes(name, await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct));
        var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        if (!InventorySchemas.Contains(current, EvidenceSchemas.Validator(name)) || !InventorySchemas.Contains(current, EvidenceValidationSchemas.OutboxValidator()) ||
            !InventorySchemas.Contains(current, DocumentProcessingSchemas.OutboxValidator())) throw Bad();
        var required = PaymentNotificationSchemas.OutboxValidator();
        var next = InventorySchemas.Contains(current, required) ? current : new BsonDocument("$and", new BsonArray { current, required });
        if (await rows.Find(new BsonDocument("$nor", new BsonArray { next })).Limit(1).AnyAsync(ct) ||
            await rows.Find(new BsonDocument("notificationReceipt", new BsonDocument("$exists", true))).Limit(1).AnyAsync(ct)) throw Bad();
        // No prior consumer is accepted. Preserve pending producer events; do not invent delivery receipts for earlier processing.
        // ponytail: full source history per event is quadratic for a very large single declaration; use verified history checkpoints only after measuring the release workload.
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            using var cursor = await rows.Find(tx, new BsonDocument("kind", PaymentReviewActionRecords.Operation)).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var job in cursor.Current) {
                await lease.RenewAsync(ct);
                if (job.GetValue("status", "") != "pending" || job.GetValue("attempts", -1) != 0 || job.Contains("leaseToken") || job.Contains("completedAt")) throw Bad();
                _ = await PaymentReviewNotifications.SourceAsync(database, tx, job, ct);
            }
            using var actions = await database.GetCollection<BsonDocument>(PaymentReviewActionRecords.Versions).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await actions.MoveNextAsync(ct)) foreach (var action in actions.Current) {
                await lease.RenewAsync(ct);
                if (!action.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 <= 0) throw Bad();
                var job = await rows.Find(tx, new BsonDocument("sourceKey", PaymentReviewActionRecords.Operation + ":" + id.AsInt64)).SingleOrDefaultAsync(ct) ?? throw Bad();
                _ = await PaymentReviewNotifications.SourceAsync(database, tx, job, ct);
            }
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", next }, { "validationLevel", "strict" },
            { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private static InvalidOperationException Bad() => new("Payment in-app notification migration preflight failed");
}
