using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Reporting;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Reporting;

[Collection("Mongo")]
public sealed class AccountingReportMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_interrupted_read_owner_preserves_stronger_validator_uuid_and_every_predecessor_without_seeding_or_rewriting_counters()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture); await RestoreV83Async(data.Database);
        var protectedState = await SnapshotAsync(data.Database, excludeReport: true);
        var stronger = new BsonDocument("$and", new BsonArray { AccountingReportSchemas.Validator(), new BsonDocument("actorId", new BsonDocument("$ne", 999L)) });
        if (await AccountingReportRecords.DefinitionAsync(data.Database, AccountingReportRecords.Events, default) is null)
            await data.Database.CreateCollectionAsync(AccountingReportRecords.Events, new CreateCollectionOptions<BsonDocument> {
                Validator = stronger, ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
        else await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountingReportRecords.Events }, { "validator", stronger } });
        var definition = await AccountingReportRecords.DefinitionAsync(data.Database, AccountingReportRecords.Events, default);
        await AccountingReportTestSchema.ApplyAsync(data.Database, data.Clock);
        await AccountingReportRecords.RequireStorageAsync(data.Database, default);
        Assert.Equal(definition!.ToBson(), (await AccountingReportRecords.DefinitionAsync(data.Database, AccountingReportRecords.Events, default))!.ToBson());
        Assert.Equal(protectedState.ToBson(), (await SnapshotAsync(data.Database, excludeReport: true)).ToBson());
        Assert.False(await data.Database.GetCollection<BsonDocument>(AccountingReportRecords.Events).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", AccountingReportRecords.Events)).AnyAsync());
        var completed = await SnapshotAsync(data.Database); await AccountingReportTestSchema.ApplyAsync(data.Database, data.Clock);
        Assert.Equal(completed.ToBson(), (await SnapshotAsync(data.Database)).ToBson());
    }

    [Fact]
    public async Task Consumed_or_malformed_read_allocator_weakened_pdf_predecessor_and_hidden_read_index_refuse_before_any_ddl()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture); await AccountingReportTestSchema.ApplyAsync(data.Database, data.Clock);
        await RestoreV83Async(data.Database);
        var counters = data.Database.GetCollection<BsonDocument>("idCounters");
        foreach (var invalid in new BsonValue[] { new BsonInt64(1), new BsonInt32(0), new BsonString("0") }) {
            // Malformed counters deliberately bypass their own validator for this retained-data rehearsal.
            await counters.InsertOneAsync(new BsonDocument { { "_id", AccountingReportRecords.Events }, { "value", invalid } }, new InsertOneOptions { BypassDocumentValidation = true });
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await counters.DeleteOneAsync(new BsonDocument("_id", AccountingReportRecords.Events)); }
        }
        var pdfTable = PdfEvidenceValidationSchemas.Collections[0];
        var pdfDefinition = await AccountingReportRecords.DefinitionAsync(data.Database, pdfTable, default);
        var pdfValidator = pdfDefinition!["options"]["validator"];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", pdfTable }, { "validator", new BsonDocument() } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", pdfTable }, { "validator", pdfValidator } }); }
        var index = AccountingReportSchemas.Keys[0];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountingReportRecords.Events },
            { "index", new BsonDocument { { "name", index.Name }, { "hidden", true } } } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountingReportRecords.Events },
            { "index", new BsonDocument { { "name", index.Name }, { "hidden", false } } } }); }
    }

    [Fact]
    public async Task Unsupported_preinstallation_read_and_inconsistent_persisted_chart_are_preserved_and_never_adopted()
    {
        await using var data = await AccountingReportTestData.CreateAsync(fixture, UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.Version); await RestoreV83Async(data.Database);
        var unowned = EmptyReadEvent(data.Clock.UtcNow);
        await data.Rows(AccountingReportRecords.Events).InsertOneAsync(unowned);
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Rows(AccountingReportRecords.Events).DeleteOneAsync(new BsonDocument("_id", unowned["_id"])); }
        await data.PostAsync();
        var chart = await data.Rows("accountingChartVersions").Find(new BsonDocument()).SingleAsync();
        var code = chart["mappings"][0]["accountCode"];
        await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", chart["_id"]), Builders<BsonDocument>.Update.Set("mappings.0.accountCode", "WRONG"));
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", chart["_id"]), Builders<BsonDocument>.Update.Set("mappings.0.accountCode", code)); }
        await AccountingReportTestSchema.ApplyAsync(data.Database, data.Clock);
        Assert.Single((await data.Service.ReadAsync(data.Reader, AccountingReportTestData.Key, data.EndDate, AccountingReportTestData.Metadata)).Journals);
    }

    // Empty test-only suffix rehearsal. No collection, stronger operator validator, UUID or allocator is discarded.
    internal static async Task RestoreV83Async(IMongoDatabase database)
    {
        await UnitToko.Tests.Documents.PdfEvidenceAdmissionMigrationTests.RestoreV84Async(database);
        await UnitToko.Tests.Documents.PdfDocumentExtractionMigrationTests.UndoM86OutboxExtensionAsync(database);
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        if (history.Count <= 83) return;
        Assert.Equal(84, history.Count); Assert.True(MigrationHistory.IsValid(history, 84));
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await AccountingReportRecords.RequireStorageAsync(database, default);
        Assert.False(await database.GetCollection<BsonDocument>(AccountingReportRecords.Events).Find(new BsonDocument()).AnyAsync());
        Assert.False(await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", AccountingReportRecords.Events)).AnyAsync());
        Assert.Equal(1L, (await database.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 84 }, { "state", "applied" } })).DeletedCount);
    }
    private static async Task RefusesAsync(IMongoDatabase database, IClock clock)
    {
        var before = await SnapshotAsync(database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AccountingReportTestSchema.ApplyAsync(database, clock));
        Assert.Equal("Accounting report migration found incompatible retained storage", error.Message);
        Assert.Equal(before.ToBson(), (await SnapshotAsync(database)).ToBson());
    }
    private static BsonDocument EmptyReadEvent(DateTimeOffset now)
    {
        var row = new BsonDocument { { "_id", 901L }, { "schemaVersion", 1 }, { "actorId", 601L }, { "actorGrantVersion", 1L },
            { "permission", "report.read" }, { "operation", AccountingReportRecords.BooksOperation }, { "purpose", "accounting-reporting" },
            { "correlationId", "unsupported-preinstallation-read" }, { "recordedAt", now.UtcDateTime }, { "expiresAt", BsonNull.Value },
            { "snapshotReadId", BsonNull.Value }, { "bookId", BsonNull.Value }, { "key", BsonNull.Value }, { "endDate", BsonNull.Value },
            { "bookHash", BsonNull.Value }, { "sourcesHash", BsonNull.Value }, { "chartSetHash", BsonNull.Value }, { "snapshotHash", new string('a', 64) },
            { "resultCount", 0 }, { "journalCount", 0 }, { "lineCount", 0 }, { "accountCount", 0 }, { "selection", new BsonArray() } };
        row.Add("eventHash", AccountingReportRecords.EventHash(row)); return row;
    }
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database, bool excludeReport = false)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString;
            if (name is "schemaMigrations" or "migrationLocks" || excludeReport && name == AccountingReportRecords.Events) continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
