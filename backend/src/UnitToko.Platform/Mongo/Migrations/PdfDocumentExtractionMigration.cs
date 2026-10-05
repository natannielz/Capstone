using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Procurement;
using UnitToko.Platform.Reporting;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Prospective PDF OCR owner. Retained image results and approved original PDF proofs are never relabelled.</summary>
internal sealed partial class PdfDocumentExtractionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 86;
    private static readonly string[] Owners = [PdfDocumentExtractionRecords.Policies, PdfDocumentExtractionRecords.ProcessingReads,
        PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.Failures, PdfDocumentExtractionRecords.Reads];
    private static readonly string[] Kinds = ["payment.document-ocr.requested", "supplierdocument.document-ocr.requested"];
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("PDF document extraction migration found incompatible retained storage");

    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();

        var changes = new List<(string Name, bool Exists, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in Owners) {
            await lease.RenewAsync(ct);
            var definition = await DefinitionAsync(name, ct);
            if (definition is not null && !SafeDefinition(definition, PdfDocumentExtractionSchemas.Validator(name))) throw Invalid();
            // No pre-86 writer owns even an inactive policy, a read attempt or a consumed allocator in this namespace.
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 != 0L)) throw Invalid();
            List<BsonDocument> indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = PdfDocumentExtractionSchemas.Keys(name);
            if (definition is not null && !SafeIndexes(indexes, keys)) throw Invalid();
            changes.Add((name, definition is not null, keys.Where(key => !indexes.Any(row => Matches(row, key))).ToArray()));
        }

        try {
            await RequirePredecessorsAsync(lease, ct);
            await RequireHistoriesAsync(lease, ct);
            await RequireCountersAsync(lease, ct);
        } catch (Exception error) when (error is DomainValidationException or PersistenceConflictException or InvalidOperationException) { throw Invalid(); }

        var outbox = await DefinitionAsync("outboxEvents", ct) ?? throw Invalid();
        var previous = outbox["options"]["validator"].AsBsonDocument;
        if (!PdfEvidenceAdmissionSchemas.SafeDefinition(outbox) ||
            !PdfDocumentExtractionSchemas.SupportsOutbox(previous, PdfDocumentExtractionSchemas.PreviousOutboxValidator())) throw Invalid();
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await Rows("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
        var next = PdfDocumentExtractionSchemas.ExtendOutbox(previous);
        if (await Rows("outboxEvents").Find(new BsonDocument("$or", new BsonArray {
                new BsonDocument("kind", new BsonDocument("$in", new BsonArray(Kinds))),
                new BsonDocument("processingReceipt.pdfDocumentExtraction", new BsonDocument("$exists", true)) })).Limit(1).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { previous })).Limit(1).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { next })).Limit(1).AnyAsync(ct)) throw Invalid();

        // Every retained source, current definition, history, counter and candidate passed before the first DDL.
        // Interrupted installation only creates missing empty owners/indexes; stronger controls and UUIDs survive.
        foreach (var change in changes) {
            await lease.RenewAsync(ct);
            if (!change.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", change.Name }, { "validator", PdfDocumentExtractionSchemas.Validator(change.Name) },
                { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in change.Missing)
                if (key.Key != new BsonDocument("_id", 1))
                    await Rows(change.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                        new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        var currentOutbox = await DefinitionAsync("outboxEvents", ct) ?? throw Invalid();
        if (currentOutbox["options"]["validator"] != previous) throw Invalid();
        if (next != previous) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", "outboxEvents" }, { "validator", next }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        await PdfDocumentExtractionRecords.RequireStorageAsync(database, ct);
    }

    private async Task RequirePredecessorsAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        await SupplierDocumentEvidenceSchemas.RequireStorageAsync(database, ct);
        await DocumentProcessingStorage.RequireAsync(database, ct);
        await AccountContactStorage.RequireAsync(database, ct);
        await AccountingReportRecords.RequireStorageAsync(database, ct);
        // The shared allocator remains the strict applied foundation owner; migration never repairs it.
        await RequireOwnerAsync("idCounters", new BsonDocument("$jsonSchema", new BsonDocument {
            { "bsonType", "object" }, { "required", new BsonArray { "value" } },
            { "properties", new BsonDocument("value", new BsonDocument("bsonType", "long")) } }), [("", new("_id", 1), true)], lease, ct);
        foreach (var name in EvidenceSchemas.Collections.Concat(EvidenceAccessSchemas.Collections).Concat(PaymentSubmissionSchemas.Collections).Distinct()) {
            await lease.RenewAsync(ct);
            var definition = await DefinitionAsync(name, ct) ?? throw Invalid();
            if (!PdfEvidenceAdmissionSchemas.SafeDefinition(definition)) throw Invalid();
            await RequireRowsAsync(name, definition, ct);
        }
        foreach (var name in DocumentProcessingSchemas.Collections)
            await RequireOwnerAsync(name, DocumentProcessingSchemas.Validator(name), [("", DocumentProcessingSchemas.Key(name), true)], lease, ct);
        foreach (var name in SupplierDocumentProcessingSchemas.Collections)
            await RequireOwnerAsync(name, SupplierDocumentProcessingSchemas.Validator(name), [("", SupplierDocumentProcessingSchemas.Key(name), true)], lease, ct);
        foreach (var name in EvidenceValidationSchemas.Collections)
            await RequireOwnerAsync(name, EvidenceValidationSchemas.Validator(name), [("", EvidenceValidationSchemas.Key(name), true)], lease, ct);
        foreach (var name in ProcurementInspectionEvidenceSchemas.NewCollections)
            await RequireOwnerAsync(name, ProcurementInspectionEvidenceSchemas.Validator(name), [("", ProcurementInspectionEvidenceSchemas.Key(name), true)], lease, ct);
        foreach (var name in PdfEvidenceValidationSchemas.Collections)
            await RequireOwnerAsync(name, PdfEvidenceValidationSchemas.Validator(name), [("", PdfEvidenceValidationSchemas.Key(name), true)], lease, ct);
        foreach (var name in EvidenceImageRevalidationSchemas.Collections)
            await RequireOwnerAsync(name, EvidenceImageRevalidationSchemas.Validator(name), EvidenceImageRevalidationSchemas.Keys(name), lease, ct);
        foreach (var name in AccountContactSchemas.Collections)
            await RequireOwnerAsync(name, AccountContactSchemas.Validator(name), AccountContactSchemas.Keys(name), lease, ct);
        await RequireOwnerAsync(AccountingReportRecords.Events, AccountingReportSchemas.Validator(), AccountingReportSchemas.Keys, lease, ct);
    }

    private async Task RequireHistoriesAsync(MigrationLease lease, CancellationToken ct)
    {
        if (database.Client is not MongoClient client) throw Invalid();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database); var clock = new SystemClock();
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog,
            new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var supplier = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
        var documents = new EvidenceService(database, unit, ids, clock);
        await SupplierDocumentEvidenceSchemas.RequireHistoriesAsync(database, lease, (tx, id, token) => supplier.SubmissionAsync(tx, id, token), ct);

        async Task Each(string name, BsonDocument filter, Func<IClientSessionHandle, BsonDocument, Task> check) =>
            await EvidenceMigration.EachAsync(Rows(name), filter, async row => {
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
                tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                await check(tx, row); await tx.CommitTransactionAsync(ct);
            }, lease, ct);
        async Task<BsonDocument> Job(IClientSessionHandle tx, BsonValue id, string kind) {
            var row = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (row["kind"] != kind) throw Invalid(); return row;
        }
        async Task OriginalBinding(IClientSessionHandle tx, BsonDocument read, BsonDocument job) {
            var binding = read["binding"];
            var pin = await EvidencePinRecords.ReadAsync(database, tx, binding["pinId"].AsInt64, ct);
            var reservation = await Rows("evidenceUploadReservations").Find(tx, new BsonDocument("_id", binding["reservationId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            var version = await Rows("documentVersions").Find(tx, new BsonDocument("_id", binding["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            var validation = await Rows("evidenceValidationResults").Find(tx, new BsonDocument("_id", binding["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            var lifecycle = await Rows("evidenceLifecycles").Find(tx, new BsonDocument("_id", binding["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            foreach (var field in new[] { "reservationId", "pinHash", "reference", "versionRowId", "storeId", "generation", "validationHash", "lifecycleRevision" })
                if (binding[field] != pin[field]) throw Invalid();
            if (reservation["versionRowId"] != version["_id"] || binding["length"] != version["length"] ||
                version["storeId"] != binding["storeId"] || version["generation"] != binding["generation"] ||
                validation["resultHash"] != binding["validationHash"] || lifecycle["revision"] != binding["lifecycleRevision"] ||
                job["createdAt"].CompareTo(read["createdAt"]) > 0 || pin["createdAt"].CompareTo(read["createdAt"]) > 0) throw Invalid();
            foreach (var (table, id, revision) in new[] {
                ("evidenceReadPolicies", "readPolicyId", "readPolicyRevision"), ("evidenceValidationPolicies", "validationPolicyId", "validationPolicyRevision") }) {
                var policy = await Rows(table).Find(tx, new BsonDocument("_id", binding[id])).SingleOrDefaultAsync(ct) ?? throw Invalid();
                if (policy["storeId"] != binding["storeId"] || policy["revision"].AsInt64 < binding[revision].AsInt64) throw Invalid();
            }
            if (validation["policyId"] != binding["validationPolicyId"] || validation["policyRevision"] != binding["validationPolicyRevision"]) throw Invalid();
        }
        var paymentExtraction = new ExtractionRecords(database);
        await Each("documentProcessingReadAudits", new(), async (tx, row) => {
            var read = await paymentExtraction.ReadAuditAsync(tx, row["_id"].AsInt64, ct);
            var job = await Job(tx, row["outboxId"], "payment.ocr.requested");
            var source = await new UnitToko.Platform.Billing.PaymentOcrSource(database).RequireAsync(tx, job, ct);
            foreach (var (field, expected) in new (string, BsonValue)[] {
                ("submissionId", source.SubmissionId), ("declarationId", source.DeclarationId), ("declarationVersionId", source.DeclarationVersionId),
                ("declarationRevision", source.DeclarationRevision), ("reviewRevision", source.ReviewRevision), ("submissionHash", source.SubmissionHash),
                ("reservationId", source.ReservationId), ("pinId", source.PinId) })
                if (read["binding"][field] != expected) throw Invalid();
            await OriginalBinding(tx, read, job);
        });
        await Each("documentExtractionReadAudits", new(), async (tx, row) => {
            var job = await Job(tx, row["outboxId"], "payment.ocr.requested");
            var source = await new UnitToko.Platform.Billing.PaymentOcrSource(database).RequireAsync(tx, job, ct);
            if (row["submissionId"] != source.SubmissionId || row["reference"] != job["payload"]["evidence"] ||
                row["resultId"].IsBsonNull != row["resultHash"].IsBsonNull || (row["state"] == "succeeded") != !row["resultId"].IsBsonNull) throw Invalid();
        });
        // The M83 result owner validates the actual original, complete page proof, matching audit and exclusive receipt.
        await Each(PdfEvidenceValidationRecords.Results, new(), async (tx, row) => {
            await documents.ValidateHistoricalAdmissionAsync(tx, row["source"]["reservationId"].AsInt64, ct);
            var reservation = await Rows("evidenceUploadReservations").Find(tx, new BsonDocument("_id", row["source"]["reservationId"])).SingleAsync(ct);
            if (reservation["versionRowId"] != row["_id"]) throw Invalid();
        });
        await Each(PdfEvidenceValidationRecords.Audits, new(), async (tx, row) => {
            var result = await Rows(PdfEvidenceValidationRecords.Results).Find(tx, new BsonDocument("_id", row["versionRowId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (!row.Equals(PdfEvidenceValidationRecords.Audit(result))) throw Invalid();
        });
        await Each("outboxEvents", new("kind", PdfEvidenceValidationRecords.Kind), async (tx, job) => {
            await documents.ValidateHistoricalAdmissionAsync(tx, job["payload"]["reservationId"].AsInt64, ct);
            var reservation = await Rows("evidenceUploadReservations").Find(tx, new BsonDocument("_id", job["payload"]["reservationId"])).SingleAsync(ct);
            if (reservation["versionRowId"] != job["payload"]["versionRowId"] ||
                job["sourceKey"] != $"evidence.quarantined:{reservation["versionRowId"].AsInt64}:1") throw Invalid();
            if (job["status"] == "completed" && !await Rows(PdfEvidenceValidationRecords.Results).Find(tx, new BsonDocument("outboxId", job["_id"])).AnyAsync(ct)) throw Invalid();
        });

        var extraction = new SupplierDocumentExtractionRecords(database);
        async Task<BsonDocument> Submission(IClientSessionHandle tx, BsonDocument job) => await supplier.SubmissionAsync(tx, job["payload"]["submissionId"].AsInt64, ct);
        await Each("outboxEvents", new("kind", SupplierDocumentExtractionRecords.Kind), async (tx, job) => {
            _ = await Submission(tx, job); var result = await extraction.ResultAsync(tx, job, ct);
            if (job["status"] == "completed" && result is null) throw Invalid();
        });
        await Each("supplierDocumentProcessingReadAudits", new(), async (tx, row) => {
            var read = await extraction.ReadAuditAsync(tx, row["_id"].AsInt64, ct);
            var job = await Job(tx, row["outboxId"], SupplierDocumentExtractionRecords.Kind); var submitted = await Submission(tx, job);
            var binding = read["binding"];
            foreach (var (field, expected) in new (string, BsonValue)[] {
                ("submissionId", submitted["_id"]), ("draftId", submitted["draftId"]), ("draftRevision", 1L),
                ("reservationId", submitted["reservationId"]), ("pinId", submitted["pinId"]), ("submissionHash", submitted["snapshotHash"]),
                ("draftHash", submitted["draftHash"]), ("reference", submitted["evidence"]) })
                if (binding[field] != expected) throw Invalid();
            await OriginalBinding(tx, read, job);
        });
        await Each("supplierDocumentProcessingFailures", new(), async (tx, row) => { _ = await extraction.HistoricalFailureAsync(tx, row["_id"].AsInt64, ct); });
        foreach (var table in new[] { "supplierDocumentExtractionResults", "supplierDocumentExtractionAudits", "supplierDocumentExtractionReadAudits" })
            await Each(table, new(), async (tx, row) => {
                var job = await Job(tx, row["outboxId"], SupplierDocumentExtractionRecords.Kind); var submitted = await Submission(tx, job);
                var result = await extraction.ResultAsync(tx, job, ct);
                if (table != "supplierDocumentExtractionReadAudits" && result is null ||
                    table == "supplierDocumentExtractionReadAudits" && (row["submissionId"] != submitted["_id"] || row["draftId"] != submitted["draftId"] ||
                        row["reference"] != submitted["evidence"] || row["resultId"].IsBsonNull != row["resultHash"].IsBsonNull ||
                        (row["state"] == "succeeded") != !row["resultId"].IsBsonNull || !row["resultId"].IsBsonNull &&
                        (result is null || row["resultId"] != result["_id"] || row["resultHash"] != result["resultHash"]))) throw Invalid();
            });
        // A missing revalidation head cannot hide orphan requests, results, jobs or receipts.
        foreach (var table in EvidenceImageRevalidationSchemas.Collections.Where(name => name != EvidenceImageRevalidationSchemas.Policies))
            await Each(table, new(), async (tx, row) => {
                var version = table == EvidenceImageRevalidationSchemas.Heads ? row["_id"] : table == EvidenceImageRevalidationSchemas.Results ? row["source"]["versionRowId"] : row["versionRowId"];
                _ = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx, version.AsInt64, ct);
            });
    }

    private async Task RequireCountersAsync(MigrationLease lease, CancellationToken ct)
    {
        var counters = new List<(string Name, string[] Tables)> { ("evidenceIds", ["evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts"]) };
        foreach (var name in new[] { "evidenceStorePolicies", "evidenceReadPolicies", "evidenceReadLeases", "evidenceReadAudits", "evidenceLifecycleAudits",
                "evidenceValidationPolicies", "evidenceValidationAudits", "paymentSubmissions", "evidencePins", "evidencePinAudits", "auditEvents",
                "evidenceVideoStorePolicies", "evidenceVideoReadPolicies", VideoValidationRecords.Policies, VideoValidationRecords.Audits,
                PdfEvidenceValidationRecords.Policies, PdfEvidenceValidationRecords.Audits, AccountingReportRecords.Events,
                EvidenceImageRevalidationSchemas.Policies, EvidenceImageRevalidationSchemas.Requests, EvidenceImageRevalidationSchemas.Jobs,
                EvidenceImageRevalidationSchemas.Results, EvidenceImageRevalidationSchemas.Audits }
            .Concat(DocumentProcessingSchemas.Collections).Concat(SupplierDocumentProcessingSchemas.Collections).Distinct()) counters.Add((name, [name]));
        foreach (var (name, tables) in counters) {
            await lease.RenewAsync(ct); long maximum = 0;
            foreach (var table in tables) {
                var row = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
                if (row is not null) {
                    if (!row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or long.MaxValue) throw Invalid();
                    maximum = Math.Max(maximum, row["_id"].AsInt64);
                }
            }
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or long.MaxValue) throw Invalid();
        }
        foreach (var name in new[] { "evidenceValidationResults", VideoValidationRecords.Results, PdfEvidenceValidationRecords.Results,
                "evidenceLifecycles", EvidenceImageRevalidationSchemas.Heads, EvidenceImageRevalidationSchemas.Receipts })
            if (await Rows("idCounters").Find(new BsonDocument("_id", name)).Limit(1).AnyAsync(ct)) throw Invalid();
    }

    private async Task RequireOwnerAsync(string name, BsonDocument required, (string Name, BsonDocument Key, bool Unique)[] keys, MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct); var definition = await DefinitionAsync(name, ct) ?? throw Invalid();
        var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (!SafeDefinition(definition, required) || !SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => Matches(row, key)))) throw Invalid();
        await RequireRowsAsync(name, definition, ct);
    }
    private async Task RequireRowsAsync(string name, BsonDocument definition, CancellationToken ct)
    {
        if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
    }
    private async Task<BsonDocument?> DefinitionAsync(string name, CancellationToken ct) =>
        await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
    private static bool SafeDefinition(BsonDocument definition, BsonDocument required) =>
        PdfEvidenceAdmissionSchemas.SafeDefinition(definition) && InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, required);
    private static bool Matches(BsonDocument row, (string Name, BsonDocument Key, bool Unique) key) =>
        (key.Name.Length == 0 || row.GetValue("name", "") == key.Name || key.Key == new BsonDocument("_id", 1) && row.GetValue("name", "") == "_id_") &&
        row.GetValue("key", BsonNull.Value) == key.Key &&
        (row.GetValue("name", "") == "_id_" && key.Key == new BsonDocument("_id", 1) ? key.Unique : row.GetValue("unique", false).AsBoolean == key.Unique) &&
        !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression");
    private static bool SafeIndexes(IReadOnlyList<BsonDocument> indexes, (string Name, BsonDocument Key, bool Unique)[] keys) =>
        indexes.Any(row => row.GetValue("name", "") == "_id_" && row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)) &&
        indexes.All(row => !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression") &&
            (row.GetValue("name", "") == "_id_" || keys.Any(key => Matches(row, key)) ||
                !row.GetValue("unique", false).AsBoolean && !keys.Any(key => key.Name.Length > 0 && row.GetValue("name", "") == key.Name || row.GetValue("key", BsonNull.Value) == key.Key)));
}
