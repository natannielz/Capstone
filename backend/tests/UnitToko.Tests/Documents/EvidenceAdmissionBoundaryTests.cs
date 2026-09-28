using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionBoundaryTests(MongoFixture fixture)
{
    internal static readonly StorageIoBudget Budget = new(1024, TimeSpan.FromSeconds(10), 1024);
    internal static async Task<(ReservationResult Reservation, StageClaim Stage, ExpectedPublication Expected)> FreezeAsync(EvidenceAdmissionTestData data)
    {
        var reservation = await data.ReserveAsync();
        var claim = await data.StageClaimAsync(reservation);
        var sealedResult = Assert.IsType<StageOutcome.Sealed>(await data.Registration.Store!.StageAsync(claim.Attempt, new MemoryStream([1, 2, 3, 4]), Budget, default));
        var expected = await data.Unit.ExecuteAsync((tx, ct) => data.Service.FreezeContentAsync(tx, data.Actor, claim, sealedResult.Stage, EvidenceAdmissionTestData.Meta(), ct));
        return (reservation, claim, expected);
    }
    internal static async Task<PublicationClaim> ClaimAsync(EvidenceAdmissionTestData data, long reservationId, string request, DateTimeOffset until)
    {
        var row = await data.RowAsync("evidenceUploadReservations", reservationId);
        return await data.Unit.ExecuteAsync((tx, ct) => data.Service.ClaimPublicationAsync(tx, data.Actor, reservationId, row["revision"].AsInt64,
            request, until, EvidenceAdmissionTestData.Meta(), ct));
    }

    [WindowsStorageTheory]
    [InlineData("audit")]
    [InlineData("receipt")]
    public async Task Mutations_reject_missing_original_reserve_provenance(string missing)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        if (missing == "audit")
        {
            var row = await data.RowAsync("evidenceUploadReservations", r.ReservationId);
            await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["reserveAuditId"]));
        }
        else await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "evidence.catalog-image.reserve"));
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Rows("orders").InsertOneAsync(tx, new() { { "_id", 88L }, { "schemaVersion", 1 } });
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.ClaimStageAsync(tx, data.Actor, r.ReservationId, r.Revision, new string('b', 64), data.Catalog.Clock.UtcNow.AddSeconds(120), EvidenceAdmissionTestData.Meta()));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument("_id", 88L)));
    }

    [WindowsStorageFact]
    public async Task Frozen_tuple_survives_raw_window_expiry_and_only_bodyless_recovery_gets_new_window()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await FreezeAsync(data);
        var before = await data.RowAsync("evidenceUploadAttempts", frozen.Reservation.AttemptId);
        data.Catalog.Clock.UtcNow = frozen.Stage.Until.AddMilliseconds(1);
        var outcome = await data.Service.RecoverAsync(data.Actor, frozen.Reservation.ReservationId, EvidenceAdmissionTestData.Meta());
        Assert.Equal("quarantined", outcome.State);
        var after = await data.RowAsync("evidenceUploadAttempts", frozen.Reservation.AttemptId);
        Assert.Equal(before["writerUntil"], after["writerUntil"]); Assert.Equal(before["writerRequestId"], after["writerRequestId"]);
        Assert.Equal(before["generation"], after["generation"]); Assert.Equal(before["sha256"], after["sha256"]);
    }

    [WindowsStorageFact]
    public async Task Expired_publication_claim_can_be_taken_over_but_old_claim_cannot_complete()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await FreezeAsync(data);
        var old = await ClaimAsync(data, frozen.Reservation.ReservationId, frozen.Stage.RequestId, frozen.Stage.Until);
        Assert.Equal(PublishState.Published, (await data.Registration.Store!.PublishAsync(frozen.Expected, Budget, default)).State);
        var opened = Assert.IsType<OpenOutcome.Opened>(await data.Registration.Store.OpenVerifiedAsync(frozen.Expected, Budget, default));
        await using (var handle = opened.Handle)
        {
            data.Catalog.Clock.UtcNow = old.Until.AddMilliseconds(1);
            var current = await ClaimAsync(data, frozen.Reservation.ReservationId, new string('e', 64), data.Catalog.Clock.UtcNow.AddSeconds(30));
            Assert.True(current.Epoch > old.Epoch);
            await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.CompleteUploadAsync(tx, data.Actor, old, handle, EvidenceAdmissionTestData.Meta(), ct)));
            Assert.Equal("reserved", (await data.RowAsync("documentVersions", frozen.Reservation.VersionRowId))["state"]);
        }
    }

    [WindowsStorageTheory]
    [InlineData("partial")]
    [InlineData("corrupt")]
    [InlineData("missing-stage")]
    [InlineData("busy")]
    public async Task Recovery_never_replaces_a_partial_corrupt_missing_or_busy_candidate(string state)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await FreezeAsync(data);
        var original = Path.Combine(data.Root, frozen.Expected.Attempt.Generation + ".original");
        var ready = Path.Combine(data.Root, frozen.Expected.Attempt.StageToken + ".ready");
        FileStream? held = null;
        if (state == "missing-stage") File.Delete(ready);
        else if (state == "busy") { await data.Registration.Store!.PublishAsync(frozen.Expected, Budget, default); held = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.None); }
        else File.WriteAllBytes(original, state == "partial" ? [1] : [9, 9, 9, 9]);
        try
        {
            var outcome = await data.Service.RecoverAsync(data.Actor, frozen.Reservation.ReservationId, EvidenceAdmissionTestData.Meta());
            Assert.Equal("publish_pending", outcome.State);
            Assert.Equal("reserved", (await data.RowAsync("documentVersions", frozen.Reservation.VersionRowId))["state"]);
            if (state == "partial") Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(original));
            if (state == "corrupt") Assert.Equal(new byte[] { 9, 9, 9, 9 }, File.ReadAllBytes(original));
        }
        finally { held?.Dispose(); }
    }

    [WindowsStorageTheory]
    [InlineData("store")]
    [InlineData("ticket")]
    [InlineData("generation")]
    [InlineData("oversize")]
    public async Task Freeze_rejects_altered_sealed_stage_binding_and_never_publishes(string changed)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync(); var claim = await data.StageClaimAsync(reservation);
        var sealedResult = Assert.IsType<StageOutcome.Sealed>(await data.Registration.Store!.StageAsync(claim.Attempt, new MemoryStream([1, 2]), Budget, default));
        var original = sealedResult.Stage;
        var ticket = new StorageAttempt(changed == "store" ? new string('f', 64) : claim.Attempt.StoreId,
            claim.Attempt.ReservationId, claim.Attempt.AttemptId, changed == "ticket" ? 2 : 1,
            claim.Attempt.StageToken, changed == "generation" ? new string('f', 64) : claim.Attempt.Generation);
        var altered = new SealedStage(ticket, changed == "oversize" ? new StorageContent(1025, original.Content.Sha256) : original.Content);
        await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.FreezeContentAsync(tx, data.Actor, claim, altered, EvidenceAdmissionTestData.Meta(), ct)));
        Assert.Equal("writing", (await data.RowAsync("evidenceUploadAttempts", reservation.AttemptId))["state"]);
        Assert.Empty(Directory.GetFiles(data.Root, "*.original"));
    }

    [WindowsStorageFact]
    public async Task Disposed_handle_never_completes_even_when_exact_tuple_matches()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await FreezeAsync(data);
        var claim = await ClaimAsync(data, frozen.Reservation.ReservationId, frozen.Stage.RequestId, frozen.Stage.Until);
        await data.Registration.Store!.PublishAsync(frozen.Expected, Budget, default);
        var opened = Assert.IsType<OpenOutcome.Opened>(await data.Registration.Store.OpenVerifiedAsync(frozen.Expected, Budget, default));
        await opened.Handle.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.CompleteUploadAsync(tx, data.Actor, claim, opened.Handle, EvidenceAdmissionTestData.Meta(), ct)));
        Assert.Equal("reserved", (await data.RowAsync("documentVersions", frozen.Reservation.VersionRowId))["state"]);
    }

    [WindowsStorageTheory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("session")]
    [InlineData("target")]
    public async Task Authority_revocation_during_real_staging_prevents_freeze_and_quarantine(string revoked)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync();
        using var source = new PausedSource();
        var upload = data.Service.UploadAsync(data.Actor, reserved.ReservationId, reserved.Revision, source, EvidenceAdmissionTestData.Meta());
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var collection = revoked switch { "grant" => "authorizationGrants", "membership" => "memberships", "session" => "identitySessions", _ => "productFamilies" };
            BsonValue id = revoked == "session" ? new BsonString(data.Actor.Session!.Id) : new BsonInt64(revoked == "target" ? data.Target.Id : data.Actor.UserId);
            var update = revoked == "target" ? Builders<BsonDocument>.Update.Set("active", false) :
                revoked == "session" ? Builders<BsonDocument>.Update.Set("idleExpiresAt", data.Catalog.Clock.UtcNow.UtcDateTime) :
                Builders<BsonDocument>.Update.Set("status", "revoked");
            await data.Rows(collection).UpdateOneAsync(new BsonDocument("_id", id), update);
        }
        finally { source.Continue.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => upload);
        var attempt = await data.RowAsync("evidenceUploadAttempts", reserved.AttemptId);
        Assert.Equal("writing", attempt["state"]); Assert.True(attempt["sha256"].IsBsonNull);
        Assert.Empty(Directory.GetFiles(data.Root, "*.original"));
        Assert.Single(Directory.GetFiles(data.Root, "*.ready"));
    }

    [WindowsStorageFact]
    public async Task Claim_deadline_rechecked_after_final_authorization_await_aborts_earlier_effects()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync(); var stage = await data.StageClaimAsync(reserved);
        var sealedStage = Assert.IsType<StageOutcome.Sealed>(await data.Registration.Store!.StageAsync(stage.Attempt, new MemoryStream([1]), Budget, default)).Stage;
        var selected = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.GetValue("find", BsonNull.Value) == "identitySessions" && command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt")) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (selected == command.RequestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) data.Catalog.Clock.UtcNow = stage.Until;
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.DB.DatabaseNamespace.DatabaseName);
        var service = data.NewService(db: db, client: client);
        using var tx = await client.StartSessionAsync(); tx.StartTransaction();
        await db.GetCollection<BsonDocument>("orders").InsertOneAsync(tx, new() { { "_id", 91L }, { "schemaVersion", 1 } });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.FreezeContentAsync(tx, data.Actor, stage, sealedStage, EvidenceAdmissionTestData.Meta()));
        Assert.Equal(1, observed); Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument("_id", 91L)));
        Assert.Equal("writing", (await data.RowAsync("evidenceUploadAttempts", reserved.AttemptId))["state"]);
    }

    [WindowsStorageFact]
    public async Task Policy_change_denies_mutation_but_keeps_receipt_and_status_readable()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync();
        await data.Rows("evidenceStorePolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false).Inc("policyVersion", 1L));
        await Assert.ThrowsAnyAsync<Exception>(() => data.StageClaimAsync(reserved));
        Assert.Equal(reserved, await data.ReserveAsync());
        Assert.Equal("fresh", (await data.StatusAsync(reserved.ReservationId)).State);
    }

    [WindowsStorageFact]
    public async Task Caught_completion_audit_failure_cannot_commit_business_write_or_metadata_but_retains_file()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await FreezeAsync(data);
        var claim = await ClaimAsync(data, frozen.Reservation.ReservationId, frozen.Stage.RequestId, frozen.Stage.Until);
        await data.Registration.Store!.PublishAsync(frozen.Expected, Budget, default);
        var opened = Assert.IsType<OpenOutcome.Opened>(await data.Registration.Store.OpenVerifiedAsync(frozen.Expected, Budget, default));
        await using var handle = opened.Handle;
        await data.DB.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("kind", new BsonDocument("$ne", "evidence.quarantine")) } });
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Rows("orders").InsertOneAsync(tx, new() { { "_id", 99L }, { "schemaVersion", 1 } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.CompleteUploadAsync(tx, data.Actor, claim, handle, EvidenceAdmissionTestData.Meta()));
        Assert.False(tx.IsInTransaction);
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument("_id", 99L)));
        Assert.Equal("reserved", (await data.RowAsync("documentVersions", frozen.Reservation.VersionRowId))["state"]);
        Assert.Single(Directory.GetFiles(data.Root, "*.original"));
    }

    internal sealed class PausedSource : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        internal PausedSource() : base([1, 2, 3, 4]) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reads++; Entered.TrySetResult(); await Continue.Task.WaitAsync(ct); return await base.ReadAsync(buffer, ct);
        }
    }
}
