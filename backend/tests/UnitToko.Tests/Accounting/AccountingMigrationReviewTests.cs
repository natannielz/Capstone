using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Accounting;
[Collection("Mongo")]
public sealed class AccountingMigrationReviewTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("fingerprint")]
    [InlineData("balanced_mutation")]
    [InlineData("missing_audit")]
    [InlineData("audit_fingerprint")]
    [InlineData("audit_actor")]
    [InlineData("extreme_date")]
    [InlineData("forged_stage_audit")]
    public async Task Corruption_rejects_before_target_ddl_or_counter_write(string corruption)
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.CommandAsync("first");
        var journals = data.DB.GetCollection<BsonDocument>("journalEntries");
        var journal = await journals.Find(new BsonDocument()).SingleAsync();
        if (corruption == "fingerprint")
            await journals.UpdateOneAsync(new BsonDocument("_id", journal["_id"]), Builders<BsonDocument>.Update.Set("fingerprint", new string ('b', 64)));
        if (corruption == "balanced_mutation")
            await journals.UpdateOneAsync(new BsonDocument("_id", journal["_id"]), Builders<BsonDocument>.Update.Set("total", new BsonDecimal128(200m)).Set("lines.0.debit", new BsonDecimal128(200m)).Set("lines.1.credit", new BsonDecimal128(200m)));
        if (corruption == "missing_audit")
            await data.DB.GetCollection<BsonDocument>("accountingAuditEvents").DeleteOneAsync(new BsonDocument("journalId", journal["_id"]));
        if (corruption == "audit_fingerprint")
            await data.DB.GetCollection<BsonDocument>("accountingAuditEvents").UpdateOneAsync(new BsonDocument("journalId", journal["_id"]), Builders<BsonDocument>.Update.Set("fingerprint", new string ('b', 64)));
        if (corruption == "audit_actor")
            await data.DB.GetCollection<BsonDocument>("accountingAuditEvents").UpdateOneAsync(new BsonDocument("journalId", journal["_id"]), Builders<BsonDocument>.Update.Set("actorId", 999L));
        if (corruption == "extreme_date")
        {
            await data.DB.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "journalEntries" }, { "validator", new BsonDocument() } });
            await journals.UpdateOneAsync(new BsonDocument("_id", journal["_id"]), Builders<BsonDocument>.Update.Set("createdAt", new BsonDateTime(long.MaxValue)));
        }

        if (corruption == "forged_stage_audit")
        {
            await AccountingPeriodTests.SeedApprovalAsync(data, "2026-09");
            var submitted = await data.DB.GetCollection<BsonDocument>("accountingPeriodHistory").Find(new BsonDocument("stage", "submitted")).SingleAsync();
            await data.DB.GetCollection<BsonDocument>("accountingAuditEvents").InsertOneAsync(new BsonDocument { { "_id", 9000L }, { "schemaVersion", 1 }, { "bookId", 1L }, { "eventKey", $"accounting:period:1:{submitted["version"].AsInt64}" }, { "kind", "period.closed" }, { "journalId", BsonNull.Value }, { "periodId", 1L }, { "fingerprint", new string ('a', 64) }, { "actorId", 666L }, { "actorGrantVersion", 1L }, { "createdAt", data.Access.Clock.UtcNow.UtcDateTime }, { "reason", "Forged synthetic audit" }, { "correlationId", "test" } });
        }

        await ResetMigrationAsync(data.DB);
        var before = await TargetSnapshotAsync(data.DB);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Access.Clock).ApplyAsync());
        Assert.Equal(before, await TargetSnapshotAsync(data.DB));
    }

    [Fact]
    public async Task First_history_cannot_claim_closed_without_submission_and_approval()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.DB.GetCollection<BsonDocument>("periods").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("version", 2L).Set("state", "closed").Set("approvedRevision", 0L).Set("snapshotHash", new string ('a', 64)).Set("lastHistoryId", 1L));
        await data.DB.GetCollection<BsonDocument>("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("closedThrough", "2026-09"));
        await data.DB.GetCollection<BsonDocument>("accountingPeriodHistory").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 }, { "bookId", 1L }, { "periodId", 1L }, { "month", "2026-09" }, { "version", 2L }, { "dataRevision", 0L }, { "stage", "closed" }, { "previousHistoryId", BsonNull.Value }, { "snapshotHash", new string ('a', 64) }, { "actorId", 900L }, { "actorGrantVersion", 1L }, { "createdAt", data.Access.Clock.UtcNow.UtcDateTime }, { "reason", "Synthetic malformed history" }, { "correlationId", "test" } });
        await ResetMigrationAsync(data.DB);
        var before = await TargetSnapshotAsync(data.DB);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Access.Clock).ApplyAsync());
        Assert.Equal(before, await TargetSnapshotAsync(data.DB));
    }

    [Fact]
    public async Task Populated_retry_preserves_extra_restrictions_and_advances_only_low_counters()
    {
        await using var data = await AccountingData.CreateAsync(fixture);
        await data.CommandAsync("first");
        await AccountingPeriodTests.SeedApprovalAsync(data, "2026-10");
        await data.Uow.ExecuteAsync(async (session, token) =>
        {
            await new AccountingPeriodStore(data.DB, data.Access.Clock).CloseApprovedAsync(session, data.Actor, AccountingData.Book, "2026-10", AccountingData.Metadata, token);
            return 0;
        });
        var books = await (await data.DB.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "accountingBooks") })).SingleAsync();
        var validator = new BsonDocument("$and", new BsonArray { books["options"]["validator"], new BsonDocument("canonicalKey", new BsonDocument("$ne", "forbidden")) });
        await data.DB.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "accountingBooks" }, { "validator", validator } });
        await data.DB.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "journalEntries"), Builders<BsonDocument>.Update.Set("value", 0L));
        await data.DB.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "accountingAuditEvents"), Builders<BsonDocument>.Update.Set("value", 9000L));
        await ResetMigrationAsync(data.DB);
        var migration = new CollectionMigrations(data.DB, data.Access.Clock);
        await migration.ApplyAsync();
        await migration.ApplyAsync();
        Assert.Equal(2L, (await new IdAllocator(data.DB).NextAsync("journalEntries")).Value);
        Assert.Equal(9001L, (await new IdAllocator(data.DB).NextAsync("accountingAuditEvents")).Value);
        var after = await (await data.DB.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "accountingBooks") })).SingleAsync();
        Assert.True(after["options"]["validator"].ToJson().Contains("forbidden", StringComparison.Ordinal));
        var index = (await (await data.DB.GetCollection<BsonDocument>("journalEntries").Indexes.ListAsync()).ToListAsync()).Single(row => row["name"] == "uq_sourceEvent");
        Assert.True(index["unique"].AsBoolean);
        Assert.True(index.Contains("partialFilterExpression"));
    }

    internal static Task ResetMigrationAsync(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations").DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$gte", 12)));
    private static async Task<string> TargetSnapshotAsync(IMongoDatabase db)
    {
        var definitions = await (await db.ListCollectionsAsync()).ToListAsync();
        var targets = definitions.Where(row => AccountingSchemas.Collections.Contains(row["name"].AsString)).OrderBy(row => row["name"].AsString).Select(row => row.ToJson());
        var counters = await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        return string.Join("\n", targets) + string.Join("\n", counters.Select(row => row.ToJson()));
    }
}
