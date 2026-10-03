using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed partial class SupplierClaimMigration
{
    private async Task PreflightSupplierDocumentsAsync(MigrationLease lease, Dictionary<string, long> maxima, CancellationToken ct) {
        foreach (var name in SupplierDocumentSchemas.Collections.Concat(SupplierDocumentProcessingSchemas.Collections)) {
            await lease.RenewAsync(ct);
            var owner = SupplierDocumentSchemas.Collections.Contains(name);
            var validator = owner ? SupplierDocumentSchemas.Validator(name) : SupplierDocumentProcessingSchemas.Validator(name);
            await InventoryRepository.RequireStorageAsync(database, name, validator, owner ? SupplierDocumentSchemas.Keys(name) : [SupplierDocumentProcessingSchemas.Key(name)], ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            var maximum = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            if (name is B.Heads or B.InspectionHeads) {
                if (await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync(ct)) throw Bad();
            } else {
                await CounterAsync(name, ct);
                if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
                var high = maximum is null ? 0 : maximum["_id"].AsInt64;
                var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
                if (high > 0 && (counter is null || counter["value"].AsInt64 < high)) throw Bad();
                maxima.Add(name, high);
            }
        }
        if (database.Client is not MongoClient client) throw Bad();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog,
            new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var records = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
        var documents = new SupplierDocumentService(database, unit, ids, clock, catalog, orders,
            new(database, unit, ids, clock, catalog, orders, new(database, unit, ids, clock, catalog)));
        var source = new SupplierDocumentOcrSource(documents); var extraction = new SupplierDocumentExtractionRecords(database);
        await ProcurementInspectionEvidenceAdmissionGraph.ValidateSupplierDocumentAsync(database, lease, ct);
        await SupplierDocumentEvidenceSchemas.RequireHistoriesAsync(database, lease, (tx, id, token) => records.SubmissionAsync(tx, id, token), ct);
        using var tx = await client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try {
            IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
            async Task Each(string name, Func<BsonDocument, Task> action, BsonDocument? filter = null) {
                using var cursor = await Rows(name).Find(tx, filter ?? new BsonDocument()).ToCursorAsync(ct);
                while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) { await lease.RenewAsync(ct); await action(row); }
            }
            await Each(B.Drafts, async row => {
                var history = await records.HistoryAsync(tx, row["_id"].AsInt64, ct);
                _ = await records.RequireSourceCapacitiesAsync(tx, history, ct);
            });
            await records.OrphansAsync(tx, ct);
            foreach (var (table, operation) in new[] { (B.Drafts, B.DraftOperation), (B.Submissions, B.SubmitOperation), (B.Matches, B.MatchOperation) }) {
                var count = await Rows(table).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct);
                foreach (var (effects, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                    if (await Rows(effects).CountDocumentsAsync(tx, new BsonDocument(field, operation), cancellationToken: ct) != count) throw Bad();
            }
            var prefix = new BsonRegularExpression(@"^procurement\.supplierdocument(?:[.:]|$)");
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                if (await Rows(table).Find(tx, new BsonDocument(field, new BsonDocument { { "$regex", prefix }, { "$nin", new BsonArray { B.DraftOperation, B.SubmitOperation, B.MatchOperation } } })).AnyAsync(ct)) throw Bad();
            if (await Rows("outboxEvents").Find(tx, new BsonDocument { { "sourceKey", prefix }, { "kind", new BsonDocument("$nin", new BsonArray { B.DraftOperation, B.SubmitOperation, B.MatchOperation }) } }).AnyAsync(ct) ||
                await Rows("journalEntries").Find(tx, new BsonDocument("source.namespace", prefix)).AnyAsync(ct) ||
                await Rows("auditEvents").Find(tx, new BsonDocument("operation", prefix)).AnyAsync(ct)) throw Bad();
            async Task<(BsonDocument Job, SupplierDocumentOcrSourceSnapshot Source, BsonDocument? Result)> OcrAsync(BsonValue id) {
                var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Bad();
                var owning = await source.RequireAsync(tx, job, ct); var result = await extraction.ResultAsync(tx, job, ct);
                if (job["status"] == "completed" && result is null) throw Bad();
                if (result is not null) _ = await extraction.RequireResultAsync(tx, job, owning, result["_id"].AsInt64, ct);
                return (job, owning, result);
            }
            await Each("outboxEvents", async job => { _ = await OcrAsync(job["_id"]); }, new BsonDocument("$or", new BsonArray {
                new BsonDocument("kind", SupplierDocumentExtractionRecords.Kind), new BsonDocument("sourceKey", new BsonRegularExpression(@"^supplierdocument\.ocr(?:[.:]|$)")),
                new BsonDocument("processingReceipt.supplierDocumentExtraction", new BsonDocument("$exists", true)) }));
            await Each("supplierDocumentProcessingReadAudits", async row => {
                var read = await extraction.ReadAuditAsync(tx, row["_id"].AsInt64, ct); var original = await OcrAsync(read["outboxId"]);
                await RequireOcrBindingAsync(tx, read["binding"].AsBsonDocument, original.Source, ct);
            });
            await Each("supplierDocumentProcessingFailures", async row => {
                var failure = await extraction.HistoricalFailureAsync(tx, row["_id"].AsInt64, ct); _ = await OcrAsync(failure.Read["outboxId"]);
            });
            foreach (var table in new[] { "supplierDocumentExtractionResults", "supplierDocumentExtractionAudits", "supplierDocumentExtractionReadAudits" })
                await Each(table, async row => {
                    var original = await OcrAsync(row["outboxId"]);
                    if (table != "supplierDocumentExtractionReadAudits") {
                        if (original.Result is null || (table == "supplierDocumentExtractionResults" ? row["_id"] : row["resultId"]) != original.Result["_id"]) throw Bad();
                    } else {
                        if (row["submissionId"] != original.Source.SubmissionId || row["draftId"] != original.Source.DraftId ||
                            row["reference"] != UnitToko.Platform.Billing.PaymentSubmissionBson.Reference(original.Source.Evidence) ||
                            !row["resultId"].IsBsonNull && (original.Result is null || row["resultId"] != original.Result["_id"] || row["resultHash"] != original.Result["resultHash"])) throw Bad();
                    }
                });
            await tx.AbortTransactionAsync(ct);
        } catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
    private async Task RequireOcrBindingAsync(IClientSessionHandle tx, BsonDocument binding, SupplierDocumentOcrSourceSnapshot source, CancellationToken ct) {
        foreach (var (field, value) in new (string, BsonValue)[] { ("submissionId", source.SubmissionId), ("draftId", source.DraftId), ("draftRevision", source.DraftRevision),
            ("reservationId", source.ReservationId), ("pinId", source.PinId), ("submissionHash", source.SubmissionHash), ("draftHash", source.DraftHash),
            ("reference", UnitToko.Platform.Billing.PaymentSubmissionBson.Reference(source.Evidence)) }) if (!binding[field].Equals(value)) throw Bad();
        var pin = await EvidencePinRecords.ReadAsync(database, tx, source.PinId, ct);
        var version = await database.GetCollection<BsonDocument>("documentVersions").Find(tx, new BsonDocument("_id", binding["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Bad();
        foreach (var field in new[] { "pinHash", "versionRowId", "storeId", "generation", "validationHash", "lifecycleRevision" }) if (binding[field] != pin[field]) throw Bad();
        foreach (var field in new[] { "storeId", "generation", "length" }) if (binding[field] != version[field]) throw Bad();
        if (version["reservationId"] != source.ReservationId || version["sha256"] != source.Evidence.Sha256) throw Bad();
        foreach (var (table, id, revision) in new[] { ("evidenceReadPolicies", "readPolicyId", "readPolicyRevision"), ("evidenceValidationPolicies", "validationPolicyId", "validationPolicyRevision") }) {
            var policy = await database.GetCollection<BsonDocument>(table).Find(tx, new BsonDocument("_id", binding[id])).SingleOrDefaultAsync(ct) ?? throw Bad();
            if (policy["storeId"] != binding["storeId"] || policy["revision"].AsInt64 < binding[revision].AsInt64) throw Bad();
        }
    }
}
