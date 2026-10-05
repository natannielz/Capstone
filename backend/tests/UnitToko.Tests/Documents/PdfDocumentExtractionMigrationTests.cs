using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class PdfDocumentExtractionMigrationTests(MongoFixture fixture)
{
    private static readonly string[] Owners = [PdfDocumentExtractionRecords.Policies, PdfDocumentExtractionRecords.ProcessingReads,
        PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.Failures, PdfDocumentExtractionRecords.Reads];
    private const string FailureMessage = "PDF document extraction migration found incompatible retained storage";

    [Fact]
    public async Task Empty_interrupted_owners_keep_stronger_controls_UUIDs_indexes_predecessor_counters_and_history_without_approval_seeds()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV85Async(db);
        await EnsureOwnersAsync(db);
        foreach (var name in Owners) {
            var definition = await DefinitionAsync(db, name);
            await ChangeAsync(db, name, new BsonDocument("$and", new BsonArray {
                definition["options"]["validator"], new BsonDocument("_id", new BsonDocument("$ne", 999L)) }));
        }
        // A safe supporting index and a retained allocator gap are operational controls, not objects to recreate.
        await db.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Reads).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", -1), new CreateIndexOptions { Name = "ix_operator_pdf_reads_time" }));
        Assert.Equal(1L, (await new IdAllocator(db).NextAsync("documentProcessingPolicies")).Value);
        var queue = await DefinitionAsync(db, "outboxEvents");
        await ChangeAsync(db, "outboxEvents", new BsonDocument("$and", new BsonArray {
            PdfDocumentExtractionSchemas.ExtendOutbox(queue["options"]["validator"].AsBsonDocument),
            new BsonDocument("sourceKey", new BsonDocument("$type", "string")) }));
        var before = await SnapshotAsync(db); var history = await HistoryAsync(db);
        await ApplyAsync(db, data.Clock); await PdfDocumentExtractionRecords.RequireStorageAsync(db, default);
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
        Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(85)));
        Assert.Equal(86, (await HistoryAsync(db)).Count);
        foreach (var name in Owners) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var complete = await SnapshotAsync(db); await ApplyAsync(db, data.Clock);
        Assert.Equal(complete.ToBson(), (await SnapshotAsync(db)).ToBson());
        await RestoreV85Async(db); Assert.Equal(complete.ToBson(), (await SnapshotAsync(db)).ToBson());
        await ApplyAsync(db, data.Clock); Assert.Equal(complete.ToBson(), (await SnapshotAsync(db)).ToBson());
    }

    [Fact]
    public async Task Unowned_rows_consumed_or_malformed_allocators_and_unsafe_new_owner_controls_refuse_before_any_DDL()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV85Async(db); await EnsureOwnersAsync(db);
        foreach (var owner in Owners) {
            var rows = db.GetCollection<BsonDocument>(owner);
            await rows.InsertOneAsync(new BsonDocument("_id", 9001L), new InsertOneOptions { BypassDocumentValidation = true });
            try { await RefusesAsync(db, data.Clock); }
            finally { await rows.DeleteOneAsync(new BsonDocument("_id", 9001L)); }
            foreach (var invalid in new BsonValue[] { new BsonInt64(1), new BsonInt32(0), new BsonString("0"), new BsonInt64(long.MaxValue) }) {
                await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", owner }, { "value", invalid } }, new InsertOneOptions { BypassDocumentValidation = true });
                try { await RefusesAsync(db, data.Clock); }
                finally { await db.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", owner)); }
            }
        }
        var definition = await DefinitionAsync(db, PdfDocumentExtractionRecords.Policies); var required = definition["options"]["validator"];
        await ChangeAsync(db, PdfDocumentExtractionRecords.Policies, new BsonDocument());
        try { await RefusesAsync(db, data.Clock); }
        finally { await ChangeAsync(db, PdfDocumentExtractionRecords.Policies, required.AsBsonDocument); }
        var key = PdfDocumentExtractionSchemas.Keys(PdfDocumentExtractionRecords.Policies)[0];
        await HideAsync(db, PdfDocumentExtractionRecords.Policies, key.Name, true);
        try { await RefusesAsync(db, data.Clock); }
        finally { await HideAsync(db, PdfDocumentExtractionRecords.Policies, key.Name, false); }
        await ApplyAsync(db, data.Clock); await PdfDocumentExtractionRecords.RequireStorageAsync(db, default);
    }

    [Fact]
    public async Task Weak_predecessors_malformed_counter_and_unexplained_document_jobs_are_preserved_and_never_relabelled()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV85Async(db); await EnsureOwnersAsync(db);
        foreach (var name in new[] { "evidenceLineages", PdfEvidenceValidationRecords.Policies, "documentProcessingPolicies",
                "supplierDocumentProcessingPolicies", EvidenceImageRevalidationSchemas.Policies, AccountContactSchemas.Collections[0], "idCounters" }) {
            var definition = await DefinitionAsync(db, name); var validator = definition["options"]["validator"];
            await ChangeAsync(db, name, new BsonDocument());
            try { await RefusesAsync(db, data.Clock); }
            finally { await ChangeAsync(db, name, validator.AsBsonDocument); }
        }
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "documentProcessingPolicies" }, { "value", 0 } }, new InsertOneOptions { BypassDocumentValidation = true });
        try { await RefusesAsync(db, data.Clock); }
        finally { await db.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", "documentProcessingPolicies")); }
        // The derivative PDF proof result IDs belong to evidenceIds, never to a repaired local sequence.
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", PdfEvidenceValidationRecords.Results }, { "value", 0L } });
        try { await RefusesAsync(db, data.Clock); }
        finally { await db.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", PdfEvidenceValidationRecords.Results)); }
        foreach (var kind in new[] { "payment.document-ocr.requested", "supplierdocument.document-ocr.requested", "payment.ocr.requested" }) {
            var job = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", kind },
                { "payload", new BsonDocument() }, { "sourceKey", "unsupported-pre-86-" + kind } };
            await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(job, new InsertOneOptions { BypassDocumentValidation = true });
            try { await RefusesAsync(db, data.Clock); }
            finally { await db.GetCollection<BsonDocument>("outboxEvents").DeleteOneAsync(new BsonDocument("_id", job["_id"])); }
        }
        var imageKey = DocumentProcessingSchemas.Key("documentProcessingPolicies");
        var index = (await (await db.GetCollection<BsonDocument>("documentProcessingPolicies").Indexes.ListAsync()).ToListAsync()).Single(row => row["key"] == imageKey);
        await HideAsync(db, "documentProcessingPolicies", index["name"].AsString, true);
        try { await RefusesAsync(db, data.Clock); }
        finally { await HideAsync(db, "documentProcessingPolicies", index["name"].AsString, false); }
        var marker = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 85)).SingleAsync();
        await db.GetCollection<BsonDocument>("schemaMigrations").UpdateOneAsync(new BsonDocument("_id", 85), new BsonDocument("$set", new BsonDocument("state", "applying")));
        var before = await SnapshotAsync(db);
        try { await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(db, data.Clock)); Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson()); }
        finally { await db.GetCollection<BsonDocument>("schemaMigrations").ReplaceOneAsync(new BsonDocument("_id", 85), marker); }
        await ApplyAsync(db, data.Clock);
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_payment_image_result_pin_source_and_receipt_survive_and_corrupt_retained_result_is_refused_without_repair()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var db = data.Database;
        await PaymentOcrProcessorTests.Submit(data);
        var processor = new PaymentOcrProcessor(db, new(data.Catalog.Client), new(db), data.Catalog.Clock,
            data.Files.Registration, PaymentOcrProcessorTests.Consumer, new PaymentOcrProcessorTests.FixtureEngine());
        Assert.Equal(1L, (await new IdAllocator(db).NextAsync("documentProcessingPolicies")).Value);
        await PaymentOcrProcessorTests.Policy(data, processor); Assert.Equal("completed", await processor.RunOnceAsync());
        Assert.Equal("succeeded", (await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "retained-before-pdf-owner"))).State);
        var result = await data.Rows("documentExtractionResults").Find(new BsonDocument()).SingleAsync();
        // Seed the real retained workflow while the registered schema is current. Only then
        // remove the proven-empty additive marker to exercise its prospective migration.
        await RestoreV85Async(db);
        await data.Rows("documentExtractionResults").UpdateOneAsync(new BsonDocument("_id", result["_id"]), new BsonDocument("$set", new BsonDocument("resultHash", new string('f', 64))));
        try { await RefusesAsync(db, data.Catalog.Clock); }
        finally { await data.Rows("documentExtractionResults").ReplaceOneAsync(new BsonDocument("_id", result["_id"]), result); }
        var before = await SnapshotAsync(db); await ApplyAsync(db, data.Catalog.Clock); await AssertPredecessorsPreservedAsync(before, db);
        Assert.False(await data.Rows(PdfDocumentExtractionRecords.Results).Find(new BsonDocument()).AnyAsync());
        Assert.Equal("succeeded", (await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "retained-after-pdf-owner"))).State);
        var imageJob = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync();
        var source = new PaymentOcrSource(db); var unit = new MongoUnitOfWork(data.Catalog.Client);
        Assert.Equal(data.Reference, (await unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, imageJob, ct))).Evidence);
        var forgedDocumentJob = imageJob.DeepClone().AsBsonDocument; forgedDocumentJob["kind"] = PdfDocumentExtractionContract.PaymentJobKind;
        forgedDocumentJob["sourceKey"] = "payment.document-ocr.requested:" + imageJob["payload"]["submissionId"].AsInt64;
        await Assert.ThrowsAnyAsync<Exception>(() => unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, forgedDocumentJob, ct)));
        Assert.Equal(imageJob, await data.Rows("outboxEvents").Find(new BsonDocument("_id", imageJob["_id"])).SingleAsync());
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_M85_original_PDF_page_proof_private_history_and_UUIDs_survive_but_corrupt_page_proof_cannot_be_adopted()
    {
        await using var data = await PdfEvidenceAdmissionTests.PaymentPdfData.CreateAsync(fixture); var db = data.Database;
        var (reserved, reference) = await data.UploadAsync(PdfEvidenceAdmissionTestRuntime.Pdf());
        await using var runtime = await data.RuntimeAsync(); Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        await using (var file = await data.OpenAsync(reserved, reference)) { using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.NotEmpty(bytes.ToArray()); }
        await RestoreV85Async(db);
        var proof = await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument()).SingleAsync();
        var corrupted = proof.DeepClone().AsBsonDocument; corrupted["pages"][0]["sha256"] = new string('f', 64);
        await data.Rows(PdfEvidenceValidationRecords.Results).ReplaceOneAsync(new BsonDocument("_id", proof["_id"]), corrupted);
        try { await RefusesAsync(db, data.Clock); }
        finally { await data.Rows(PdfEvidenceValidationRecords.Results).ReplaceOneAsync(new BsonDocument("_id", proof["_id"]), proof); }
        var before = await SnapshotAsync(db); await ApplyAsync(db, data.Clock); await AssertPredecessorsPreservedAsync(before, db);
        Assert.Equal(proof, await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument("_id", proof["_id"])).SingleAsync());
        Assert.False(await data.Rows(PdfDocumentExtractionRecords.Results).Find(new BsonDocument()).AnyAsync());
        await using var original = await data.OpenAsync(reserved, reference); Assert.Equal("application/pdf", original.Mime);
    }

    private static async Task ApplyAsync(IMongoDatabase db, IClock clock)
    {
        var lease = await new MigrationGate(db, clock, 86).AcquireAsync(86, default); if (lease is null) return;
        try { await new PdfDocumentExtractionMigration(db).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }

    // Empty test-only suffix rehearsal. Later empty owners are proven before their
    // markers are removed; only exact reviewed successor validator templates are
    // restored for this predecessor rehearsal, with operator conjuncts retained.
    internal static async Task RestoreV85Async(IMongoDatabase db)
    {
        await PaymentSupplementMigrationTests.RestoreV86Async(db);
        var history = await HistoryAsync(db); if (history.Count <= 85) return;
        Assert.Equal(86, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 86));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await PdfDocumentExtractionRecords.RequireStorageAsync(db, default);
        foreach (var name in Owners) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            var counter = await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
            Assert.True(counter is null || counter.TryGetValue("value", out var value) && value.IsInt64 && value.AsInt64 == 0L);
        }
        Assert.False(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("$or", new BsonArray {
            new BsonDocument("kind", new BsonDocument("$in", new BsonArray { "payment.document-ocr.requested", "supplierdocument.document-ocr.requested" })),
            new BsonDocument("processingReceipt.pdfDocumentExtraction", new BsonDocument("$exists", true)) })).AnyAsync());
        await RestoreReviewedM86ValidatorsAsync(db);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 86 }, { "state", "applied" } })).DeletedCount);
    }

    private static async Task RestoreReviewedM86ValidatorsAsync(IMongoDatabase db)
    {
        await PaymentSupplementMigrationTests.RequireEmptySupplementAsync(db);
        var templates = Owners.Select(name => (Name: name, Successor: PaymentSupplementSchemas.DocumentOwnerValidator(name),
                Predecessor: PdfDocumentExtractionSchemas.Validator(name)))
            .Concat(PaymentSupplementEvidenceSchemas.Collections.Select(name => (Name: name,
                Successor: PaymentSupplementEvidenceSchemas.Validator(name), Predecessor: PaymentSupplementEvidenceSchemas.PreviousValidator(name))))
            .Append((Name: "outboxEvents", Successor: PaymentSupplementSchemas.ExtendedOutboxValidator(),
                Predecessor: PdfDocumentExtractionSchemas.ExtendedOutboxValidator()));
        var changes = new List<(string Name, BsonDocument Before, BsonDocument After, BsonValue Uuid)>();
        foreach (var template in templates) {
            var definition = await DefinitionAsync(db, template.Name);
            Assert.True(PdfEvidenceAdmissionSchemas.SafeDefinition(definition));
            var before = definition["options"]["validator"].AsBsonDocument;
            var recognized = false;
            BsonDocument Replace(BsonDocument value) {
                if (CatalogSchemas.SemanticEquals(value, template.Successor)) {
                    recognized = true; return template.Predecessor.DeepClone().AsBsonDocument;
                }
                if (CatalogSchemas.SemanticEquals(value, template.Predecessor)) {
                    recognized = true; return value.DeepClone().AsBsonDocument;
                }
                var copy = value.DeepClone().AsBsonDocument;
                // An unrecognized disjunction is never inferred to be safe.
                if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                    copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
                return copy;
            }
            var after = Replace(before);
            Assert.True(recognized && InventorySchemas.Contains(after, template.Predecessor));
            Assert.False(await db.GetCollection<BsonDocument>(template.Name).Find(new BsonDocument("$nor", new BsonArray { after })).AnyAsync());
            changes.Add((template.Name, before.DeepClone().AsBsonDocument, after, definition["info"]["uuid"].DeepClone()));
        }
        // Every definition and retained row was checked before any test-only DDL.
        foreach (var change in changes) {
            var current = await DefinitionAsync(db, change.Name);
            Assert.Equal(change.Uuid, current["info"]["uuid"]); Assert.Equal(change.Before, current["options"]["validator"]);
            if (!change.Before.Equals(change.After)) await ChangeAsync(db, change.Name, change.After);
            var installed = await DefinitionAsync(db, change.Name);
            Assert.Equal(change.Uuid, installed["info"]["uuid"]); Assert.Equal(change.After, installed["options"]["validator"]);
        }
    }

    private static async Task EnsureOwnersAsync(IMongoDatabase db)
    {
        foreach (var name in Owners) {
            if (await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleOrDefaultAsync() is null)
                await db.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
                    Validator = PdfDocumentExtractionSchemas.Validator(name), ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
            foreach (var key in PdfDocumentExtractionSchemas.Keys(name))
                await db.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                    new CreateIndexOptions { Name = key.Name, Unique = key.Unique }));
        }
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(db, clock));
        Assert.Equal(FailureMessage, error.Message); Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
    }
    private static async Task AssertPredecessorsPreservedAsync(BsonDocument before, IMongoDatabase db)
    {
        var after = await SnapshotAsync(db); var expected = before.DeepClone().AsBsonDocument;
        foreach (var name in Owners) { expected.Remove(name); after.Remove(name); }
        expected["outboxEvents"]["definition"]["options"]["validator"] = PdfDocumentExtractionSchemas.ExtendOutbox(before["outboxEvents"]["definition"]["options"]["validator"].AsBsonDocument);
        Assert.Equal(expected.ToBson(), after.ToBson());
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static Task<BsonDocument> ChangeAsync(IMongoDatabase db, string name, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", name }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static Task<BsonDocument> HideAsync(IMongoDatabase db, string name, string index, bool hidden) => db.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", name }, { "index", new BsonDocument { { "name", index }, { "hidden", hidden } } } });
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
