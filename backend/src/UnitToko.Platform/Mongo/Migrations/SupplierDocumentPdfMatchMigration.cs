using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Mongo.Migrations;

// One additive collMod. No rows, original files, pins, policies, allocator,
// indexes, financial claims or operator approvals are rewritten or seeded.
internal sealed class SupplierDocumentPdfMatchMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 89;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Supplier PDF matching migration found incompatible retained storage");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException or
            InvalidOperationException or ArgumentException or KeyNotFoundException or InvalidCastException or FormatException or OverflowException) {
            throw new InvalidOperationException("Supplier PDF matching migration preflight failed", error);
        }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying" ||
            database.Client is not MongoClient client) throw Invalid();
        var before = await AccountContactStorage.DefinitionAsync(database, B.Matches, ct) ?? throw Invalid();
        var previousValidator = before["options"]["validator"].AsBsonDocument;
        var successor = SupplierDocumentPdfMatchSchemas.Extend(previousValidator);
        var preserved = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, List<BsonDocument>>(StringComparer.Ordinal);
        foreach (var name in SupplierDocumentSchemas.Collections) await SupplierDocumentPdfMatchStorage.RequireAsync(database, name, ct);
        await SupplierDocumentEvidenceSchemas.RequireStorageAsync(database, ct);
        await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        await PdfDocumentExtractionRecords.RequireStorageAsync(database, ct);
        foreach (var name in SupplierDocumentSchemas.Collections.Concat(PdfDocumentExtractionSchemas.Collections)
            .Concat(SupplierDocumentEvidenceSchemas.Collections).Distinct(StringComparer.Ordinal)) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (!definition.TryGetValue("info", out var info) || !info.IsBsonDocument ||
                !info.AsBsonDocument.TryGetValue("uuid", out var uuid) || !uuid.IsBsonBinaryData) throw Invalid();
            preserved.Add(name, definition.DeepClone().AsBsonDocument);
            indexes.Add(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        // A reviewed interrupted installation may already contain successor DDL,
        // but no pre89 producer/backfill may adopt annotated business history.
        if (await Rows(B.Matches).Find(new BsonDocument("$or", new BsonArray {
                new BsonDocument("schemaVersion", new BsonDocument("$nin", new BsonArray { 1, 2 })),
                new BsonDocument("input.pdfObservations", new BsonDocument("$exists", true)) })).Limit(1).AnyAsync(ct) ||
            await Rows("auditEvents").Find(new BsonDocument {
                { "kind", B.MatchOperation }, { "pdfObservations", new BsonDocument("$exists", true) } }).Limit(1).AnyAsync(ct)) throw Invalid();
        await RequireCountersAsync(lease, ct);
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        await RequireHistoriesAsync(client, lease, ct);
        if (await Rows(B.Matches).Find(new BsonDocument("$nor", new BsonArray { successor })).Limit(1).AnyAsync(ct)) throw Invalid();
        // All source graphs, actual private original/result/receipt and capacity
        // histories are reconstructed before the only DDL operation.
        foreach (var pair in preserved) {
            var current = await AccountContactStorage.DefinitionAsync(database, pair.Key, ct) ?? throw Invalid();
            if (current["info"]["uuid"] != pair.Value["info"]["uuid"] || current["options"] != pair.Value["options"]) throw Invalid();
        }
        await lease.RenewAsync(ct);
        if (successor != previousValidator) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", B.Matches }, { "validator", successor }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var pair in preserved) {
            var installed = await AccountContactStorage.DefinitionAsync(database, pair.Key, ct) ?? throw Invalid();
            if (installed["info"]["uuid"] != pair.Value["info"]["uuid"]) throw Invalid();
            var expectedOptions = pair.Value["options"].DeepClone().AsBsonDocument;
            if (pair.Key == B.Matches) { expectedOptions["validator"] = successor; expectedOptions["validationLevel"] = "strict"; expectedOptions["validationAction"] = "error"; }
            if (installed["options"] != expectedOptions) throw Invalid();
            var afterIndexes = await (await Rows(pair.Key).Indexes.ListAsync(ct)).ToListAsync(ct);
            var beforeIndexes = indexes[pair.Key];
            if (afterIndexes.Count != beforeIndexes.Count || beforeIndexes.Any(index => !afterIndexes.Any(candidate => candidate.Equals(index)))) throw Invalid();
        }
        var retainedCounters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (counters.Count != retainedCounters.Count || counters.Where((row, index) => row != retainedCounters[index]).Any()) throw Invalid();
        await SupplierDocumentPdfMatchStorage.RequireAsync(database, B.Matches, ct, requireAnnotated: true);
    }
    private async Task RequireCountersAsync(MigrationLease lease, CancellationToken ct) {
        await EvidenceMigration.EachAsync(Rows("idCounters"), new BsonDocument(), row => {
            if (!row.TryGetValue("_id", out var id) || !id.IsString || string.IsNullOrWhiteSpace(id.AsString) ||
                !row.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
            return Task.CompletedTask;
        }, lease, ct);
        foreach (var name in new[] { B.Drafts, B.Submissions, B.Matches, B.Identities, "auditEvents" }.Concat(PdfDocumentExtractionSchemas.Collections)) {
            await lease.RenewAsync(ct);
            var last = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var maximum = last is null ? 0L : last["_id"].IsInt64 && last["_id"].AsInt64 is > 0 and < long.MaxValue - 1 ? last["_id"].AsInt64 : throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 ||
                value.AsInt64 < maximum || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Invalid();
        }
    }
    private async Task RequireHistoriesAsync(MongoClient client, MigrationLease lease, CancellationToken ct) {
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog,
            new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var records = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
        async Task Each(string name, Func<IClientSessionHandle, BsonDocument, Task> check, BsonDocument? filter = null) =>
            await EvidenceMigration.EachAsync(Rows(name), filter ?? new BsonDocument(), async row => {
                using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
                tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot));
                await check(tx, row); await tx.CommitTransactionAsync(ct);
            }, lease, ct);
        await Each(B.Drafts, async (tx, row) => { _ = await records.HistoryAsync(tx, row["_id"].AsInt64, ct); });
        await Each(B.Submissions, async (tx, row) => {
            var actual = await records.SubmissionAsync(tx, row["_id"].AsInt64, ct);
            SupplierDocumentRecords.Equal(actual, row);
            _ = await records.HistoryAsync(tx, row["draftId"].AsInt64, ct);
        });
        await Each(B.Matches, async (tx, row) => {
            var actual = await records.MatchAsync(tx, row["_id"].AsInt64, ct); SupplierDocumentRecords.Equal(actual, row);
            _ = await records.HistoryAsync(tx, row["draftId"].AsInt64, ct);
        });
        foreach (var table in new[] { B.Heads, B.Identities, B.InspectionHeads, B.OrderHeads })
            await Each(table, async (tx, row) => {
                await records.OrphansAsync(tx, ct);
                if (table == B.Heads) _ = await records.HistoryAsync(tx, row["draftId"].AsInt64, ct);
                else if (table == B.Identities) {
                    var first = await records.DraftAsync(tx, row["firstDraftId"].AsInt64, ct);
                    SupplierDocumentRecords.Equal(await records.IdentityAsync(tx, first, ct), row);
                } else {
                    var match = await records.MatchAsync(tx, row["lastMatchId"].AsInt64, ct);
                    _ = await records.HistoryAsync(tx, match["draftId"].AsInt64, ct);
                }
            });
        var pdf = new PdfDocumentExtractionRecords(database);
        async Task PdfSource(IClientSessionHandle tx, BsonDocument binding, BsonDocument original, BsonValue outboxId) {
            var document = await records.SubmissionAsync(tx, binding["submissionId"].AsInt64, ct);
            var publication = await records.OriginalAsync(tx, document, ct) ?? throw Invalid();
            if (original != PdfDocumentExtractionRecords.Original(publication)) throw Invalid();
            foreach (var (name, expected) in new (string, BsonValue)[] {
                ("submissionId", document["_id"]), ("draftId", document["draftId"]), ("draftRevision", 1L),
                ("submissionHash", document["snapshotHash"]), ("draftHash", document["draftHash"]), ("pinId", document["pinId"]),
                ("reservationId", document["reservationId"]), ("uploaderId", document["actorId"]), ("reference", document["evidence"]) })
                SupplierDocumentRecords.Equal(binding[name], expected);
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", outboxId)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            SupplierDocumentRecords.Equal(job["sourceKey"], B.DocumentOcrKey(document));
            SupplierDocumentRecords.Equal(job["payload"], B.DocumentOcrPayload(document, publication));
        }
        await Each(PdfDocumentExtractionRecords.Results, async (tx, row) => {
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", row["outboxId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            SupplierDocumentRecords.Equal(await pdf.ResultAsync(tx, job, ct) ?? throw Invalid(), row);
            await PdfSource(tx, row["binding"].AsBsonDocument, row["original"].AsBsonDocument, row["outboxId"]);
        }, new("sourceKind", "SupplierInvoice"));
        await Each(PdfDocumentExtractionRecords.ProcessingReads, async (tx, row) => {
            SupplierDocumentRecords.Equal(await pdf.ReadAuditAsync(tx, row["_id"].AsInt64, ct), row);
            await PdfSource(tx, row["binding"].AsBsonDocument, row["original"].AsBsonDocument, row["outboxId"]);
        }, new("sourceKind", "SupplierInvoice"));
        await Each(PdfDocumentExtractionRecords.Failures, async (tx, row) => {
            SupplierDocumentRecords.Equal((await pdf.HistoricalFailureAsync(tx, row["_id"].AsInt64, ct)).Failure, row);
            await PdfSource(tx, row["binding"].AsBsonDocument, row["original"].AsBsonDocument, row["outboxId"]);
        }, new("sourceKind", "SupplierInvoice"));
        await Each(PdfDocumentExtractionRecords.Reads, async (tx, row) => {
            var document = await records.SubmissionAsync(tx, row["submissionId"].AsInt64, ct);
            if (row["sourceId"] != document["draftId"] || row["sourceRevision"] != 1L || row["reference"] != document["evidence"] ||
                row["createdAt"].CompareTo(document["recordedAt"]) < 0) throw Invalid();
            var publication = await records.OriginalAsync(tx, document, ct) ?? throw Invalid();
            var job = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", row["outboxId"])).SingleOrDefaultAsync(ct) ?? throw Invalid();
            SupplierDocumentRecords.Equal(job["payload"], B.DocumentOcrPayload(document, publication));
            if (!row["resultId"].IsBsonNull) {
                var result = await pdf.ResultAsync(tx, job, ct) ?? throw Invalid();
                if (result["_id"] != row["resultId"] || result["resultHash"] != row["resultHash"] ||
                    result["createdAt"].CompareTo(row["createdAt"]) > 0) throw Invalid();
            }
        }, new("sourceKind", "SupplierInvoice"));
        // Reuse the actual mixed-quality source graph; no recomputed stock,
        // valuation or payable amount is introduced by this provenance extension.
        await ProcurementMixedQualityPreflight.RequireAsync(database, unit, clock, catalog, orders,
            new StockSourceSetupService(database, unit, ids, clock), ct);
    }
}
