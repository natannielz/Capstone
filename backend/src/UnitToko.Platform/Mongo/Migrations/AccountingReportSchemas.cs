using MongoDB.Bson;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Reporting;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class AccountingReportSchemas
{
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] Keys = [
        ("ix_accountingReport_actor_id", new BsonDocument { { "actorId", 1 }, { "_id", -1 } }, false),
        ("ix_accountingReport_snapshot", new BsonDocument { { "snapshotReadId", 1 }, { "_id", 1 } }, false)];
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", long.MaxValue } };
    private static BsonDocument Int(int maximum) => new() { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", maximum } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Key() => S.Object(new() { { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() },
        { "currency", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[A-Z]{3}\z" } } } });
    internal static BsonDocument Validator()
    {
        var fields = new BsonDocument {
            { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "actorId", Long() }, { "actorGrantVersion", Long(0) },
            { "permission", new BsonDocument("enum", new BsonArray { "report.read" }) },
            { "operation", new BsonDocument("enum", new BsonArray { AccountingReportRecords.BooksOperation,
                AccountingReportRecords.ReadOperation, AccountingReportRecords.ExportOperation }) },
            { "purpose", new BsonDocument("enum", new BsonArray { AccountingReportReadMetadata.ReportingPurpose }) },
            { "correlationId", S.Text(128) }, { "recordedAt", Date() }, { "expiresAt", Nullable(Date()) },
            { "snapshotReadId", Nullable(Long()) }, { "bookId", Nullable(Long()) }, { "key", Nullable(Key()) },
            { "endDate", Nullable(new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?!0000)[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } }) },
            { "bookHash", Nullable(Hash()) }, { "sourcesHash", Nullable(Hash()) }, { "chartSetHash", Nullable(Hash()) },
            { "snapshotHash", Hash() }, { "eventHash", Hash() }, { "resultCount", Int(AccountingReportRules.MaximumLines) },
            { "journalCount", Int(AccountingReportRules.MaximumJournals) }, { "lineCount", Int(AccountingReportRules.MaximumLines) },
            { "accountCount", Int(AccountingReportRules.MaximumAccounts) },
            { "selection", new BsonDocument { { "bsonType", "array" }, { "maxItems", AccountingReportRules.MaximumJournals },
                { "items", S.Object(new() { { "journalId", Long() }, { "contentHash", Hash() } }) } } }
        };
        var isList = new BsonDocument("$eq", new BsonArray { "$operation", AccountingReportRecords.BooksOperation });
        var allNull = new BsonArray(new[] { "expiresAt", "snapshotReadId", "bookId", "key", "endDate", "bookHash", "sourcesHash", "chartSetHash" }
            .Select(field => new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$" + field), "null" })));
        allNull.Add(new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", SafeArray()), 0 }));
        allNull.Add(new BsonDocument("$lte", new BsonArray { "$resultCount", AccountingReportRules.MaximumBooks }));
        foreach (var field in new[] { "journalCount", "lineCount", "accountCount" }) allNull.Add(new BsonDocument("$eq", new BsonArray { "$" + field, 0 }));
        var selectionIds = new BsonDocument("$map", new BsonDocument { { "input", SafeArray() }, { "as", "item" }, { "in", "$$item.journalId" } });
        var finite = new BsonArray {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", SafeArray()), "$journalCount" }),
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", selectionIds), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { selectionIds, new BsonArray() })) }),
            new BsonDocument("$eq", new BsonArray { "$resultCount", "$lineCount" }),
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$bookId"), "long" }),
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$key"), "object" }),
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$expiresAt"), "date" }),
            new BsonDocument("$gt", new BsonArray { "$expiresAt", "$recordedAt" }),
            new BsonDocument("$lte", new BsonArray { new BsonDocument("$subtract", new BsonArray { "$expiresAt", "$recordedAt" }),
                (long)AccountingReportRules.SnapshotLifetime.TotalMilliseconds }),
            new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$operation", AccountingReportRecords.ReadOperation }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$snapshotReadId"), "null" }),
                new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$snapshotReadId"), "long" }),
                    new BsonDocument("$ne", new BsonArray { "$snapshotReadId", "$_id" }) }) })
        };
        foreach (var field in new[] { "endDate", "bookHash", "sourcesHash", "chartSetHash" })
            finite.Add(new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$" + field), "string" }));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)),
            new BsonDocument("$expr", new BsonDocument("$cond", new BsonArray { isList, new BsonDocument("$and", allNull), new BsonDocument("$and", finite) })) });
    }
    private static BsonValue SafeArray() => new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", "$selection"), "$selection", new BsonArray() });
}
