using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.SupplierTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SupplierMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_upgrade_preserves_real_catalog_and_profiles_without_inventing_supplier_or_verification_policy()
    {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture); await RestoreV61Async(data.Organizations);
        var guard = new BsonDocument("supplierId", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", D.Proposals }, { "validator", new BsonDocument("$and", new BsonArray { SupplierSchemas.Validator(D.Proposals), guard }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(D.Proposals).Indexes.DropOneAsync("ux_" + D.Proposals + "_0");
        await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { Name = "operator_supplier_resume_lookup" }));
        var history = await HistoryAsync(data.Database); var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(61))); Assert.Equal(preserved, await PreservedAsync(data.Database));
        var after = await DefinitionsAsync(data.Database);
        Assert.Equal(before.Where(row => !SupplierSchemas.Collections.Contains(row["name"].AsString)), after.Where(row => !SupplierSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in SupplierSchemas.Collections) {
            var previous = Assert.Single(before, row => row["name"] == name); var current = Assert.Single(after, row => row["name"] == name);
            Assert.Equal(previous["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, SupplierSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in SupplierSchemas.Keys(name)) {
                var index = Assert.Single(indexes, row => row["key"] == key);
                if (index["name"] == "_id_") Assert.Equal(new BsonDocument("_id", 1), index["key"]);
                else Assert.True(index["unique"].AsBoolean);
            }
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == D.Proposals)["options"]["validator"].AsBsonDocument, guard));
        var applied = await HistoryAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data.Database)); Assert.Equal(after, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
    }

    [Theory]
    [InlineData("row")] [InlineData("ttl")] [InlineData("counter")] [InlineData("orphan")]
    public async Task Unsafe_partial_supplier_install_refuses_before_DDL_or_source_changes(string fault)
    {
        await using var data = await ProcurementRequestTestData.CreateAsync(fixture); await RestoreV61Async(data.Organizations);
        if (fault == "row") await data.Rows(D.Proposals).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(D.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", D.Proposals }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", D.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await DefinitionsAsync(data.Database); var preserved = await PreservedAsync(data.Database);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal("Supplier migration preflight failed", error.Message);
        Assert.Equal(before, await DefinitionsAsync(data.Database)); Assert.Equal(preserved, await PreservedAsync(data.Database));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 62 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Supplier_schema_rejects_incomplete_policy_and_malformed_real_proposal_without_replacing_history()
    {
        await using var data = await D.CreateAsync(fixture); var policy = await data.Rows(D.Policies).Find(new BsonDocument("_id", D.PolicyId)).SingleAsync();
        foreach (var field in new[] { "trustedChannelReference", "operatingOwnerReference", "approvalReference", "legalName" }) {
            var invalid = policy.DeepClone().AsBsonDocument; invalid.Remove(field);
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), invalid))).WriteError.Code);
            Assert.Equal(policy, await data.Rows(D.Policies).Find(new BsonDocument("_id", D.PolicyId)).SingleAsync());
        }
        await data.ProposeAsync("supplier-schema"); var original = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "actor", "definition", "interval", "unknown" }) {
            var invalid = original.DeepClone().AsBsonDocument;
            if (fault == "actor") invalid["actorId"] = new BsonDouble(620);
            if (fault == "definition") invalid["input"]["mappings"][0]["definitionId"] = "1";
            if (fault == "interval") invalid["input"]["mappings"][0]["validUntil"] = invalid["input"]["mappings"][0]["validFrom"];
            if (fault == "unknown") invalid["bankAccount"] = "not-authorized";
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows(D.Proposals).ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid))).WriteError.Code);
            Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        }
        await data.NoBusinessEffectsAsync();
    }

    internal static async Task RestoreV61Async(OrganizationAddressTestData data)
    {
        var history = await HistoryAsync(data.Database); Assert.Equal(CollectionMigrations.CurrentVersion, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"])); Assert.Equal(new[] { 62 }, history.Skip(61).Select(row => row["_id"].AsInt32));
        foreach (var name in SupplierSchemas.Collections) {
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var prefix = new BsonRegularExpression(@"^procurement\.supplier(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, prefix)).AnyAsync());
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 62 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(61)), await HistoryAsync(data.Database));
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase database) => (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> PreservedAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "catalogCategories", "productFamilies", "skus", "variantDefinitions", "idempotencyRecords", "auditEvents", "outboxEvents", "procurementRequests", "procurementRequestDecisions" })
            state[table] = new BsonArray(await database.GetCollection<BsonDocument>(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
