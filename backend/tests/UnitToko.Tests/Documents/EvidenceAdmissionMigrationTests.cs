using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class EvidenceAdmissionMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task V13_clean_install_has_strict_shapes_indexes_and_no_configuration_seed()
    {
        var db = fixture.Client.GetDatabase(fixture.DatabaseName + "_ev_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
            var appliedV13 = await db.GetCollection<BsonDocument>("schemaMigrations")
                .Find(new BsonDocument("_id", 13)).SingleAsync();
            Assert.Equal("applied", appliedV13["state"].AsString);
            Assert.True(appliedV13["appliedAt"].IsBsonDateTime);
            foreach (var name in EvidenceSchemas.Collections.Take(5))
            {
                var info = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
                Assert.Equal("strict", info["options"]["validationLevel"]); Assert.Equal("error", info["options"]["validationAction"]);
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
            }
            var indexes = await (await db.GetCollection<BsonDocument>("documentVersions").Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, index => index["name"] == "uq_documentId_version");
            Assert.Contains(indexes, index => index["name"] == "ux_evidence_lineage_version" && index["unique"] == true);
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("documentVersions").InsertOneAsync(
                new() { { "_id", 1L }, { "schemaVersion", 1 }, { "documentId", 9L }, { "version", 1L } }));
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }

    [Fact]
    public async Task Unknown_legacy_version_fails_before_any_target_ddl()
    {
        var db = fixture.Client.GetDatabase(fixture.DatabaseName + "_ev_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await ApplyV12Async(db);
            await db.GetCollection<BsonDocument>("documentVersions").InsertOneAsync(new() {
                { "_id", 90L }, { "schemaVersion", 1 }, { "documentId", 10L }, { "version", 1L } });
            var before = await OptionsAsync(db, "documentVersions");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
            Assert.Equal(before, await OptionsAsync(db, "documentVersions"));
            Assert.False(await (await db.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", "evidenceUploadAttempts") })).AnyAsync());
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }

    [Fact]
    public async Task Existing_stronger_policy_validator_and_high_water_are_preserved()
    {
        var db = fixture.Client.GetDatabase(fixture.DatabaseName + "_ev_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await ApplyV12Async(db);
            var stronger = new BsonDocument("storeId", new string('a', 64));
            await db.CreateCollectionAsync("evidenceStorePolicies", new CreateCollectionOptions<BsonDocument> { Validator = stronger });
            var policy = EvidenceAdmissionSchemaTests.Policy(); policy["_id"] = 987L;
            await db.GetCollection<BsonDocument>("evidenceStorePolicies").InsertOneAsync(policy);
            await new CollectionMigrations(db, new SystemClock()).ApplyAsync();
            Assert.Contains(stronger.ToJson(), (await OptionsAsync(db, "evidenceStorePolicies"))["validator"].ToJson());
            var next = await new IdAllocator(db).NextAsync("evidenceStorePolicies"); Assert.Equal(988, next.Value);
            var forbidden = EvidenceAdmissionSchemaTests.Policy(); forbidden["_id"] = 989L; forbidden["storeId"] = new string('b', 64);
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("evidenceStorePolicies").InsertOneAsync(forbidden));
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }

    [Fact]
    public async Task Ttl_index_rejects_migration_without_changing_existing_validator()
    {
        var db = fixture.Client.GetDatabase(fixture.DatabaseName + "_ev_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await ApplyV12Async(db);
            await db.GetCollection<BsonDocument>("auditEvents").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { Name = "unsafe_retention", ExpireAfter = TimeSpan.FromDays(1) }));
            var before = await OptionsAsync(db, "auditEvents");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, new SystemClock()).ApplyAsync());
            Assert.Equal(before, await OptionsAsync(db, "auditEvents"));
        }
        finally { await fixture.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName); }
    }

    internal static async Task ApplyV12Async(IMongoDatabase db)
    {
        var clock = new SystemClock();
        var gate = new MigrationGate(db, clock, 12);
        IMigrationStep[] steps = [new FoundationMigration(db), new AuthorizationMigration(db), new AuthorizationLifecycleMigration(db),
            new IdentityMigration(db), new ProviderLifecycleMigration(db), new CatalogVisibilityMigration(db), new HistoricalReadMigration(db),
            new PersonAuthorityMigration(db), new PersonGovernanceMigration(db), new PersonReviewPreviewMigration(db), new CatalogMigration(db), new AccountingMigration(db)];
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, default);
            if (lease is null) continue;
            await step.ApplyAsync(lease, default); await lease.CompleteAsync(default);
        }
    }
    internal static async Task<BsonDocument> OptionsAsync(IMongoDatabase db, string name) =>
        (await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync())["options"].AsBsonDocument;
}
