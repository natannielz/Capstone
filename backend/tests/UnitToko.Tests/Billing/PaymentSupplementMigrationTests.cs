using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
public sealed class PaymentSupplementMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_interrupted_owner_keeps_UUIDs_stronger_controls_indexes_counters_and_applied_predecessor_history_without_seeds()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV86Async(db);
        await EnsureOwnersAsync(db);
        foreach (var name in PaymentSupplementSchemas.Collections) {
            var current = (await DefinitionAsync(db, name))["options"]["validator"];
            await ChangeAsync(db, name, new("$and", new BsonArray { current, new BsonDocument("_id", new BsonDocument("$ne", 999L)) }));
        }
        foreach (var name in PdfDocumentExtractionSchemas.Collections) {
            var current = (await DefinitionAsync(db, name))["options"]["validator"].AsBsonDocument;
            await ChangeAsync(db, name, new("$and", new BsonArray { PaymentSupplementSchemas.ExtendDocumentOwner(name, current),
                new BsonDocument("_id", new BsonDocument("$ne", 998L)) }));
        }
        foreach (var name in PaymentSupplementEvidenceSchemas.Collections) {
            var current = (await DefinitionAsync(db, name))["options"]["validator"].AsBsonDocument;
            await ChangeAsync(db, name, new("$and", new BsonArray { PaymentSupplementEvidenceSchemas.Extend(name, current),
                new BsonDocument("_id", new BsonDocument("$ne", 997L)) }));
        }
        var queue = (await DefinitionAsync(db, "outboxEvents"))["options"]["validator"].AsBsonDocument;
        await ChangeAsync(db, "outboxEvents", new("$and", new BsonArray { PaymentSupplementSchemas.ExtendOutbox(queue),
            new BsonDocument("sourceKey", new BsonDocument("$type", "string")) }));
        await db.GetCollection<BsonDocument>(PaymentSupplementBson.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("createdAt", -1), new CreateIndexOptions { Name = "ix_operator_supplement_created" }));
        Assert.Equal(1L, (await new IdAllocator(db).NextAsync("documentProcessingPolicies")).Value);
        var before = await SnapshotAsync(db); var history = await HistoryAsync(db);
        await ApplyAsync(db, data.Clock);
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson()); Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(86)));
        foreach (var name in PaymentSupplementSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        await RestoreV86Async(db); await ApplyAsync(db, data.Clock); Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
    }
    [Fact]
    public async Task Unowned_rows_consumed_allocators_and_hidden_or_weak_new_controls_refuse_before_DDL_without_repairs()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV86Async(db); await EnsureOwnersAsync(db);
        foreach (var name in PaymentSupplementSchemas.Collections) {
            var rows = db.GetCollection<BsonDocument>(name);
            await rows.InsertOneAsync(new BsonDocument("_id", 9001L), new InsertOneOptions { BypassDocumentValidation = true });
            try { await RefusesAsync(db, data.Clock); } finally { await rows.DeleteOneAsync(new BsonDocument("_id", 9001L)); }
            foreach (var bad in new BsonValue[] { new BsonInt64(1), new BsonInt32(0), new BsonString("0"), new BsonInt64(long.MaxValue) }) {
                await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", name }, { "value", bad } }, new InsertOneOptions { BypassDocumentValidation = true });
                try { await RefusesAsync(db, data.Clock); } finally { await db.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", name)); }
            }
        }
        var policy = (await DefinitionAsync(db, PaymentSupplementBson.Policies))["options"]["validator"].AsBsonDocument;
        await ChangeAsync(db, PaymentSupplementBson.Policies, new());
        try { await RefusesAsync(db, data.Clock); } finally { await ChangeAsync(db, PaymentSupplementBson.Policies, policy); }
        var index = PaymentSupplementSchemas.Keys(PaymentSupplementBson.Policies)[0];
        await HideAsync(db, PaymentSupplementBson.Policies, index.Name, true);
        try { await RefusesAsync(db, data.Clock); } finally { await HideAsync(db, PaymentSupplementBson.Policies, index.Name, false); }
    }
    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_complete_supplier_pages_and_receipt_survive_but_corrupt_result_or_orphan_read_refuses_adoption()
    {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture);
        // The fixture's explicit synthetic policy used ID1; retain its allocator provenance too.
        Assert.Equal(1L, (await new IdAllocator(data.Database).NextAsync(PdfDocumentExtractionRecords.Policies)).Value);
        Assert.Equal("completed", await data.Runtime.RunOnceAsync());
        var supplierJob = await data.JobAsync();
        var submitted = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument("_id", supplierJob["payload"]["submissionId"])).SingleAsync();
        Assert.True(submitted["recordedAt"].IsBsonDateTime);
        Assert.Equal(submitted["recordedAt"], supplierJob["createdAt"]);
        Assert.False(submitted.Contains("submittedAt"));
        var db = data.Database; await RestoreV86Async(db); var result = await data.Rows(PdfDocumentExtractionRecords.Results).Find(new BsonDocument()).SingleAsync();
        await data.Rows(PdfDocumentExtractionRecords.Results).UpdateOneAsync(new BsonDocument("_id", result["_id"]), new BsonDocument("$set", new BsonDocument("resultHash", new string('f', 64))));
        try { await RefusesAsync(db, data.Data.Clock); } finally { await data.Rows(PdfDocumentExtractionRecords.Results).ReplaceOneAsync(new BsonDocument("_id", result["_id"]), result); }
        var read = await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).Find(new BsonDocument()).SingleAsync();
        await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).DeleteOneAsync(new BsonDocument("_id", read["_id"]));
        try { await RefusesAsync(db, data.Data.Clock); } finally { await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).InsertOneAsync(read); }
        var before = await SnapshotAsync(db); await ApplyAsync(db, data.Data.Clock); var after = await SnapshotAsync(db);
        var expected = ExpectedExtensions(before);
        foreach (var name in PaymentSupplementSchemas.Collections) after.Remove(name);
        Assert.Equal(expected.ToBson(), after.ToBson());
        var job = await data.JobAsync(); Assert.Equal(result, await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(db).ResultAsync(tx, job, ct)));
    }
    [Fact]
    public async Task Actual_M86_history_and_global_counters_are_required_and_primary_payment_PDF_kind_remains_rejected()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV86Async(db);
        var marker = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 86)).SingleAsync();
        await db.GetCollection<BsonDocument>("schemaMigrations").UpdateOneAsync(new BsonDocument("_id", 86), new BsonDocument("$set", new BsonDocument("state", "applying")));
        var before = await SnapshotAsync(db);
        try { await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(db, data.Clock)); Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson()); }
        finally { await db.GetCollection<BsonDocument>("schemaMigrations").ReplaceOneAsync(new BsonDocument("_id", 86), marker); }
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", PdfDocumentExtractionRecords.Policies }, { "value", 0 } }, new InsertOneOptions { BypassDocumentValidation = true });
        try { await RefusesAsync(db, data.Clock); } finally { await db.GetCollection<BsonDocument>("idCounters").DeleteOneAsync(new BsonDocument("_id", PdfDocumentExtractionRecords.Policies)); }
        await ApplyAsync(db, data.Clock);
        var original = new BsonDocument { { "storeId", new string('a', 64) }, { "reservationId", 5L }, { "attemptId", 8L }, { "attemptRevision", 1L },
            { "stageToken", new string('c', 64) }, { "generation", new string('d', 64) }, { "contentRevision", 1L }, { "length", 1024L }, { "sha256", new string('b', 64) } };
        var unsupported = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", PdfDocumentExtractionContract.PaymentJobKind },
            { "sourceKey", "payment.document-ocr.requested:1" }, { "payload", new BsonDocument { { "sourceKind", "Payment" }, { "submissionId", 1L },
                { "evidence", new BsonDocument { { "id", 2L }, { "version", 1L }, { "sha256", new string('b', 64) } } }, { "pinId", 3L },
                { "submissionHash", new string('e', 64) }, { "uploaderId", 4L }, { "reservationId", 5L }, { "original", original },
                { "declarationId", 6L }, { "declarationRevision", 1L }, { "reviewRevision", 1L }, { "declarationVersionId", 7L } } },
            { "createdAt", data.Clock.UtcNow.UtcDateTime }, { "dueAt", data.Clock.UtcNow.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } };
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(unsupported))).WriteError.Code);
    }
    internal static async Task ApplyAsync(IMongoDatabase db, IClock clock)
    {
        // Rehearse M87 only after the exact later owner is proven empty. Its
        // reviewed successor controls and all existing data remain installed.
        await RestoreV87Async(db);
        var lease = await new MigrationGate(db, clock, 87).AcquireAsync(87, default); if (lease is null) return;
        try { await new PaymentSupplementMigration(db).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task RestoreV86Async(IMongoDatabase db)
    {
        await RestoreV87Async(db);
        var history = await HistoryAsync(db); if (history.Count <= 86) return;
        Assert.Equal(87, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 87));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await PaymentSupplementStorage.RequireAsync(db, default);
        await RequireEmptySupplementAsync(db);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 87 }, { "state", "applied" } })).DeletedCount);
    }
    // The later owner is additive and owns no new allocator. Remove only its
    // proven-empty marker; retain all rows, reviewed successor controls and UUIDs.
    internal static async Task RestoreV87Async(IMongoDatabase db)
    {
        var history = await HistoryAsync(db); if (history.Count <= 87) return;
        Assert.Equal(88, history.Count); Assert.True(MigrationHistory.IsValid(history.Select(row => row.AsBsonDocument).ToArray(), 88));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await PaymentSupplementCorrectionStorage.RequireAsync(db, PaymentCorrectionBson.Versions, default, requireAnnotated: true);
        await PaymentSupplementCorrectionStorage.RequireAsync(db, PaymentCorrectionBson.Heads, default);
        Assert.False(await db.GetCollection<BsonDocument>(PaymentCorrectionBson.Versions).Find(new BsonDocument("$or", new BsonArray {
            new BsonDocument("schemaVersion", 2), new BsonDocument("supplementaryObservations", new BsonDocument("$exists", true)) })).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("auditEvents").Find(new BsonDocument("supplementaryObservations", new BsonDocument("$exists", true))).AnyAsync());
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 88 }, { "state", "applied" } })).DeletedCount);
    }
    internal static async Task RequireEmptySupplementAsync(IMongoDatabase db)
    {
        foreach (var name in PaymentSupplementSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            var counter = await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
            Assert.True(counter is null || counter.TryGetValue("value", out var value) && value.IsInt64 && value.AsInt64 == 0L);
        }
        foreach (var name in PdfDocumentExtractionSchemas.Collections)
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument("sourceKind", PaymentSupplementBson.SourceKind)).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("evidenceUploadReservations").Find(new BsonDocument("purpose", "PaymentSupplementPdf")).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("evidencePins").Find(new BsonDocument("associationKind", "PaymentSupplement")).AnyAsync());
        foreach (var (name, filter) in new[] {
            ("evidenceLineages", new BsonDocument("purpose", "PaymentSupplementPdf")),
            ("evidenceReadLeases", new BsonDocument("target.kind", "PaymentSupplement")),
            ("auditEvents", new BsonDocument("evidence.target.kind", "PaymentSupplement")),
            ("idempotencyRecords", new BsonDocument("operation", new BsonDocument("$in", new BsonArray { "evidence.payment-supplement-pdf.reserve", "evidence.payment-supplement-pdf.complete" }))) })
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(filter).AnyAsync());
    }
    internal static async Task EnsureOwnersAsync(IMongoDatabase db)
    {
        foreach (var name in PaymentSupplementSchemas.Collections) {
            if (await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleOrDefaultAsync() is null)
                await db.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> { Validator = PaymentSupplementSchemas.Validator(name),
                    ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
            foreach (var key in PaymentSupplementSchemas.Keys(name)) await db.GetCollection<BsonDocument>(name).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }));
        }
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(db, clock));
        Assert.Equal("Payment supplement migration found incompatible retained storage", error.Message); Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
    }
    private static BsonDocument ExpectedExtensions(BsonDocument before)
    {
        var expected = before.DeepClone().AsBsonDocument;
        expected["outboxEvents"]["definition"]["options"]["validator"] = PaymentSupplementSchemas.ExtendOutbox(expected["outboxEvents"]["definition"]["options"]["validator"].AsBsonDocument);
        foreach (var name in PdfDocumentExtractionSchemas.Collections)
            expected[name]["definition"]["options"]["validator"] = PaymentSupplementSchemas.ExtendDocumentOwner(name, expected[name]["definition"]["options"]["validator"].AsBsonDocument);
        foreach (var name in PaymentSupplementEvidenceSchemas.Collections)
            expected[name]["definition"]["options"]["validator"] = PaymentSupplementEvidenceSchemas.Extend(name, expected[name]["definition"]["options"]["validator"].AsBsonDocument);
        foreach (var name in PaymentSupplementSchemas.Collections) expected.Remove(name);
        return expected;
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
