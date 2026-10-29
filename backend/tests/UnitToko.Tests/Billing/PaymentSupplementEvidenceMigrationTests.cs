using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
public sealed class PaymentSupplementEvidenceMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task All_admission_read_pin_predecessor_UUIDs_and_operator_controls_survive_without_new_PDF_or_approval_seeds()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture); var db = data.Database;
        await PaymentSupplementMigrationTests.RestoreV86Async(db);
        var observed = new Dictionary<string, (BsonValue Uuid, BsonDocument Next)>();
        foreach (var name in PaymentSupplementEvidenceSchemas.Collections) {
            var definition = await Definition(db, name);
            // Fresh whole-registry setup already proves M1..M87. Rehearse the
            // actual reviewed M86 templates only after proving no new owner rows.
            var previous = PaymentSupplementEvidenceSchemas.PreviousValidator(name);
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument("$nor", new BsonArray { previous })).AnyAsync());
            var installed = new BsonDocument("$and", new BsonArray { previous, new BsonDocument("_id", new BsonDocument("$ne", 9191L)) });
            await Change(db, name, installed);
            observed.Add(name, (definition["info"]["uuid"], PaymentSupplementEvidenceSchemas.Extend(name, installed)));
        }
        Assert.Equal(CatalogEvidenceReadSchemas.Validator("evidenceReadLeases"),
            (await Definition(db, "evidenceReadLeases"))["options"]["validator"]["$and"][0]);
        await CatalogEvidenceReadSchemas.RequireStorageAsync(db, default);
        await PaymentSupplementMigrationTests.ApplyAsync(db, data.Clock);
        foreach (var (name, expected) in observed) {
            var definition = await Definition(db, name); Assert.Equal(expected.Uuid, definition["info"]["uuid"]);
            Assert.Equal(expected.Next, definition["options"]["validator"]);
        }
        await CatalogEvidenceReadSchemas.RequireStorageAsync(db, default);
        await SupplierDocumentEvidenceSchemas.RequireStorageAsync(db, default);
        var beforeReplay = await Definitions(db);
        await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.ApplyAsync(db, data.Clock);
        Assert.Equal(beforeReplay, await Definitions(db));
        Assert.True(await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.IsCompleteAsync(db, data.Clock));
        Assert.False(await db.GetCollection<BsonDocument>(PaymentSupplementBson.Policies).Find(new BsonDocument()).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("evidenceUploadReservations").Find(new BsonDocument("purpose", "PaymentSupplementPdf")).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("evidencePins").Find(new BsonDocument("associationKind", "PaymentSupplement")).AnyAsync());
    }
    [Fact]
    public async Task Unowned_supplement_admission_and_unknown_weakened_branch_refuse_before_any_existing_validator_or_UUID_changes()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture); var db = data.Database;
        await PaymentSupplementMigrationTests.RestoreV86Async(db);
        var table = db.GetCollection<BsonDocument>("evidenceUploadReservations");
        await table.InsertOneAsync(new BsonDocument { { "_id", 9111L }, { "purpose", "PaymentSupplementPdf" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db);
        try { await Assert.ThrowsAnyAsync<Exception>(() => PaymentSupplementMigrationTests.ApplyAsync(db, data.Clock)); Assert.Equal(before, await Definitions(db)); }
        finally { await table.DeleteOneAsync(new BsonDocument("_id", 9111L)); }
        var definition = await Definition(db, "evidenceReadLeases"); var original = definition["options"]["validator"].AsBsonDocument;
        await Change(db, "evidenceReadLeases", new("$or", new BsonArray { original, new BsonDocument() }));
        before = await Definitions(db);
        try { await Assert.ThrowsAnyAsync<Exception>(() => PaymentSupplementMigrationTests.ApplyAsync(db, data.Clock)); Assert.Equal(before, await Definitions(db)); }
        finally { await Change(db, "evidenceReadLeases", original); }
        Assert.False(await db.GetCollection<BsonDocument>(PaymentSupplementBson.Policies).Find(new BsonDocument()).AnyAsync());
    }
    private static async Task<BsonDocument> Definitions(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var name in PaymentSupplementEvidenceSchemas.Collections) result[name] = await Definition(db, name);
        return result;
    }
    private static Task<BsonDocument> Change(IMongoDatabase db, string name, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", name }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task<BsonDocument> Definition(IMongoDatabase db, string name) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
}
