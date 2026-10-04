using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Reporting;

internal static class AccountingReportRecords
{
    internal const string Events = "accountingReportReadEvents", BooksOperation = "accounting.report.books",
        ReadOperation = "accounting.report.read", ExportOperation = "accounting.report.export";
    internal static string Hash(BsonValue value) => CatalogRepository.Digest(value);
    internal static string EventHash(BsonDocument row) => Hash(new BsonDocument(row.Where(field => field.Name != "eventHash")));
    internal static BsonDocument StableBook(BsonDocument book) => new(book.Where(field => field.Name != "fence"));
    internal static PersistenceConflictException Broken() => new("accounting_report_integrity_unavailable", "The complete persisted book, chart, journal and reporting audit are required");
    internal static PersistenceConflictException Stale() => new("accounting_report_snapshot_stale", "Reload the report; its source configuration changed or its export reference expired");

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        await AccountingMigration.RequireInstalledStorageAsync(database, ct);
        var definition = await DefinitionAsync(database, Events, ct) ?? throw Broken();
        var keys = AccountingReportSchemas.Keys;
        if (!SafeDefinition(definition, AccountingReportSchemas.Validator())) throw Broken();
        var indexes = await (await database.GetCollection<BsonDocument>(Events).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (!SafeIndexes(indexes) || keys.Any(key => !indexes.Any(row => Matches(row, key)))) throw Broken();
    }
    internal static async Task<BsonDocument?> DefinitionAsync(IMongoDatabase database, string name, CancellationToken ct) =>
        await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
    internal static bool SafeDefinition(BsonDocument definition, BsonDocument required) =>
        UnitToko.Platform.Identity.AccountContactStorage.SafeDefinition(definition, required);
    internal static bool Matches(BsonDocument row, (string Name, BsonDocument Key, bool Unique) key) =>
        UnitToko.Platform.Identity.AccountContactStorage.Matches(row, key);
    internal static bool SafeIndexes(IReadOnlyList<BsonDocument> rows) =>
        UnitToko.Platform.Identity.AccountContactStorage.SafeIndexes(rows, AccountingReportSchemas.Keys);
}
