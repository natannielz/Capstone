using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SupplierClaimMigrationTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    [SupportedOSPlatform("windows")]
    public async Task Actual_supplier_original_OCR_and_matched_inspection_survive_claim_upgrade_but_missing_proofs_refuse_before_DDL() {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture);
        Assert.Equal(1, (await new IdAllocator(data.Database).NextAsync("supplierDocumentProcessingPolicies")).Value);
        var runtime = await SupplierDocumentOcrTestData.PrepareNativeAsync(data); await data.SubmitOriginalAsync(); Assert.Equal("completed", await runtime.RunOnceAsync());
        var extracted = await data.Evidence.ReadExtractionAsync(data.EvidenceReader, data.DraftId, 1, data.Reference, new("supplier-document-ocr-view", "claim-upgrade-before"));
        Assert.Equal("succeeded", extracted.State); Assert.NotNull(extracted.ResultId);
        var match = await data.MatchAsync("retained-invoice-match", data.MatchInput() with { OcrResultId = extracted.ResultId });
        var submitted = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument("_id", data.DocumentId)).SingleAsync();
        Assert.Equal(data.DraftId, submitted["draftId"].AsInt64);
        await RestoreV70Async(data.Database);
        var gate = new MigrationGate(data.Database, data.Clock, 71); var lease = Assert.IsType<MigrationLease>(await gate.AcquireAsync(71, CancellationToken.None));
        try {
            var oldError = await Assert.ThrowsAsync<InvalidOperationException>(() => SupplierDocumentEvidenceSchemas.RequireHistoriesAsync(data.Database, lease, CancellationToken.None));
            Assert.Equal("Supplier document evidence migration preflight failed: supplier pin before acceptance", oldError.Message);
        } finally { await lease.ReleaseAsync(); }
        foreach (var (table, filter) in new[] {
            ("supplierDocumentMatches", new BsonDocument("_id", match["matchId"])),
            ("supplierDocumentInspectionHeads", new BsonDocument()), ("supplierDocumentOrderHeads", new BsonDocument()),
            ("supplierDocumentExtractionAudits", new BsonDocument()),
            ("evidencePinAudits", new BsonDocument("pinId", submitted["pinId"])) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try { await RefusesAsync(data.Database, data.Clock); } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "supplierdocument.ocr.requested")).SingleAsync();
        Assert.Equal(1, (await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]),
            new BsonDocument("$unset", new BsonDocument("processingReceipt", "")), new UpdateOptions { BypassDocumentValidation = true })).ModifiedCount);
        try { await RefusesAsync(data.Database, data.Clock, "Supplier document evidence migration preflight failed: outboxEvents"); }
        finally { await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), job); }
        var before = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        var policy = await data.Rows(SupplierTestData.Policies).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Limit(1).SingleAsync();
        Assert.DoesNotContain(before["idCounters"].AsBsonArray, row => row["_id"] == SupplierTestData.Policies);
        var expected = before.DeepClone().AsBsonDocument;
        // The retained operator policy has no allocator yet. M71 establishes its exact high-water value only after every preflight succeeds.
        var policyCounter = new BsonDocument { { "_id", SupplierTestData.Policies }, { "value", policy["_id"] } };
        expected["idCounters"] = new BsonArray(before["idCounters"].AsBsonArray.Append(policyCounter).OrderBy(row => row["_id"].AsString, StringComparer.Ordinal));
        await ApplyHistorical71Async(data.Database, data.Clock);
        AssertStateEqual(expected, await StateAsync(data.Database)); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        await EmptyAsync(data.Database); Assert.Equal(match, await data.Service.LookupAsync(data.Matcher, SupplierDocumentTestData.MatchOperation, "retained-invoice-match"));
        var after = await data.Evidence.ReadExtractionAsync(data.EvidenceReader, data.DraftId, 1, data.Reference, new("supplier-document-ocr-view", "claim-upgrade-after"));
        Assert.Equal(extracted.ResultId, after.ResultId); Assert.Equal(extracted.Provenance, after.Provenance); Assert.Equal(extracted.Lines, after.Lines); Assert.Equal(extracted.Evidence, after.Evidence);
        await using var original = await data.Evidence.OpenReadAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, SupplierDocumentTestData.ReadMetadata);
        using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_partial_claim_upgrade_preserves_stronger_guards_and_rejects_incompatible_indexes_or_orphan_effects_without_seeding() {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV70Async(data.Database);
        const string table = "supplierClaimRequests"; var definition = await DefinitionAsync(data.Database, table); var original = definition["options"]["validator"].AsBsonDocument;
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { original, guard }) } });
        var rows = data.Database.GetCollection<BsonDocument>(table);
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("claimId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unknown_claim_partial", Unique = true, PartialFilterExpression = new BsonDocument("version", 1L) }));
        try { await RefusesAsync(data.Database, data.Clock); } finally { await rows.Indexes.DropOneAsync("unknown_claim_partial"); }
        var orphan = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "procurement.supplierclaim.request" }, { "sourceKey", "procurement.supplierclaim.command:999" } };
        await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(orphan, new InsertOneOptions { BypassDocumentValidation = true });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("outboxEvents").DeleteOneAsync(new BsonDocument("_id", orphan["_id"]))).DeletedCount); }
        var before = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        await ApplyHistorical71Async(data.Database, data.Clock);
        Assert.Equal(before, await StateAsync(data.Database)); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, table))["options"]["validator"].AsBsonDocument, guard)); await EmptyAsync(data.Database);
        await ApplyHistorical71Async(data.Database, data.Clock); Assert.Equal(definitions, await DefinitionsAsync(data.Database));
    }

    internal static async Task RestoreV70Async(IMongoDatabase db) {
        await ProcurementStockReceiptMigrationTests.RestoreV71Async(db);
        var history = await HistoryAsync(db); if (history.Count <= 70) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(71, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"])); await EmptyAsync(db);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.supplierclaim(?:[.:]|$)"))).AnyAsync());
        // No prior validator is changed by M71. Only the proven empty suffix receipt is removed.
        Assert.Equal(1, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 71 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(70)), await HistoryAsync(db));
    }
    // Exercise the historical migration under test; successor index transitions are validated by their own migration cases.
    private static async Task ApplyHistorical71Async(IMongoDatabase db, IClock clock) {
        var lease = await new MigrationGate(db, clock, 71).AcquireAsync(71, CancellationToken.None);
        if (lease is null) return;
        try { await new SupplierClaimMigration(db, clock).ApplyAsync(lease, CancellationToken.None); await lease.CompleteAsync(CancellationToken.None); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static async Task EmptyAsync(IMongoDatabase db) {
        foreach (var name in SupplierClaimSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock, string expected = "Supplier claim migration preflight failed") {
        var definitions = await DefinitionsAsync(db); var indexes = await IndexesAsync(db); var state = await StateAsync(db); var history = new BsonArray((await HistoryAsync(db)).Take(70));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync()); Assert.Equal(expected, error.Message);
        Assert.Equal(definitions, await DefinitionsAsync(db)); Assert.Equal(indexes, await IndexesAsync(db)); Assert.Equal(state, await StateAsync(db));
        Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(70))); Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 71 }, { "state", "applied" } }).AnyAsync());
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> IndexesAsync(IMongoDatabase db) {
        var result = new BsonDocument(); foreach (var row in await DefinitionsAsync(db)) result[row["name"].AsString] = new BsonArray((await (await db.GetCollection<BsonDocument>(row["name"].AsString).Indexes.ListAsync()).ToListAsync()).OrderBy(index => index["name"].AsString)); return result;
    }
    private static async Task<BsonDocument> StateAsync(IMongoDatabase db) {
        var result = new BsonDocument(); foreach (var row in await DefinitionsAsync(db)) {
            var name = row["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        } return result;
    }
    private static void AssertStateEqual(BsonDocument expected, BsonDocument actual) {
        var deltas = new List<string>();
        foreach (var name in expected.Names.Union(actual.Names, StringComparer.Ordinal)) {
            if (!expected.TryGetValue(name, out var before) || !actual.TryGetValue(name, out var after)) { deltas.Add(name + ": collection added or removed"); continue; }
            if (before.Equals(after)) continue;
            var oldRows = before.AsBsonArray.Select(value => value.AsBsonDocument).ToDictionary(row => row["_id"].ToJson());
            var newRows = after.AsBsonArray.Select(value => value.AsBsonDocument).ToDictionary(row => row["_id"].ToJson());
            var count = deltas.Count;
            foreach (var id in oldRows.Keys.Union(newRows.Keys, StringComparer.Ordinal)) {
                if (!oldRows.TryGetValue(id, out var oldRow)) { deltas.Add(name + "[" + id + "]: added"); continue; }
                if (!newRows.TryGetValue(id, out var newRow)) { deltas.Add(name + "[" + id + "]: removed"); continue; }
                if (oldRow.Equals(newRow)) continue;
                var fields = oldRow.Names.Union(newRow.Names, StringComparer.Ordinal).Where(field =>
                    !oldRow.TryGetValue(field, out var oldValue) || !newRow.TryGetValue(field, out var newValue) || !oldValue.Equals(newValue));
                deltas.Add(name + "[" + id + "]: changed fields " + string.Join(",", fields));
            }
            if (deltas.Count == count) deltas.Add(name + ": row or field order changed");
        }
        Assert.True(deltas.Count == 0, "Unexpected retained state changes: " + string.Join("; ", deltas.Take(12)));
        Assert.Equal(expected, actual);
    }
}
