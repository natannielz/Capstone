using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class PdfEvidenceValidationMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_interrupted_owner_resumes_and_keeps_image_revalidation_contact_and_applied_history()
    {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database;
        await RestoreV82Async(db);
        var before = await ProtectedAsync(db); var history = await HistoryAsync(db);
        var candidate = await (await db.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", PdfEvidenceValidationRecords.Policies) })).SingleOrDefaultAsync();
        if (candidate is null) await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", PdfEvidenceValidationRecords.Policies },
            { "validator", PdfEvidenceValidationSchemas.Validator(PdfEvidenceValidationRecords.Policies) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        else await db.GetCollection<BsonDocument>(PdfEvidenceValidationRecords.Policies).Indexes.DropOneAsync("ux_" + PdfEvidenceValidationRecords.Policies);
        await ApplyAsync(db, data.Clock);
        Assert.Equal(before.ToBson(), (await ProtectedAsync(db)).ToBson());
        Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(82)));
        foreach (var table in PdfEvidenceValidationSchemas.Collections) {
            var definition = await DefinitionAsync(db, table);
            Assert.Equal(PdfEvidenceValidationSchemas.Validator(table), definition["options"]["validator"]);
            var indexes = await (await db.GetCollection<BsonDocument>(table).Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, row => row.GetValue("key", BsonNull.Value) == PdfEvidenceValidationSchemas.Key(table) && row.GetValue("unique", false).AsBoolean);
        }
        await ApplyAsync(db, data.Clock);
        Assert.Equal(83, (await HistoryAsync(db)).Count);
    }

    [Fact]
    public async Task Positive_and_malformed_allocators_and_unowned_rows_refuse_before_any_collection_mutation()
    {
        foreach (var fault in new[] { "positive", "malformed", "unowned" }) {
            await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database;
            await RestoreV82Async(db);
            if (fault == "unowned") await db.GetCollection<BsonDocument>(PdfEvidenceValidationRecords.Results)
                .InsertOneAsync(new BsonDocument("_id", 909L), new InsertOneOptions { BypassDocumentValidation = true });
            else await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument {
                { "_id", PdfEvidenceValidationRecords.Audits }, { "value", fault == "positive" ? new BsonInt64(1) : new BsonInt32(0) } },
                new InsertOneOptions { BypassDocumentValidation = true });
            var before = await SnapshotAsync(db);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(db, data.Clock));
            Assert.Equal("PDF evidence validation migration found incompatible retained storage", error.Message);
            Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
        }
    }

    [Fact]
    public async Task Unsafe_predecessor_and_unexplained_PDF_job_are_not_relabelled_or_repaired_by_migration()
    {
        foreach (var fault in new[] { "revalidation", "contact", "pdf-job" }) {
            await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database;
            await RestoreV82Async(db);
            if (fault == "pdf-job") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument {
                { "kind", PdfEvidenceValidationRecords.Kind }, { "status", "pending" }, { "attempts", 0 } },
                new InsertOneOptions { BypassDocumentValidation = true });
            else {
                var table = fault == "contact" ? AccountContactSchemas.Collections[0] : EvidenceImageRevalidationSchemas.Collections[0];
                await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validationAction", "warn" } });
            }
            var before = await SnapshotAsync(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(db, data.Clock));
            Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
        }
    }

    // Independent migration harness while the root owns ordered registry integration.
    private static async Task ApplyAsync(IMongoDatabase db, IClock clock)
    {
        var lease = await new MigrationGate(db, clock, 83).AcquireAsync(83, default);
        if (lease is null) return;
        try { await new PdfEvidenceValidationMigration(db).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal static async Task RestoreV82Async(IMongoDatabase db)
    {
        var history = await HistoryAsync(db); if (history.Count <= 82) return;
        Assert.Equal(83, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in PdfEvidenceValidationSchemas.Collections) {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        }
        Assert.False(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("kind", PdfEvidenceValidationRecords.Kind)).AnyAsync());
        var definition = await DefinitionAsync(db, "outboxEvents");
        var current = definition["options"]["validator"].AsBsonDocument;
        var after = PdfEvidenceValidationSchemas.ExtendedOutboxValidator(); var before = PdfEvidenceValidationSchemas.PreviousOutboxValidator();
        var found = false;
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, after)) { found = true; return before.DeepClone().AsBsonDocument; }
            var result = value.DeepClone().AsBsonDocument;
            if (result.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                result["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return result;
        }
        var restored = Replace(current); Assert.True(found);
        // No unknown row is silently relabelled under the original validator.
        Assert.False(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { restored })).AnyAsync());
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", restored },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 83 }, { "state", "applied" } })).DeletedCount);
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string table) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
    private static async Task<BsonDocument> ProtectedAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var table in EvidenceImageRevalidationSchemas.Collections.Concat(AccountContactSchemas.Collections).Concat(EvidenceValidationSchemas.Collections))
            result[table] = await DefinitionAsync(db, table);
        return result;
    }
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var table = definition["name"].AsString; if (table is "schemaMigrations" or "migrationLocks") continue;
            result[table] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(table).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
