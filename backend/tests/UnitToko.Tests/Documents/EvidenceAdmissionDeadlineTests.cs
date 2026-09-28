using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionDeadlineTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Raw_writer_deadline_cannot_restart_after_slow_authorized_pre_read()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        var start = data.Catalog.Clock.UtcNow;
        var selected = 0; var advanced = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.GetValue("find", BsonNull.Value) == "identitySessions" && command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (selected == command.RequestId && Interlocked.CompareExchange(ref advanced, 1, 0) == 0) data.Catalog.Clock.UtcNow = start.AddSeconds(121);
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        using var source = new EvidenceAdmissionTests.CountingSource([1, 2]);
        await Assert.ThrowsAnyAsync<Exception>(() => data.NewService(db: db, client: client).UploadAsync(
            data.Actor, r.ReservationId, r.Revision, source, EvidenceAdmissionTestData.Meta()));
        Assert.Equal(0, source.Reads);
        Assert.Equal("fresh", (await data.RowAsync("evidenceUploadAttempts", r.AttemptId))["state"]);
    }

    [WindowsStorageFact]
    public async Task Transient_stage_claim_retry_preserves_original_fixed_writer_deadline_and_reads_body_once()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        var start = data.Catalog.Clock.UtcNow;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "evidence-stage-retry-" + Guid.NewGuid().ToString("N");
        var admin = fixture.Client.GetDatabase("admin");
        var attempts = 0;
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.CommandName != "update" || command.Command.GetValue("update", BsonNull.Value) != "evidenceUploadAttempts") return;
            var row = command.Command["updates"][0]["u"];
            if (row.AsBsonDocument.GetValue("state", BsonNull.Value) != "writing") return;
            if (Interlocked.Increment(ref attempts) != 1) return;
            admin.RunCommand<BsonDocument>(new BsonDocument {
                { "configureFailPoint", "failCommand" }, { "mode", new BsonDocument("times", 1) },
                { "data", new BsonDocument { { "appName", settings.ApplicationName }, { "failCommands", new BsonArray { "update" } },
                    { "errorCode", 112 }, { "errorLabels", new BsonArray { "TransientTransactionError" } } } } });
            data.Catalog.Clock.UtcNow = start.AddSeconds(60);
        });
        var client = new MongoClient(settings); var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        using var source = new EvidenceAdmissionBoundaryTests.PausedSource();
        var upload = data.NewService(db: db, client: client).UploadAsync(data.Actor, r.ReservationId, r.Revision, source, EvidenceAdmissionTestData.Meta());
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var writing = await data.RowAsync("evidenceUploadAttempts", r.AttemptId);
            Assert.Equal(new BsonDateTime(start.AddSeconds(120).UtcDateTime), writing["writerUntil"]);
            Assert.True(attempts >= 2);
        }
        finally
        {
            source.Continue.TrySetResult();
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "configureFailPoint", "failCommand" }, { "mode", "off" } });
            await upload;
        }
        Assert.Equal(2, source.Reads);
        Assert.Single(Directory.GetFiles(data.Root, "*.original"));
    }
    [WindowsStorageTheory]
    [InlineData("fractional")]
    [InlineData("reservation")]
    public async Task Publication_claim_rejects_noncanonical_or_outside_reservation_deadline_and_aborts_caller(string invalid)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await EvidenceAdmissionBoundaryTests.FreezeAsync(data);
        var deadline = data.Catalog.Clock.UtcNow.AddSeconds(60).AddTicks(1);
        if (invalid == "reservation")
        {
            data.Catalog.Clock.UtcNow = frozen.Reservation.ExpiresAt.AddSeconds(-5);
            deadline = frozen.Reservation.ExpiresAt.AddMilliseconds(1);
        }
        var reservation = await data.RowAsync("evidenceUploadReservations", frozen.Reservation.ReservationId);
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Rows("orders").InsertOneAsync(tx, new BsonDocument { { "_id", 932L }, { "schemaVersion", 1 } });
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.ClaimPublicationAsync(tx, data.Actor,
            frozen.Reservation.ReservationId, reservation["revision"].AsInt64, new string('e', 64), deadline, EvidenceAdmissionTestData.Meta()));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument("_id", 932L)));
        Assert.True((await data.RowAsync("evidenceUploadAttempts", frozen.Reservation.AttemptId))["claimKind"].IsBsonNull);
    }
}
