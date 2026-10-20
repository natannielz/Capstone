using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Accounting.AccountingBookSetupTestData;

namespace UnitToko.Tests.Accounting;

[Collection("Mongo")]
public sealed class AccountingBookSetupMigrationTests(MongoFixture fixture)
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_partial_upgrade_preserves_prior_history_stronger_guards_and_source_profiles_without_seeding_books()
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV59Async(data.Organizations);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Proposals }, { "validator", new BsonDocument("$and", new BsonArray { AccountingBookSetupSchemas.Validator(D.Proposals), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Proposals).Indexes.DropOneAsync("ux_" + D.Proposals + "_0");
        await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { Name = "operator_book_resume_lookup" }));
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Proposals }, { "value", 17L } });
        var history = await HistoryAsync(data.Database); var before = await DefinitionsAsync(data.Database); var sources = await PreservedAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(59))); Assert.Equal(sources, await PreservedAsync(data.Database));
        Assert.Equal(17L, (await data.Rows("idCounters").Find(new BsonDocument("_id", D.Proposals)).SingleAsync())["value"].AsInt64);
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !AccountingBookSetupSchemas.Collections.Contains(row["name"].AsString)), after.Where(row => !AccountingBookSetupSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in AccountingBookSetupSchemas.Collections) {
            var previous = Assert.Single(before, row => row["name"] == name); var current = Assert.Single(after, row => row["name"] == name);
            Assert.Equal(previous["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, AccountingBookSetupSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in AccountingBookSetupSchemas.Keys(name)) Assert.True(Assert.Single(indexes, row => row["key"] == key)["unique"].AsBoolean);
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == D.Proposals)["options"]["validator"].AsBsonDocument, guard));
        Assert.Contains(await (await data.Rows(D.Proposals).Indexes.ListAsync()).ToListAsync(), row => row["name"] == "operator_book_resume_lookup");
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(sources, await PreservedAsync(data.Database));
    }

    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Existing_valid_core_books_survive_migration_without_being_claimed_as_new_reviewed_setup()
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV59Async(data.Organizations);
        // Existing bounded legacy fixture only; the new positive workflow never uses this seeder.
        await AccountingTestDocuments.ConfigureAsync(data.Database); var before = await PreservedAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(before, await PreservedAsync(data.Database));
        foreach (var name in new[] { D.Books, D.Charts, D.Periods, "accountingAccounts" }) {
            var maximum = name == "accountingAccounts" ? before[D.Charts].AsBsonArray.SelectMany(row => row["mappings"].AsBsonArray).Max(row => row["accountId"].AsInt64)
                : before[name].AsBsonArray.Max(row => row["_id"].AsInt64);
            Assert.True((await data.Rows("idCounters").Find(new BsonDocument("_id", name)).SingleAsync())["value"].AsInt64 >= maximum);
        }
        foreach (var table in AccountingBookSetupSchemas.Collections) Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input()));
        Assert.Equal(before, await PreservedAsync(data.Database));
    }

    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("row")] [InlineData("ttl")] [InlineData("unique")] [InlineData("counter")] [InlineData("account-counter")] [InlineData("core")] [InlineData("orphan")]
    public async Task Unsafe_partial_or_orphan_command_effect_refuses_before_any_setup_DDL(string fault)
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV59Async(data.Organizations);
        if (fault == "row") await data.Rows(D.Proposals).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("actorId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Proposals }, { "value", long.MaxValue } });
        if (fault == "account-counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", "accountingAccounts" }, { "value", long.MaxValue } });
        if (fault == "core") await data.Rows(D.Books).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal("Accounting book setup migration preflight failed", error.Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 60 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Database_rejects_malformed_real_setup_and_duplicate_decision_without_replacing_history()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("book-schema"); await data.DecideAsync(proposal, "book-schema-approved");
        var original = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "currency", "month", "mapping", "source", "unknown" }) {
            var invalid = original.DeepClone().AsBsonDocument;
            if (fault == "currency") invalid["input"]["key"]["currency"] = "USD";
            if (fault == "month") invalid["input"]["initialMonth"] = "2026-13";
            if (fault == "mapping") invalid["input"]["mappings"][0]["accountCode"] = 123L;
            if (fault == "source") invalid["sources"]["shop"]["target"]["id"] = data.ForeignShopId;
            if (fault == "unknown") invalid["approved"] = true;
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Proposals).ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid))).WriteError.Code);
            Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        }
        var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync(); var bad = decision.DeepClone().AsBsonDocument; bad["bookId"] = BsonNull.Value;
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Decisions).ReplaceOneAsync(new BsonDocument("_id", decision["_id"]), bad))).WriteError.Code);
        var duplicate = decision.DeepClone().AsBsonDocument; duplicate["_id"] = 999999L;
        Assert.Equal(11000, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Decisions).InsertOneAsync(duplicate))).WriteError.Code);
        Assert.Equal(decision, await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync());
    }

    internal static async Task RestoreV59Async(OrganizationAddressTestData data)
    {
        await UnitToko.Tests.Procurement.ProcurementRequestMigrationTests.RestoreV60Async(data);
        var history = await HistoryAsync(data.Database); Assert.Equal(60, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 60 }, history.Skip(59).Select(row => row["_id"].AsInt32));
        foreach (var name in AccountingBookSetupSchemas.Collections) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        foreach (var name in AccountingBookSetupSchemas.Collections.Append("accountingAccounts")) Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        var prefix = new BsonRegularExpression(@"^accounting\.book(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 60 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(59)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "idempotencyRecords", "auditEvents", "outboxEvents", D.Books, D.Charts, D.Periods })
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
