using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed partial class ProcurementInspectionMigration
{
    // Reuse the accepted M69 retained-source proof without replaying its DDL or empty-new-owner checks.
    internal Task PreflightRetainedReceivingForSupplierDocumentAsync(MigrationLease lease, CancellationToken ct) => PreflightReceivingAsync(lease, ct);
}

internal sealed partial class SupplierDocumentMigration
{
    private async Task PreflightInspectionsAsync(MigrationLease lease, CancellationToken ct) {
        if (database.Client is not MongoClient client) throw Bad();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var suppliers = new SupplierService(database, unit, ids, clock, catalog);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, suppliers);
        var records = new ProcurementInspectionRecords(database, unit, clock, catalog, orders);
        using var tx = await client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try {
            using var cursor = await database.GetCollection<BsonDocument>(ProcurementReceivingBson.Intakes).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var intake in cursor.Current) {
                await lease.RenewAsync(ct);
                _ = await records.HistoryAsync(tx, intake["noticeId"].AsInt64, intake["_id"].AsInt64, ct);
            }
            await records.OrphansAsync(tx, ct);
            foreach (var (operation, collection) in new[] { (ProcurementInspectionBson.DraftOperation, ProcurementInspectionBson.Drafts), (ProcurementInspectionBson.SubmitOperation, ProcurementInspectionBson.Submissions) }) {
                var count = await database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct);
                foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                    if (await database.GetCollection<BsonDocument>(table).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
                if (await database.GetCollection<BsonDocument>("outboxEvents").CountDocumentsAsync(tx, new BsonDocument("$or", new BsonArray {
                    new BsonDocument("kind", operation), new BsonDocument("sourceKey", new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(operation) + @"(?:[.:]|$)")) }), cancellationToken: ct) != count) throw Bad();
            }
            var prefix = new BsonRegularExpression(@"^procurement\.inspection(?:[.:]|$)");
            foreach (var (table, field) in new[] { ("outboxEvents", "kind"), ("idempotencyRecords", "operation"), ("auditEvents", "kind") })
                if (await database.GetCollection<BsonDocument>(table).Find(tx, new BsonDocument(field, new BsonDocument {
                    { "$regex", prefix }, { "$nin", new BsonArray { ProcurementInspectionBson.DraftOperation, ProcurementInspectionBson.SubmitOperation } } })).AnyAsync(ct)) throw Bad();
            if (await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, new BsonDocument { { "sourceKey", prefix },
                { "kind", new BsonDocument("$nin", new BsonArray { ProcurementInspectionBson.DraftOperation, ProcurementInspectionBson.SubmitOperation }) } }).AnyAsync(ct) ||
                await database.GetCollection<BsonDocument>("auditEvents").Find(tx, new BsonDocument("operation", prefix)).AnyAsync(ct) ||
                await database.GetCollection<BsonDocument>("journalEntries").Find(tx, new BsonDocument("source.namespace", prefix)).AnyAsync(ct)) throw Bad();
            await tx.AbortTransactionAsync(ct);
        } catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
}
