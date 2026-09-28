using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionRecoveryTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData("stage")]
    [InlineData("freeze")]
    [InlineData("publication")]
    [InlineData("complete")]
    public async Task Real_unknown_commit_never_authorizes_next_io_without_positive_reconciliation(string phase)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync();
        var app = "evidence-unknown-" + Guid.NewGuid().ToString("N");
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = app;
        var armed = 0; var commits = 0;
        var admin = fixture.Client.GetDatabase("admin");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "commitTransaction") Interlocked.Increment(ref commits);
            if (command.CommandName != "update" || command.Command.GetValue("update", BsonNull.Value) != "evidenceUploadAttempts") return;
            var update = command.Command["updates"][0]["u"].AsBsonDocument;
            var state = update.GetValue("state", BsonNull.Value);
            var match = phase switch {
                "stage" => state == "writing",
                "freeze" => state == "publish_pending" && update.GetValue("claimKind", BsonNull.Value).IsBsonNull,
                "publication" => state == "publish_pending" && update.GetValue("claimKind", BsonNull.Value) == "publication",
                "complete" => state == "quarantined",
                _ => false };
            if (match && Interlocked.CompareExchange(ref armed, 1, 0) == 0)
                admin.RunCommand<BsonDocument>(Unknown(app));
        });
        var client = new MongoClient(settings); var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        var service = data.NewService(db: db, client: client);
        using var source = new EvidenceAdmissionTests.CountingSource([1, 2, 3, 4]);
        try
        {
            var outcome = await service.UploadAsync(data.Actor, reservation.ReservationId, reservation.Revision, source, EvidenceAdmissionTestData.Meta());
            Assert.Equal("unknown", outcome.State); Assert.Equal(1, armed); Assert.True(commits >= 8);
            if (phase == "stage") Assert.Equal(0, source.Reads); else Assert.Equal(2, source.Reads);
            if (phase != "complete") Assert.Empty(Directory.GetFiles(data.Root, "*.original"));
            else Assert.Single(Directory.GetFiles(data.Root, "*.original"));
        }
        finally { await OffAsync(admin); }
        var durable = await data.RowAsync("evidenceUploadAttempts", reservation.AttemptId);
        if (phase == "stage")
        {
            Assert.Equal("writing", durable["state"]);
            Assert.Equal("unresolved", (await data.Service.RecoverAsync(data.Actor, reservation.ReservationId, EvidenceAdmissionTestData.Meta())).State);
        }
        else
        {
            if (phase == "publication") data.Catalog.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(durable["claimUntil"].AsBsonDateTime.MillisecondsSinceEpoch).AddMilliseconds(1);
            var recovered = await data.Service.RecoverAsync(data.Actor, reservation.ReservationId, EvidenceAdmissionTestData.Meta());
            Assert.Equal("quarantined", recovered.State);
            Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "evidence.validation.requested")));
            Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "evidence.catalog-image.complete")));
        }
        Assert.Equal(1, await data.Rows("evidenceLineages").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3072, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedBytes"].AsInt64);
    }

    [WindowsStorageFact]
    public async Task Unknown_reserve_commit_replays_original_identity_without_duplicate_charge()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "evidence-reserve-" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(settings); var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        var service = data.NewService(db: db, client: client);
        var admin = fixture.Client.GetDatabase("admin");
        try
        {
            await admin.RunCommandAsync<BsonDocument>(Unknown(settings.ApplicationName));
            await Assert.ThrowsAsync<UncertainCommitException>(() => new MongoUnitOfWork(client).ExecuteAsync((tx, ct) =>
                service.ReserveAsync(tx, data.Actor, new(data.Target), EvidenceAdmissionTestData.Meta(), ct)));
        }
        finally { await OffAsync(admin); }
        var original = await data.Rows("evidenceUploadReservations").Find(new BsonDocument()).SingleAsync();
        var replay = await data.ReserveAsync();
        Assert.Equal(original["_id"].AsInt64, replay.ReservationId);
        Assert.Equal(1, await data.Rows("evidenceLineages").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3072, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedBytes"].AsInt64);
        Assert.Empty(Directory.GetFiles(data.Root, "*.part"));
    }

    [WindowsStorageFact]
    public async Task Interrupted_v13_ddl_reruns_without_duplicate_identity_or_losing_stronger_rules()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        await data.Rows("evidenceUploadReservations").Indexes.DropOneAsync("ix_evidence_target_created");
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "evidenceIds"), Builders<BsonDocument>.Update.Set("value", 0L));
        await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 13));
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "evidence-ddl-" + Guid.NewGuid().ToString("N");
        var db = new MongoClient(settings).GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        var admin = fixture.Client.GetDatabase("admin");
        try
        {
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", new BsonDocument("times", 1) },
                { "data", new BsonDocument { { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "createIndexes" } }, { "errorCode", 11600 } } } });
            await Assert.ThrowsAsync<MongoNodeIsRecoveringException>(() => new CollectionMigrations(db, data.Catalog.Clock).ApplyAsync());
        }
        finally { await OffAsync(admin); }
        data.Catalog.Clock.UtcNow += TimeSpan.FromMinutes(10);
        await new CollectionMigrations(data.DB, data.Catalog.Clock).ApplyAsync();
        Assert.Equal(r, await data.ReserveAsync());
        Assert.True((await new IdAllocator(data.DB).NextAsync("evidenceIds")).Value > r.AttemptId);
    }

    private static BsonDocument Unknown(string app) => new() { { "configureFailPoint", "failCommand" }, { "mode", "alwaysOn" },
        { "data", new BsonDocument { { "appName", app }, { "failCommands", new BsonArray { "commitTransaction" } },
            { "writeConcernError", new BsonDocument { { "code", 64 }, { "errmsg", "synthetic uncertain evidence reply" } } },
            { "errorLabels", new BsonArray { "UnknownTransactionCommitResult" } } } } };
    private static Task OffAsync(IMongoDatabase admin) => admin.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "configureFailPoint", "failCommand" }, { "mode", "off" } });
}
