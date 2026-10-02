using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.ProcurementRequestTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementRequestMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_partial_upgrade_preserves_real_catalog_profiles_and_stronger_guards_without_seeding_requests()
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV60Async(data.Organizations);
        var guard = new BsonDocument("actorId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Requests }, { "validator", new BsonDocument("$and", new BsonArray { ProcurementRequestSchemas.Validator(D.Requests), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Requests).Indexes.DropOneAsync("ux_" + D.Requests + "_0");
        await data.Rows(D.Requests).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { Name = "operator_procurement_resume_lookup" }));
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Requests }, { "value", 17L } });
        var history = await HistoryAsync(data.Database); var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(60))); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.Equal(17L, (await data.Rows("idCounters").Find(new BsonDocument("_id", D.Requests)).SingleAsync())["value"].AsInt64);
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !ProcurementRequestSchemas.Collections.Contains(row["name"].AsString)), after.Where(row => !ProcurementRequestSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in ProcurementRequestSchemas.Collections) {
            var previous = Assert.Single(before, row => row["name"] == name); var current = Assert.Single(after, row => row["name"] == name);
            Assert.Equal(previous["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, ProcurementRequestSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in ProcurementRequestSchemas.Keys(name)) Assert.True(Assert.Single(indexes, row => row["key"] == key)["unique"].AsBoolean);
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == D.Requests)["options"]["validator"].AsBsonDocument, guard));
        Assert.Contains(await (await data.Rows(D.Requests).Indexes.ListAsync()).ToListAsync(), row => row["name"] == "operator_procurement_resume_lookup");
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        await data.NoBusinessEffectsAsync();
    }

    [Theory]
    [InlineData("row")] [InlineData("ttl")] [InlineData("unique")] [InlineData("counter")] [InlineData("orphan")]
    public async Task Unsafe_partial_or_orphan_effect_refuses_before_any_procurement_DDL(string fault)
    {
        await using var data = await D.CreateAsync(fixture); await RestoreV60Async(data.Organizations);
        if (fault == "row") await data.Rows(D.Requests).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(D.Requests).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await data.Rows(D.Requests).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("actorId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Requests }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal("Procurement request migration preflight failed", error.Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 61 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Database_rejects_malformed_real_request_and_duplicate_decision_without_replacing_reviewed_history()
    {
        await using var data = await D.CreateAsync(fixture); var submitted = await data.SubmitAsync("request-schema"); await data.DecideAsync(submitted, "request-schema-approved");
        var original = await data.Rows(D.Requests).Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "actor-type", "route", "quantity", "definition", "unknown" }) {
            var invalid = original.DeepClone().AsBsonDocument;
            if (fault == "actor-type") invalid["actorId"] = new BsonDouble(610);
            if (fault == "route") invalid["input"]["route"] = "unrecognized";
            if (fault == "quantity") invalid["input"]["lines"][0]["packages"] = 0;
            if (fault == "definition") invalid["input"]["lines"][0]["definitionId"] = "1";
            if (fault == "unknown") invalid["supplierCost"] = new BsonDecimal128(0m);
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Requests).ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid))).WriteError.Code);
            Assert.Equal(original, await data.Rows(D.Requests).Find(new BsonDocument()).SingleAsync());
        }
        var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync(); var duplicate = decision.DeepClone().AsBsonDocument; duplicate["_id"] = 999999L;
        Assert.Equal(11000, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Decisions).InsertOneAsync(duplicate))).WriteError.Code);
        Assert.Equal(decision, await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync()); await data.NoBusinessEffectsAsync();
    }

    internal static async Task RestoreV60Async(OrganizationAddressTestData data)
    {
        var history = await HistoryAsync(data.Database); Assert.Equal(CollectionMigrations.CurrentVersion, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 61 }, history.Skip(60).Select(row => row["_id"].AsInt32));
        foreach (var name in ProcurementRequestSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var prefix = new BsonRegularExpression(@"^procurement\.request(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 61 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(60)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers", "catalogIdentifierHistory", "idempotencyRecords", "auditEvents", "outboxEvents" })
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
