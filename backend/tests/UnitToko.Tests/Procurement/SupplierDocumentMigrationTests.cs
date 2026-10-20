using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SupplierDocumentMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Completed_payment_OCR_history_survives_supplier_upgrade_and_missing_audit_or_receipt_refuses_before_DDL() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await PaymentOcrProcessorTests.Submit(data);
        var engine = new PaymentOcrProcessorTests.FixtureEngine(); var processor = PaymentOcrProcessorTests.Processor(data, engine);
        // Reuse the explicit synthetic policy helper, retaining the real allocator high-water proof.
        Assert.Equal(1, (await new IdAllocator(data.Database).NextAsync("documentProcessingPolicies")).Value);
        await PaymentOcrProcessorTests.Policy(data, processor); Assert.Equal("completed", await processor.RunOnceAsync());
        var beforeRead = await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "payment-before70"));
        Assert.Equal("succeeded", beforeRead.State); Assert.Equal(1, engine.Calls);
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync();
        Assert.Equal("completed", job["status"]); Assert.True(job.Contains("processingReceipt"));
        var result = await data.Rows("documentExtractionResults").Find(new BsonDocument("_id", beforeRead.ResultId!.Value)).SingleAsync();
        var audit = await data.Rows("documentExtractionAudits").Find(new BsonDocument("_id", result["auditId"])).SingleAsync();
        await RestoreV69Async(data.Database);
        Assert.Equal(1, (await data.Rows("documentExtractionAudits").DeleteOneAsync(new BsonDocument("_id", audit["_id"]))).DeletedCount);
        try { await RefusesAsync(data.Database, data.Catalog.Clock); }
        finally { await data.Rows("documentExtractionAudits").InsertOneAsync(audit); }
        Assert.Equal(1, (await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]),
            new BsonDocument("$unset", new BsonDocument("processingReceipt", "")), new UpdateOptions { BypassDocumentValidation = true })).ModifiedCount);
        try { await RefusesAsync(data.Database, data.Catalog.Clock, "Supplier document evidence migration preflight failed: outboxEvents"); }
        finally { await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), job); }
        var state = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Catalog.Clock).ApplyAsync();
        Assert.Equal(state, await StateAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        await RequireExpectedDefinitionsAsync(data.Database, definitions); await EmptyAsync(data.Database);
        var afterRead = await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "payment-after70"));
        Assert.Equal("succeeded", afterRead.State); Assert.Equal(beforeRead.ResultId, afterRead.ResultId); Assert.Equal(beforeRead.Evidence, afterRead.Evidence);
        Assert.Equal(beforeRead.ExtractedAt, afterRead.ExtractedAt); Assert.Equal(beforeRead.Extraction!.Provenance, afterRead.Extraction!.Provenance);
        Assert.Equal(beforeRead.Extraction.Lines, afterRead.Extraction.Lines);
        Assert.Equal("100.00", Assert.Single(afterRead.Extraction.Fields, field => field.Field == "amount").Value);
        Assert.Equal(result, await data.Rows("documentExtractionResults").Find(new BsonDocument("_id", result["_id"])).SingleAsync());
        Assert.Equal(audit, await data.Rows("documentExtractionAudits").Find(new BsonDocument("_id", audit["_id"])).SingleAsync());
        Assert.Equal(job, await data.Rows("outboxEvents").Find(new BsonDocument("_id", job["_id"])).SingleAsync());
        Assert.Equal("idle", await processor.RunOnceAsync()); Assert.Equal(1, engine.Calls);
        await using var original = await data.OpenAsync(); using var bytes = new MemoryStream();
        await original.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
    }

    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Genuine_submitted_private_video_and_inspection_history_survive_supplier_extension_and_missing_pin_or_worker_receipt_refuses_before_DDL() {
        await using var data = await ProcurementInspectionTestData.CreateAsync(fixture, onReceipt: true);
        await data.UploadAsync(); await data.ValidateAsync(); var submitted = await data.SubmitAsync(await data.PreviewSubmitAsync());
        await RestoreV69Async(data.Database);
        var pin = await data.Rows("evidencePins").Find(new BsonDocument { { "associationKind", "ProcurementInspection" }, { "associationId", submitted["inspectionId"] } }).SingleAsync();
        var exactAudit = await data.Rows("evidencePinAudits").Find(new BsonDocument("pinId", pin["_id"])).SingleAsync();
        Assert.Equal(1, (await data.Rows("evidencePinAudits").DeleteOneAsync(new BsonDocument("_id", exactAudit["_id"]))).DeletedCount);
        try { await RefusesAsync(data.Database, data.Clock); } finally { await data.Rows("evidencePinAudits").InsertOneAsync(exactAudit); }
        var job = await data.Rows("outboxEvents").Find(new BsonDocument { { "kind", VideoValidationRecords.Kind }, { "payload.reservationId", data.Reservation.ReservationId } }).SingleAsync();
        Assert.Equal(1, (await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]), new BsonDocument("$unset", new BsonDocument("processingReceipt", "")), new UpdateOptions { BypassDocumentValidation = true })).ModifiedCount);
        try { await RefusesAsync(data.Database, data.Clock); } finally { await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), job); }
        var state = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(state, await StateAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database)); await RequireExpectedDefinitionsAsync(data.Database, definitions);
        await EmptyAsync(data.Database); Assert.Equal(submitted, await data.Service.LookupAsync(data.Actor, ProcurementInspectionTestData.SubmitOperation, "inspection-submit"));
        await using var read = await data.Evidence.OpenReadAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, ProcurementInspectionTestData.ReadMetadata);
        using var copy = new MemoryStream(); await read.Content.CopyToAsync(copy); Assert.Equal(data.Bytes, copy.ToArray());
    }

    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_partial_install_preserves_stronger_guards_rejects_orphan_ocr_and_unknown_indexes_without_default_policies() {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV69Async(data.Database);
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        var definition = await DefinitionAsync(data.Database, "supplierDocumentProcessingPolicies");
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "supplierDocumentProcessingPolicies" }, { "validator", new BsonDocument("$and", new BsonArray { definition["options"]["validator"], guard }) } });
        var rows = data.Database.GetCollection<BsonDocument>(SupplierDocumentBson.Drafts);
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("draftId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unknown_supplier_partial", Unique = true, PartialFilterExpression = new BsonDocument("version", 1L) }));
        try { await RefusesAsync(data.Database, data.Clock); } finally { await rows.Indexes.DropOneAsync("unknown_supplier_partial"); }
        var orphan = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "unrelated.test" }, { "processingReceipt", new BsonDocument("supplierDocumentExtraction", true) } };
        await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(orphan, new InsertOneOptions { BypassDocumentValidation = true });
        try { await RefusesAsync(data.Database, data.Clock, "Supplier document evidence migration preflight failed: supplier OCR receipt before acceptance"); }
        finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("outboxEvents").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        var before = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(before, await StateAsync(data.Database)); await RequireExpectedDefinitionsAsync(data.Database, definitions); await EmptyAsync(data.Database);
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, "supplierDocumentProcessingPolicies"))["options"]["validator"].AsBsonDocument, guard));
        var accepted = await DefinitionsAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(accepted, await DefinitionsAsync(data.Database));
        Assert.Equal(ProcurementInspectionEvidenceSchemas.Validator("outboxEvents"), SupplierDocumentEvidenceSchemas.Validator("outboxEvents")["$and"][0]);
        foreach (var name in SupplierDocumentEvidenceSchemas.Collections.Where(name => name != "outboxEvents"))
            Assert.Equal(ProcurementInspectionEvidenceSchemas.Validator(name), SupplierDocumentEvidenceSchemas.Validator(name)["$or"][0]);
    }

    internal static async Task RestoreV69Async(IMongoDatabase db) {
        await SupplierClaimMigrationTests.RestoreV70Async(db);
        var history = await HistoryAsync(db); if (history.Count <= 69) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(70, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"])); await EmptyAsync(db);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            foreach (var prefix in new[] { @"^procurement\.supplierdocument(?:[.:]|$)", @"^evidence\.supplier-document-image(?:[.:]|$)", @"^supplierdocument\.ocr(?:[.:]|$)" })
                Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(prefix))).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("processingReceipt.supplierDocumentExtraction", new BsonDocument("$exists", true))).AnyAsync());
        foreach (var table in new[] { "evidenceLineages", "evidenceUploadReservations", "evidenceReadLeases" })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument("target.kind", "SupplierDocument")).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("evidencePins").Find(new BsonDocument("associationKind", "SupplierDocument")).AnyAsync());
        foreach (var name in SupplierDocumentEvidenceSchemas.Collections) {
            var definition = await DefinitionAsync(db, name); var found = false;
            BsonDocument Prior(BsonDocument value) {
                if (CatalogSchemas.SemanticEquals(value, SupplierDocumentEvidenceSchemas.Validator(name))) { found = true; return SupplierDocumentEvidenceSchemas.PreviousValidator(name); }
                var copy = value.DeepClone().AsBsonDocument;
                if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray) copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Prior(term.AsBsonDocument) : term.DeepClone()));
                return copy;
            }
            var previous = Prior(definition["options"]["validator"].AsBsonDocument); Assert.True(found);
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument("$nor", new BsonArray { previous })).AnyAsync());
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", previous }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        }
        Assert.Equal(1, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 70 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(69)), await HistoryAsync(db));
    }
    internal static async Task RequireExpectedDefinitionsAsync(IMongoDatabase db, List<BsonDocument> before) {
        var after = await DefinitionsAsync(db); Assert.Equal(before.Count, after.Count);
        foreach (var prior in before) {
            var expected = prior.DeepClone().AsBsonDocument; var name = prior["name"].AsString;
            if (SupplierDocumentEvidenceSchemas.Collections.Contains(name)) expected["options"]["validator"] = SupplierDocumentEvidenceSchemas.Extend(name, prior["options"]["validator"].AsBsonDocument);
            Assert.Equal(expected, Assert.Single(after, row => row["name"] == name));
        }
    }
    private static readonly string[] NewCollections = [.. SupplierDocumentSchemas.Collections, .. SupplierDocumentProcessingSchemas.Collections];
    private static async Task EmptyAsync(IMongoDatabase db) {
        foreach (var name in NewCollections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock, string expected = "Supplier document migration preflight failed") {
        var definitions = await DefinitionsAsync(db); var indexes = await IndexesAsync(db); var state = await StateAsync(db, true); var history = new BsonArray((await HistoryAsync(db)).Take(69));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync()); Assert.Equal(expected, error.Message);
        Assert.Equal(definitions, await DefinitionsAsync(db)); Assert.Equal(indexes, await IndexesAsync(db)); Assert.Equal(state, await StateAsync(db, true));
        Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(69))); Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 70 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> IndexesAsync(IMongoDatabase db) {
        var state = new BsonDocument(); foreach (var row in await DefinitionsAsync(db)) state[row["name"].AsString] = new BsonArray((await (await db.GetCollection<BsonDocument>(row["name"].AsString).Indexes.ListAsync()).ToListAsync()).OrderBy(index => index["name"].AsString)); return state;
    }
    private static async Task<BsonDocument> StateAsync(IMongoDatabase db, bool counters = false) {
        var state = new BsonDocument(); foreach (var definition in await DefinitionsAsync(db)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks" || name == "idCounters" && !counters) continue;
            state[name] = new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        }
        return state;
    }
}
