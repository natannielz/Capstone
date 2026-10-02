using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Tests.Organizations;

[Collection("Mongo")]
public sealed class OrganizationAddressMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Real_approved_profiles_upgrade_without_seeded_addresses_and_empty_resume_preserves_stronger_storage_and_history() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); await RestoreV57Async(data);
        var guard = new BsonDocument("target.id", new BsonDocument("$ne", 999999L));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Heads },
            { "validator", new BsonDocument("$and", new BsonArray { OrganizationAddressSchemas.Validator(B.Heads), guard }) },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        await data.Rows(B.Heads).Indexes.DropOneAsync("ux_" + B.Heads + "_0");
        await data.Rows(B.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("updatedAt", 1), new CreateIndexOptions { Name = "operator_address_resume_lookup" }));
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", B.Heads }, { "value", 17L } });
        var history = await HistoryAsync(data); var before = await DefinitionsAsync(data); var sources = await OrganizationStateAsync(data);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(history, new BsonArray((await HistoryAsync(data)).Take(57))); Assert.Equal(sources, await OrganizationStateAsync(data));
        Assert.Equal(17L, (await data.Rows("idCounters").Find(new BsonDocument("_id", B.Heads)).SingleAsync())["value"].AsInt64);
        var after = await DefinitionsAsync(data);
        Assert.Equal(before.Where(row => !OrganizationAddressSchemas.Collections.Contains(row["name"].AsString)),
            after.Where(row => !OrganizationAddressSchemas.Collections.Contains(row["name"].AsString)));
        foreach (var name in OrganizationAddressSchemas.Collections) {
            var previous = Assert.Single(before, row => row["name"] == name); var current = Assert.Single(after, row => row["name"] == name);
            Assert.Equal(previous["info"]["uuid"], current["info"]["uuid"]);
            Assert.True(InventorySchemas.Contains(current["options"]["validator"].AsBsonDocument, OrganizationAddressSchemas.Validator(name)));
            Assert.Equal("strict", current["options"]["validationLevel"]); Assert.Equal("error", current["options"]["validationAction"]);
            var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
            foreach (var key in OrganizationAddressSchemas.Keys(name)) Assert.True(Assert.Single(indexes, row => row["key"] == key)["unique"].AsBoolean);
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        Assert.True(InventorySchemas.Contains(Assert.Single(after, row => row["name"] == B.Heads)["options"]["validator"].AsBsonDocument, guard));
        Assert.Contains(await (await data.Rows(B.Heads).Indexes.ListAsync()).ToListAsync(), row => row["name"] == "operator_address_resume_lookup");
        var applied = await HistoryAsync(data); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(applied, await HistoryAsync(data)); Assert.Equal(after, await DefinitionsAsync(data)); Assert.Equal(sources, await OrganizationStateAsync(data));
    }

    [Fact]
    public async Task Lost_original_organization_approval_receipt_history_or_matching_profile_name_refuses_before_address_DDL() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); await RestoreV57Async(data);
        var profile = await data.Rows("organizationProfiles").Find(new BsonDocument("_id", 11L)).SingleAsync();
        var approval = await data.Rows("authorizationHistory").Find(new BsonDocument { { "operation", "organization.approved" }, { "after.profile._id", 11L } }).SingleAsync();
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", "organization.approve" }, { "result.scopeId", 11L } }).SingleAsync();
        foreach (var (table, row) in new[] { ("authorizationHistory", approval), ("idempotencyRecords", receipt), ("organizationProfiles", profile) }) {
            if (table == "organizationProfiles") await data.Rows(table).UpdateOneAsync(new BsonDocument("_id", row["_id"]), Builders<BsonDocument>.Update.Set("name", "Unreviewed changed branch name"));
            else Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try {
                var before = await DefinitionsAsync(data); var damaged = await OrganizationStateAsync(data);
                var failure = await Assert.ThrowsAsync<PersistenceConflictException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
                Assert.Equal("organization_address_integrity_unavailable", failure.Code);
                Assert.Equal(before, await DefinitionsAsync(data)); Assert.Equal(damaged, await OrganizationStateAsync(data));
                Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 58 }, { "state", "applied" } }).AnyAsync());
                foreach (var target in OrganizationAddressSchemas.Collections) Assert.False(await data.Rows(target).Find(new BsonDocument()).AnyAsync());
            } finally {
                if (table == "organizationProfiles") await data.Rows(table).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
                else await data.Rows(table).InsertOneAsync(row);
            }
        }
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.True(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 58 }, { "state", "applied" } }).AnyAsync());
    }

    [Theory]
    [InlineData("row")] [InlineData("ttl")] [InlineData("unique")] [InlineData("counter")] [InlineData("orphan")]
    public async Task Unsafe_empty_target_or_surviving_effect_cannot_initialize_postal_authority(string fault) {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); await RestoreV57Async(data);
        if (fault == "row") await data.Rows(B.Heads).InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await data.Rows(B.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("recordedAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "unique") await data.Rows(B.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("addressId", 1), new CreateIndexOptions { Unique = true }));
        if (fault == "counter") await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", B.Heads }, { "value", long.MaxValue } });
        if (fault == "orphan") await data.Rows("outboxEvents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", B.Prefix + "approve" } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await DefinitionsAsync(data); var sources = await OrganizationStateAsync(data);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.Database, data.Clock).ApplyAsync());
        Assert.Equal("Organization address migration preflight failed", failure.Message);
        Assert.Equal(before, await DefinitionsAsync(data)); Assert.Equal(sources, await OrganizationStateAsync(data));
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 58 }, { "state", "applied" } }).AnyAsync());
    }

    private static async Task RestoreV57Async(OrganizationAddressTestData data) {
        Assert.Equal(58, CollectionMigrations.CurrentVersion);
        foreach (var target in OrganizationAddressSchemas.Collections) Assert.False(await data.Rows(target).Find(new BsonDocument()).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.False(await data.Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^organization\.address(?:[.:]|$)"))).AnyAsync());
        var history = await HistoryAsync(data); Assert.Equal(58, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 58 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(57)), await HistoryAsync(data));
    }
    private static async Task<BsonArray> HistoryAsync(OrganizationAddressTestData data) => new(await data.Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<List<BsonDocument>> DefinitionsAsync(OrganizationAddressTestData data) => (await (await data.Database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> OrganizationStateAsync(OrganizationAddressTestData data) {
        var result = new BsonDocument();
        foreach (var table in new[] { "organizationProfiles", "organizationProposals", "authorizationHistory", "authorizationScopes", "idempotencyRecords", "auditEvents", "outboxEvents" })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
