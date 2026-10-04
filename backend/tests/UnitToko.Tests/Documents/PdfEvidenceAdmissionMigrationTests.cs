using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class PdfEvidenceAdmissionMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Interrupted_extensions_preserve_stronger_definitions_UUIDs_indexes_old_rows_and_other_owners_without_format_backfill() {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV84Async(db);
        foreach (var name in PdfEvidenceAdmissionSchemas.Collections) {
            var definition = await DefinitionAsync(db, name); var current = definition["options"]["validator"].AsBsonDocument;
            var retained = new BsonDocument("_id", new BsonDocument("$exists", true));
            var first = name == PdfEvidenceAdmissionSchemas.Collections[0] ? PdfEvidenceAdmissionSchemas.Extend(name, current) : current;
            await ChangeAsync(db, name, new BsonDocument("$and", new BsonArray { first, retained }));
        }
        var before = await SnapshotAsync(db);
        await ApplyAsync(db, data.Clock);
        var after = await SnapshotAsync(db); var preservedBefore = before.DeepClone().AsBsonDocument; var preservedAfter = after.DeepClone().AsBsonDocument;
        foreach (var name in PdfEvidenceAdmissionSchemas.Collections) {
            var prior = before[name]["definition"]["options"]["validator"].AsBsonDocument;
            Assert.Equal(PdfEvidenceAdmissionSchemas.Extend(name, prior), after[name]["definition"]["options"]["validator"]);
            Assert.Equal(before[name]["definition"]["info"]["uuid"], after[name]["definition"]["info"]["uuid"]);
            preservedBefore[name]["definition"]["options"].AsBsonDocument.Remove("validator"); preservedAfter[name]["definition"]["options"].AsBsonDocument.Remove("validator");
        }
        Assert.Equal(preservedBefore.ToBson(), preservedAfter.ToBson());
        Assert.Equal(85, await db.GetCollection<BsonDocument>("schemaMigrations").CountDocumentsAsync(new BsonDocument()));
        await PdfEvidenceAdmissionSchemas.RequireAsync(db, default); await ApplyAsync(db, data.Clock);
        Assert.Equal(after.ToBson(), (await SnapshotAsync(db)).ToBson());
    }
    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Actual_supplier_image_submission_pin_and_private_video_history_remain_valid_without_pdf_reinterpretation() {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture); await data.SubmitOriginalAsync();
        await RestoreV84Async(data.Database); var before = await data.StateAsync();
        await ApplyAsync(data.Database, data.Clock); Assert.Equal(before, await data.StateAsync());
        var view = await data.Service.ReadAsync(data.Actor, data.DraftId); Assert.NotNull(view);
        Assert.Equal("SupplierDocumentImage", (await data.Rows("evidenceUploadReservations").Find(new BsonDocument("_id", data.Reservation.ReservationId)).SingleAsync())["purpose"]);
        await using var original = await data.Evidence.OpenReadAsync(data.Actor, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, SupplierDocumentTestData.ReadMetadata);
        using var copy = new MemoryStream(); await original.Content.CopyToAsync(copy); Assert.Equal(data.Bytes, copy.ToArray());
        Assert.Empty(await data.Evidence.ListPdfsAsync(data.Actor, data.DraftId));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfEvidenceValidationRecords.Kind)).AnyAsync());
    }
    [Theory]
    [InlineData("unowned-pdf")]
    [InlineData("unowned-job")]
    [InlineData("malformed-policy")]
    [InlineData("weakened-revalidation")]
    [InlineData("weakened-contact")]
    [InlineData("unknown-admission")]
    [InlineData("malformed-counter")]
    public async Task Every_incompatible_candidate_or_predecessor_is_refused_before_the_first_DDL_and_never_relabelled(string fault) {
        await using var data = await StoreTestData.CreateAsync(fixture); var db = data.Database; await RestoreV84Async(db);
        if (fault == "unowned-pdf") await db.GetCollection<BsonDocument>("evidenceUploadReservations").InsertOneAsync(new BsonDocument { { "_id", 9899L }, { "purpose", "PaymentPdf" } }, new InsertOneOptions { BypassDocumentValidation = true });
        else if (fault == "unowned-job") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", PdfEvidenceValidationRecords.Kind } }, new InsertOneOptions { BypassDocumentValidation = true });
        else if (fault == "malformed-policy") await db.GetCollection<BsonDocument>(PdfEvidenceValidationRecords.Policies).InsertOneAsync(new BsonDocument("_id", 9899L), new InsertOneOptions { BypassDocumentValidation = true });
        else if (fault.StartsWith("weakened-", StringComparison.Ordinal)) await db.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", fault == "weakened-contact" ? AccountContactSchemas.Collections[0] : EvidenceImageRevalidationSchemas.Collections[0] }, { "validationAction", "warn" } });
        else if (fault == "unknown-admission") await ChangeAsync(db, "evidenceLineages", new BsonDocument("purpose", new BsonDocument("$exists", true)));
        else await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "evidenceIds" }, { "value", 0 } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await SnapshotAsync(db); await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(db, data.Clock));
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
    }
    private static async Task ApplyAsync(IMongoDatabase db, IClock clock) {
        var lease = await new MigrationGate(db, clock, 85).AcquireAsync(85, default); if (lease is null) return;
        try { await new PdfEvidenceAdmissionMigration(db).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    // Exact empty suffix only; this is test rehearsal, never a production downgrade.
    internal static async Task RestoreV84Async(IMongoDatabase db) {
        var history = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        if (history.Count <= 84) return; Assert.Equal(85, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        foreach (var table in new[] { PdfEvidenceValidationRecords.Results, PdfEvidenceValidationRecords.Audits })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("kind", PdfEvidenceValidationRecords.Kind)).AnyAsync());
        foreach (var table in new[] { "evidenceLineages", "evidenceUploadReservations" })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument("purpose", new BsonDocument("$in", new BsonArray { "PaymentPdf", "SupplierDocumentPdf" }))).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("auditEvents").Find(new BsonDocument("evidence.purpose", new BsonDocument("$in", new BsonArray { "PaymentPdf", "SupplierDocumentPdf" }))).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("operation", new BsonRegularExpression(@"^evidence\.(payment|supplier-document)-pdf\."))).AnyAsync());
        var changes = new List<(string Name, BsonDocument Restored)>();
        foreach (var name in PdfEvidenceAdmissionSchemas.Collections) {
            var definition = await DefinitionAsync(db, name); Assert.True(PdfEvidenceAdmissionSchemas.SafeDefinition(definition));
            var current = definition["options"]["validator"].AsBsonDocument;
            var before = PdfEvidenceAdmissionSchemas.PreviousValidator(name); var after = PdfEvidenceAdmissionSchemas.Validator(name); var found = false;
            BsonDocument Replace(BsonDocument value) {
                if (CatalogSchemas.SemanticEquals(value, after)) { found = true; return before.DeepClone().AsBsonDocument; }
                var result = value.DeepClone().AsBsonDocument;
                if (result.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                    result["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
                return result;
            }
            var restored = Replace(current); Assert.True(found);
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument("$nor", new BsonArray { restored })).AnyAsync());
            changes.Add((name, restored));
        }
        foreach (var item in changes) await ChangeAsync(db, item.Name, item.Restored);
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 85 }, { "state", "applied" } })).DeletedCount);
    }
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static Task<BsonDocument> ChangeAsync(IMongoDatabase db, string name, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", name }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db) {
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
