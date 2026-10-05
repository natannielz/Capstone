using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

// Integration seam: the existing M86 class declaration must become partial. Its
// retained image/admission preflight methods stay byte-semantic unchanged. This
// method is read-only and does not rerun M86 Apply or create a fake migration lease.
internal sealed partial class PdfDocumentExtractionMigration
{
    internal async Task RequireSupplementPredecessorAsync(MigrationLease lease, CancellationToken ct)
    {
        await RequirePredecessorsAsync(lease, ct);
        await RequireHistoriesAsync(lease, ct);
        await RequireCountersAsync(lease, ct);
        await PdfDocumentExtractionRecords.RequireStorageAsync(database, ct);
        foreach (var name in PdfDocumentExtractionSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await DefinitionAsync(name, ct) ?? throw Invalid();
            await RequireRowsAsync(name, definition, ct);
            var last = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var maximum = last is null ? 0L : last["_id"].IsInt64 && last["_id"].AsInt64 is > 0 and < long.MaxValue - 1 ? last["_id"].AsInt64 : throw Invalid();
            var allocator = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (allocator is null ? maximum > 0 : !allocator.TryGetValue("value", out var value) || !value.IsInt64 ||
                value.AsInt64 < maximum || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        }
        // Dormant Payment vocabulary was never permission for a primary PDF job.
        if (await Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.PaymentJobKind)).Limit(1).AnyAsync(ct)) throw Invalid();
        if (database.Client is not MongoClient client) throw Invalid();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database); var clock = new SystemClock();
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog,
            new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var supplier = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
        var extraction = new PdfDocumentExtractionRecords(database);
        async Task Each(string name, BsonDocument filter, Func<IClientSessionHandle, BsonDocument, Task> check) =>
            await EvidenceMigration.EachAsync(Rows(name), filter, async row => {
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
                tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                await check(tx, row); await tx.CommitTransactionAsync(ct);
            }, lease, ct);
        async Task<BsonDocument> Job(IClientSessionHandle tx, BsonValue id) {
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (job["kind"] != PdfDocumentExtractionContract.SupplierJobKind) throw Invalid();
            var source = await supplier.SubmissionAsync(tx, job["payload"]["submissionId"].AsInt64, ct);
            var original = await supplier.OriginalAsync(tx, source, ct) ?? throw Invalid();
            if (job["sourceKey"] != SupplierDocumentBson.DocumentOcrKey(source) ||
                !job["payload"].Equals(SupplierDocumentBson.DocumentOcrPayload(source, original)) || job["createdAt"] != source["recordedAt"]) throw Invalid();
            return job;
        }
        static void SameJob(BsonDocument row, BsonDocument job) {
            if (row["sourceKind"] != "SupplierInvoice" || row["purpose"] != "SupplierInvoice" || row["outboxId"] != job["_id"] ||
                job["sourceKey"] != PdfDocumentExtractionRecords.SourceKey("SupplierInvoice", row["binding"]["submissionId"].AsInt64) ||
                !job["payload"].Equals(PdfDocumentExtractionRecords.Payload("SupplierInvoice", row["binding"].AsBsonDocument,
                    PaymentSupplementBson.Publication(row["original"].AsBsonDocument))) ||
                job["createdAt"].CompareTo(row["createdAt"]) > 0 || row["claimAttempt"].AsInt32 > job["attempts"].AsInt32) throw Invalid();
        }
        await Each(PdfDocumentExtractionRecords.ProcessingReads, new(), async (tx, row) => {
            var read = await extraction.ReadAuditAsync(tx, row["_id"].AsInt64, ct); SameJob(read, await Job(tx, row["outboxId"]));
        });
        await Each("outboxEvents", new("kind", PdfDocumentExtractionContract.SupplierJobKind), async (tx, raw) => {
            var job = await Job(tx, raw["_id"]); var result = await extraction.ResultAsync(tx, job, ct);
            if ((job["status"] == "completed") != (result is not null)) throw Invalid();
        });
        foreach (var name in new[] { PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits })
            await Each(name, new(), async (tx, row) => {
                var job = await Job(tx, row["outboxId"]); var result = await extraction.ResultAsync(tx, job, ct) ?? throw Invalid();
                if (name == PdfDocumentExtractionRecords.Results ? !result.Equals(row) : !row.Equals(PdfDocumentExtractionRecords.Audit(result))) throw Invalid();
            });
        await Each(PdfDocumentExtractionRecords.Failures, new(), async (tx, row) => {
            var failed = await extraction.HistoricalFailureAsync(tx, row["_id"].AsInt64, ct);
            SameJob(failed.Failure, await Job(tx, row["outboxId"]));
        });
        await Each(PdfDocumentExtractionRecords.Reads, new(), async (tx, row) => {
            var job = await Job(tx, row["outboxId"]);
            var submitted = await supplier.SubmissionAsync(tx, job["payload"]["submissionId"].AsInt64, ct);
            var result = await extraction.ResultAsync(tx, job, ct);
            if (row["sourceKind"] != "SupplierInvoice" || row["sourceId"] != submitted["draftId"] || row["submissionId"] != submitted["_id"] ||
                !row["reference"].Equals(submitted["evidence"]) || row["sourceRevision"].AsInt64 < 1 ||
                row["resultId"].IsBsonNull != row["resultHash"].IsBsonNull || (row["state"] == "succeeded") != !row["resultId"].IsBsonNull ||
                !row["resultId"].IsBsonNull && (result is null || row["resultId"] != result["_id"] || row["resultHash"] != result["resultHash"] ||
                    result["createdAt"].CompareTo(row["createdAt"]) > 0)) throw Invalid();
        });
        // Every real primary image source remains in its original namespace.
        await Each("paymentSubmissions", new(), async (tx, row) => { _ = await new PaymentSubmissionRecords(database).ReadAsync(tx, row["_id"].AsInt64, ct); });
    }
}
