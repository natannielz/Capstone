using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Ordering.OrderSpendingPolicyTestData;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderSpendingPolicyMigrationTests(MongoFixture fixture)
{
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public async Task Empty_partial_upgrade_preserves_real_profiles_stronger_guards_and_prior_history_without_seeding_rules() {
        await using var data = await D.CreateAsync(fixture); await RestoreV58Async(data.Organizations);
        var guard = new BsonDocument("target.id", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Heads },
            { "validator", new BsonDocument("$and", new BsonArray { OrderSpendingPolicySchemas.Validator(D.Heads), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Heads).Indexes.DropOneAsync("ux_" + D.Heads + "_0");
        await data.Rows(D.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("updatedAt", 1), new CreateIndexOptions { Name = "operator_spending_resume_lookup" }));
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Heads }, { "value", 17L } });
        var history = await HistoryAsync(data.Database); var before = await DefinitionsAsync(data.Database); var sources = await SourcesAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(58))); Assert.Equal(sources, await SourcesAsync(data.Database));
        Assert.Equal(17L, (await data.Rows("idCounters").Find(new BsonDocument("_id", D.Heads)).SingleAsync())["value"].AsInt64);
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !OrderSpendingPolicySchemas.Collections.Contains(row["name"].AsString)),
            after.Where(row => !OrderSpendingPolicySchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in OrderSpendingPolicySchemas.Collections) {
            var previous = Assert.Single(before, row => row["name"] == name); var current = Assert.Single(after, row => row["name"] == name);
            Assert.Equal(previous["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, OrderSpendingPolicySchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in OrderSpendingPolicySchemas.Keys(name)) Assert.True(Assert.Single(indexes, row => row["key"] == key)["unique"].AsBoolean);
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == D.Heads)["options"]["validator"].AsBsonDocument, guard));
        Assert.Contains(await (await data.Rows(D.Heads).Indexes.ListAsync()).ToListAsync(), row => row["name"] == "operator_spending_resume_lookup");
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(sources, await SourcesAsync(data.Database));
    }
    [Theory(Skip = Retired.HistoricalUpgrade)]
    [InlineData("row")] [InlineData("ttl")] [InlineData("unique")] [InlineData("counter")] [InlineData("orphan")]
    public async Task Unsafe_partial_or_surviving_effect_refuses_before_any_spending_policy_DDL(string fault) {
        await using var data = await D.CreateAsync(fixture); await RestoreV58Async(data.Organizations);
        if (fault == "row") await data.Rows(D.Heads).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("policyId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Heads }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await DefinitionsAsync(data.Database); var state = await SourcesAsync(data.Database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal("Order spending policy migration preflight failed", error.Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(state, await SourcesAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 59 }, { "state", "applied" } }).AnyAsync());
    }
    [Fact]
    public async Task Database_rejects_malformed_real_version_and_duplicate_target_without_overwriting_approved_history() {
        await using var data = await D.CreateAsync(fixture); var result = await data.DecideAsync(await data.ProposeAsync("policy-schema"), "policy-schema-approved");
        var version = await data.Rows(D.Versions).Find(new BsonDocument("_id", result["versionId"])).SingleAsync();
        foreach (var fault in new[] { "money-type", "mode", "window", "source", "predecessor" }) {
            var invalid = version.DeepClone().AsBsonDocument;
            if (fault == "money-type") invalid["details"]["rule"]["threshold"] = new BsonDouble(500000.25);
            if (fault == "mode") invalid["details"]["rule"]["mode"] = "not_required";
            if (fault == "window") invalid["details"]["validUntil"] = invalid["details"]["validFrom"];
            if (fault == "source") invalid["source"]["target"]["id"] = 12L;
            if (fault == "predecessor") invalid["version"] = 2L;
            var error = await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Versions).ReplaceOneAsync(new BsonDocument("_id", version["_id"]), invalid));
            Assert.Equal(121, error.WriteError.Code); Assert.Equal(version, await data.Rows(D.Versions).Find(new BsonDocument("_id", version["_id"])).SingleAsync());
        }
        var head = await data.Rows(D.Heads).Find(new BsonDocument()).SingleAsync(); var duplicate = head.DeepClone().AsBsonDocument; duplicate["_id"] = 999999L;
        Assert.Equal(11000, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Heads).InsertOneAsync(duplicate))).WriteError.Code);
        Assert.Equal(1, await data.Rows(D.Heads).CountDocumentsAsync(new BsonDocument()));
    }
    internal static async Task RestoreV58Async(OrganizationAddressTestData data) {
        await UnitToko.Tests.Accounting.AccountingBookSetupMigrationTests.RestoreV59Async(data);
        var history = await HistoryAsync(data.Database); Assert.Equal(59, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 59 }, history.Skip(58).Select(row => row["_id"].AsInt32));
        foreach (var name in OrderSpendingPolicySchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var prefix = new BsonRegularExpression(@"^ordering\.spending\.policy(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 59 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(58)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> SourcesAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "idempotencyRecords", "auditEvents", "outboxEvents" })
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
