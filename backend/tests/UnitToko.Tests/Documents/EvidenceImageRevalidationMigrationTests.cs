using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
public sealed class EvidenceImageRevalidationMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_interrupted_owner_resumes_without_rewriting_predecessor_definitions()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture);
        await RestoreV80Async(data.Database);
        var beforePhoto = await DefinitionAsync(data.Database, "catalogPrimaryPhotos");
        var key = EvidenceImageRevalidationSchemas.Keys(EvidenceImageRevalidationSchemas.Requests)[0];
        await data.Database.GetCollection<BsonDocument>(EvidenceImageRevalidationSchemas.Requests).Indexes.DropOneAsync(key.Name);
        await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.ApplyAsync(data.Database, data.Clock);
        var afterPhoto = await DefinitionAsync(data.Database, "catalogPrimaryPhotos");
        Assert.Equal(beforePhoto.ToBson(), afterPhoto.ToBson());
        var indexes = await (await data.Database.GetCollection<BsonDocument>(EvidenceImageRevalidationSchemas.Requests)
            .Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, row => row.GetValue("name", "") == key.Name && row.GetValue("key", BsonNull.Value) == key.Key);
        await EvidenceImageRevalidationStorage.RequireAsync(data.Database, default);
        Assert.True(await data.Database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument {
            { "_id", 81 }, { "state", "applied" } }).AnyAsync());
    }

    [Fact]
    public async Task Positive_or_malformed_allocators_and_unsafe_predecessor_refuse_before_ddl()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture);
        await RestoreV80Async(data.Database);
        var counters = data.Database.GetCollection<BsonDocument>("idCounters");
        await counters.InsertOneAsync(new BsonDocument { { "_id", EvidenceImageRevalidationSchemas.Requests }, { "value", 1L } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await counters.DeleteOneAsync(new BsonDocument("_id", EvidenceImageRevalidationSchemas.Requests)); }
        var photo = await DefinitionAsync(data.Database, "catalogPrimaryPhotos");
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "catalogPrimaryPhotos" },
            { "validator", new BsonDocument("skuId", new BsonDocument("$gt", 0L)) } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "catalogPrimaryPhotos" },
            { "validator", photo["options"]["validator"] } }); }
    }

    [Fact]
    public async Task Unowned_rows_cannot_be_relabelled_as_an_empty_candidate()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture);
        await RestoreV80Async(data.Database);
        // A new collection can be present during an interrupted DDL sequence, but
        // there is no sanctioned pre-81 writer for a retained business row.
        await data.Database.GetCollection<BsonDocument>(EvidenceImageRevalidationSchemas.Heads)
            .InsertOneAsync(new BsonDocument { { "_id", 999L }, { "schemaVersion", 1 },
                { "revision", 1L }, { "requestId", 998L }, { "requestHash", new string('a', 64) },
                { "resultId", BsonNull.Value }, { "resultHash", BsonNull.Value },
                { "status", "pending" }, { "fence", 0L } });
        await RefusesAsync(data.Database, data.Clock);
    }

    // Test-only predecessor rehearsal, with no M81 business row, queue effect, or allocator.
    internal static async Task RestoreV80Async(IMongoDatabase db)
    {
        await UnitToko.Tests.Identity.AccountContactMigrationTests.RestoreV81Async(db);
        var history = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument())
            .Sort(new BsonDocument("_id", 1)).ToListAsync();
        if (history.Count <= 80) return;
        Assert.Equal(81, history.Count);
        Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await EvidenceImageRevalidationStorage.RequireAsync(db, default);
        foreach (var table in EvidenceImageRevalidationSchemas.Collections)
        {
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters")
                .Find(new BsonDocument("_id", table)).AnyAsync());
        }
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(
            new BsonDocument { { "_id", 81 }, { "state", "applied" } })).DeletedCount);
    }

    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.ApplyAsync(db, clock));
        Assert.Equal("Image revalidation migration found incompatible retained storage", error.Message);
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
    }

    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string table) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", table) })).SingleAsync();
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync())
            .OrderBy(row => row["name"].AsString, StringComparer.Ordinal))
        {
            var name = definition["name"].AsString;
            if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync())
                    .OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument())
                    .Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
