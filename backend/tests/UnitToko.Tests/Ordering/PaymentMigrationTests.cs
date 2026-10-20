using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentMigrationTests(MongoFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private async Task Run(bool beforeEvidence, Func<IMongoDatabase, Task> action) {
        var name = "ut_payment_upgrade_" + Guid.NewGuid().ToString("N"); var db = fixture.Client.GetDatabase(name);
        try { if (beforeEvidence) await LegacyOrderSubmissionTestSchema.ApplyV30Async(db, Clock); else await LegacyOrderSubmissionTestSchema.ApplyV29Async(db, Clock); await action(db); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private static async Task<BsonArray> History(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument[]> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
    [WindowsStorageFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Evidence_upgrade_keeps_an_actual_completed_catalog_upload_and_its_business_records() {
        await using var original = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await original.ReserveAsync(); using var bytes = new MemoryStream("quarantined original fixture"u8.ToArray());
        Assert.Equal("quarantined", (await original.Service.UploadAsync(original.Actor, reservation.ReservationId, reservation.Revision, bytes, EvidenceAdmissionTestData.Meta())).State);
        await Run(true, async db => {
            var before = await History(db); var names = await (await original.DB.ListCollectionNamesAsync()).ToListAsync();
            // Copy the real v13-compatible graph into a database with its actual v30 predecessor
            // history. No migration record is fabricated, deleted or rewritten by this fixture.
            var snapshots = new Dictionary<string, BsonArray>();
            foreach (var name in names.Where(name => name != "schemaMigrations")) {
                var rows = await original.DB.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
                if (rows.Count > 0) await db.GetCollection<BsonDocument>(name).InsertManyAsync(rows);
                snapshots[name] = new(rows);
            }
            await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(30)));
            foreach (var pair in snapshots) Assert.Equal(pair.Value, new BsonArray(await db.GetCollection<BsonDocument>(pair.Key).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        });
    }
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public Task Draft_upgrade_retains_history_stronger_validator_and_lossless_counter() => Run(false, async db => {
        var before = await History(db); Assert.Equal(29, before.Count);
        var required = PaymentDraftSchemas.Validator(PaymentDraftBson.Versions);
        var stronger = new BsonDocument("$and", new BsonArray { required, new BsonDocument("revision", new BsonDocument("$lt", 100000L)) });
        await db.CreateCollectionAsync(PaymentDraftBson.Versions, new CreateCollectionOptions<BsonDocument> { Validator = stronger });
        await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", PaymentDraftBson.Heads }, { "value", 9007199254740993L } });
        await new CollectionMigrations(db, Clock).ApplyAsync(); var after = await History(db);
        Assert.Equal(before, new BsonArray(after.Take(29))); Assert.Equal(CollectionMigrations.CurrentVersion, after.Count);
        Assert.Equal(stronger, (await Definitions(db)).Single(row => row["name"] == PaymentDraftBson.Versions)["options"]["validator"]);
        Assert.Equal(9007199254740994L, (await new IdAllocator(db).NextAsync(PaymentDraftBson.Heads)).Value);
        await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(after, await History(db));
    });
    [Theory]
    [InlineData("legacy")] [InlineData("orphan")] [InlineData("ttl")] [InlineData("counter")]
    public Task Draft_preflight_rejects_unaccepted_work_without_target_mutation(string fault) => Run(false, async db => {
        if (fault == "legacy") await db.GetCollection<BsonDocument>("payments").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } });
        if (fault == "orphan") await db.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument("sourceKey", PaymentDraftBson.Operation + ":1"), new InsertOneOptions { BypassDocumentValidation = true });
        if (fault == "ttl") await db.GetCollection<BsonDocument>(PaymentDraftBson.Heads).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "counter") await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", PaymentDraftBson.Versions }, { "value", long.MaxValue } });
        var definitions = await Definitions(db); var history = await History(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(definitions, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(29)));
    });
    [Fact(Skip = Retired.HistoricalUpgrade)]
    public Task Evidence_forward_extension_preserves_stronger_rules_history_and_indexes() => Run(true, async db => {
        const string name = "evidenceLineages"; var before = await History(db); Assert.Equal(30, before.Count);
        var extra = new BsonDocument("target.id", new BsonDocument("$lte", 9007199254740993L));
        var stronger = new BsonDocument("$and", new BsonArray { EvidenceSchemas.Validator(name), extra });
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", stronger } });
        var indexes = await (await db.GetCollection<BsonDocument>("auditEvents").Indexes.ListAsync()).ToListAsync();
        await new CollectionMigrations(db, Clock).ApplyAsync();
        Assert.Equal(before, new BsonArray((await History(db)).Take(30)));
        Assert.Equal(new BsonDocument("$and", new BsonArray { PaymentEvidenceSchemas.Validator(name), extra }), (await Definitions(db)).Single(row => row["name"] == name)["options"]["validator"]);
        Assert.Equal(indexes, await (await db.GetCollection<BsonDocument>("auditEvents").Indexes.ListAsync()).ToListAsync());
        var head = new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 }, { "createdAt", DateTime.UnixEpoch },
            { "target", new BsonDocument { { "kind", "PaymentDeclaration" }, { "id", 2L } } }, { "purpose", "PaymentImage" },
            { "owner", new BsonDocument { { "kind", "fulfillmentShop" }, { "organizationId", 1L }, { "shopId", 2L }, { "channelId", 3L }, { "warehouseId", BsonNull.Value } } },
            { "uploader", new BsonDocument { { "userId", 4L }, { "issuer", "https://identity.invalid/" }, { "subject", "payment-test" } } }, { "firstVersionRowId", 5L } };
        await db.GetCollection<BsonDocument>(name).InsertOneAsync(head);
        foreach (var mutate in new Action<BsonDocument>[] { row => row["purpose"] = "CatalogImage", row => row["target"]["kind"] = "CatalogSku", row => row["target"]["id"] = 9007199254740994L }) {
            var invalid = head.DeepClone().AsBsonDocument; invalid["_id"] = 6L; mutate(invalid);
            await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(invalid));
        }
    });
    [Theory]
    [InlineData("validator")] [InlineData("ttl")] [InlineData("orphan")]
    public Task Evidence_preflight_fails_before_any_collection_changes(string fault) => Run(true, async db => {
        if (fault == "validator") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "evidenceLineages" }, { "validator", new BsonDocument("schemaVersion", 1) } });
        if (fault == "ttl") await db.GetCollection<BsonDocument>("evidenceUploadAttempts").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("createdAt", 1), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(1) }));
        if (fault == "orphan") await db.GetCollection<BsonDocument>("evidenceUploadReservations").InsertOneAsync(new BsonDocument { { "_id", 77L }, { "target", new BsonDocument("kind", "PaymentDeclaration") } }, new InsertOneOptions { BypassDocumentValidation = true });
        var before = await Definitions(db); var history = await History(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, Clock).ApplyAsync());
        Assert.Equal(before, await Definitions(db)); Assert.Equal(history, new BsonArray((await History(db)).Take(30)));
    });
    [Fact]
    public Task Evidence_partial_ddl_resumes_without_rewriting_history() => Run(true, async db => {
        var before = await History(db); using var cancellation = new CancellationTokenSource(); var changed = false;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(value => { if (value.CommandName == "collMod") { changed = true; cancellation.Cancel(); } });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(client.GetDatabase(db.DatabaseNamespace.DatabaseName), Clock).ApplyAsync(cancellation.Token));
        Assert.True(changed); await new CollectionMigrations(db, Clock).ApplyAsync(); Assert.Equal(before, new BsonArray((await History(db)).Take(30)));
    });
}
