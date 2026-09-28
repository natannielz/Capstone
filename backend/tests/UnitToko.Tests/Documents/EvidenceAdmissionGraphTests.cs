using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionGraphTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData("fresh")]
    [InlineData("writing")]
    [InlineData("publish_pending")]
    [InlineData("publication_claimed")]
    [InlineData("publication_reclaimed")]
    [InlineData("quarantined")]
    [InlineData("interrupted")]
    public async Task Valid_prepopulated_graph_reruns_without_changes_and_reconciles_shared_id_high_water(string state)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync();
        if (state == "writing") await data.StageClaimAsync(reservation);
        if (state is "publish_pending" or "publication_claimed" or "publication_reclaimed")
        {
            var claim = await data.StageClaimAsync(reservation);
            var seal = Assert.IsType<UnitToko.Platform.Storage.StageOutcome.Sealed>(await data.Registration.Store!.StageAsync(
                claim.Attempt, new MemoryStream([1, 2]), EvidenceAdmissionBoundaryTests.Budget, default));
            await data.Unit.ExecuteAsync((tx, ct) => data.Service.FreezeContentAsync(tx, data.Actor, claim, seal.Stage, EvidenceAdmissionTestData.Meta(), ct));
            if (state is "publication_claimed" or "publication_reclaimed")
            {
                var publication = await EvidenceAdmissionBoundaryTests.ClaimAsync(data, reservation.ReservationId, claim.RequestId, claim.Until);
                if (state == "publication_reclaimed")
                {
                    data.Catalog.Clock.UtcNow = publication.Until.AddMilliseconds(1);
                    await EvidenceAdmissionBoundaryTests.ClaimAsync(data, reservation.ReservationId, new string('e', 64), data.Catalog.Clock.UtcNow.AddSeconds(120));
                }
            }
        }
        if (state is "quarantined" or "interrupted")
            await data.Service.UploadAsync(data.Actor, reservation.ReservationId, reservation.Revision,
                new MemoryStream(state == "quarantined" ? [1, 2] : []), EvidenceAdmissionTestData.Meta());
        var before = await data.RowAsync("evidenceUploadReservations", reservation.ReservationId);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "evidenceIds"), Builders<BsonDocument>.Update.Set("value", 0L));
        await Reset(data);
        await new CollectionMigrations(data.DB, data.Catalog.Clock).ApplyAsync();
        Assert.Equal(before, await data.RowAsync("evidenceUploadReservations", reservation.ReservationId));
        var maximum = new[] { reservation.ReservationId, reservation.AttemptId, reservation.VersionRowId, reservation.LineageId }.Max();
        Assert.Equal(maximum + 1, (await new IdAllocator(data.DB).NextAsync("evidenceIds")).Value);
        Assert.Equal(reservation, await data.ReserveAsync());
    }

    [WindowsStorageTheory]
    [InlineData("owner")]
    [InlineData("uploader")]
    [InlineData("attempt-binding")]
    [InlineData("quota")]
    [InlineData("charge")]
    [InlineData("reserve-audit")]
    [InlineData("receipt")]
    [InlineData("history")]
    [InlineData("counter")]
    public async Task Malformed_graph_fails_before_target_validator_or_counter_changes(string broken)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync();
        var row = await data.RowAsync("evidenceUploadReservations", reservation.ReservationId);
        switch (broken)
        {
            case "owner": await Change("evidenceLineages", reservation.LineageId, "owner.shopId", 201L); break;
            case "uploader": await Change("evidenceLineages", reservation.LineageId, "uploader.subject", "forged-subject"); break;
            case "attempt-binding": await Change("evidenceUploadAttempts", reservation.AttemptId, "reservationId", 999L); break;
            case "quota": await data.Rows("evidenceStorePolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("chargedBytes", 0L)); break;
            case "charge": await Change("evidenceUploadReservations", reservation.ReservationId, "chargedBytes", 1024L); break;
            case "reserve-audit": await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["reserveAuditId"])); break;
            case "receipt": await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "evidence.catalog-image.reserve")); break;
            case "history":
                await data.StageClaimAsync(reservation);
                await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("kind", "evidence.claim")); break;
            case "counter": await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "evidenceIds"),
                Builders<BsonDocument>.Update.Set("value", -1L)); break;
        }
        await Reset(data);
        var before = await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "evidenceLineages");
        var counter = await data.Rows("idCounters").Find(new BsonDocument("_id", "evidenceIds")).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(before, await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "evidenceLineages"));
        Assert.Equal(counter, await data.Rows("idCounters").Find(new BsonDocument("_id", "evidenceIds")).SingleAsync());
        async Task Change(string collection, long id, string field, BsonValue value) =>
            await data.Rows(collection).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set(field, value),
                new UpdateOptions { BypassDocumentValidation = true });
    }

    [WindowsStorageTheory]
    [InlineData("payload")]
    [InlineData("missing-outbox")]
    [InlineData("completion-receipt")]
    [InlineData("completion-audit")]
    [InlineData("verdict")]
    public async Task Corrupted_quarantine_provenance_or_fake_verdict_never_passes_preflight(string broken)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync();
        await data.Service.UploadAsync(data.Actor, reservation.ReservationId, reservation.Revision, new MemoryStream([1, 2]), EvidenceAdmissionTestData.Meta());
        var row = await data.RowAsync("evidenceUploadReservations", reservation.ReservationId);
        switch (broken)
        {
            case "payload": await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("kind", "evidence.validation.requested"),
                Builders<BsonDocument>.Update.Set("payload.lineageId", 999L)); break;
            case "missing-outbox": await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("kind", "evidence.validation.requested")); break;
            case "completion-receipt": await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("operation", "evidence.catalog-image.complete")); break;
            case "completion-audit": await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", row["completionAuditId"])); break;
            case "verdict": await data.Rows("documentVersions").UpdateOneAsync(new BsonDocument("_id", reservation.VersionRowId),
                Builders<BsonDocument>.Update.Set("scanStatus", "clean"), new UpdateOptions { BypassDocumentValidation = true }); break;
        }
        await Reset(data);
        var before = await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "documentVersions");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(before, await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "documentVersions"));
    }

    [WindowsStorageFact]
    public async Task Mongo_strict_shapes_reject_malformed_ids_partial_tuple_and_private_extra_field()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync();
        var valid = await data.RowAsync("documentVersions", reservation.VersionRowId);
        foreach (var change in new Action<BsonDocument>[] {
            r => r["_id"] = 777, r => r["schemaVersion"] = 1L, r => r["contentVersion"] = 2L,
            r => r["length"] = 1L, r => r["sha256"] = new string('A', 64),
            r => r["detectedMime"] = "image/png", r => r["scanStatus"] = "clean",
            r => r["path"] = "private/location", r => r["createdAt"] = new BsonDateTime(long.MaxValue) })
        {
            var row = valid.DeepClone().AsBsonDocument;
            row["_id"] = 777L; row["lineageId"] = 778L; row["reservationId"] = 779L; change(row);
            Assert.False(EvidenceSchemas.IsValid("documentVersions", row));
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("documentVersions").InsertOneAsync(row))).WriteError.Code);
        }
    }

    [WindowsStorageTheory]
    [InlineData("missing-publication")]
    [InlineData("epoch")]
    public async Task Impossible_claim_history_fails_before_any_target_mutation(string broken)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        await data.Service.UploadAsync(data.Actor, r.ReservationId, r.Revision, new MemoryStream([1, 2]), EvidenceAdmissionTestData.Meta());
        if (broken == "epoch")
            await data.Rows("evidenceUploadAttempts").UpdateOneAsync(new BsonDocument("_id", r.AttemptId),
                Builders<BsonDocument>.Update.Inc("claimEpoch", 1L));
        else
        {
            var claim = await data.Rows("auditEvents").Find(new BsonDocument { { "kind", "evidence.claim" }, { "evidence.from.reservation", 3L } }).SingleAsync();
            await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", claim["_id"]));
            foreach (var (collection, id) in new[] { ("evidenceUploadReservations", r.ReservationId), ("evidenceUploadAttempts", r.AttemptId), ("documentVersions", r.VersionRowId) })
                await data.Rows(collection).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Inc("revision", -1L));
            await data.Rows("evidenceUploadAttempts").UpdateOneAsync(new BsonDocument("_id", r.AttemptId), Builders<BsonDocument>.Update.Inc("claimEpoch", -1L));
            await data.Rows("documentVersions").UpdateOneAsync(new BsonDocument("_id", r.VersionRowId), Builders<BsonDocument>.Update.Inc("fence", -1L));
            await data.Rows("auditEvents").UpdateOneAsync(new BsonDocument("kind", "evidence.quarantine"),
                Builders<BsonDocument>.Update.Inc("evidence.from.reservation", -1L).Inc("evidence.from.attempt", -1L)
                    .Inc("evidence.to.reservation", -1L).Inc("evidence.to.attempt", -1L));
            await data.Rows("idempotencyRecords").UpdateOneAsync(new BsonDocument("operation", "evidence.catalog-image.complete"),
                Builders<BsonDocument>.Update.Inc("result.revision", -1L));
        }
        await Reset(data);
        var before = await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "documentVersions");
        var counter = await data.Rows("idCounters").Find(new BsonDocument("_id", "evidenceIds")).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(before, await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "documentVersions"));
        Assert.Equal(counter, await data.Rows("idCounters").Find(new BsonDocument("_id", "evidenceIds")).SingleAsync());
    }
    [WindowsStorageFact]
    public async Task Claimed_history_with_erased_durable_publication_claim_fails_preflight()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await EvidenceAdmissionBoundaryTests.FreezeAsync(data);
        await EvidenceAdmissionBoundaryTests.ClaimAsync(data, frozen.Reservation.ReservationId, frozen.Stage.RequestId, frozen.Stage.Until);
        var clear = Builders<BsonDocument>.Update.Set("claimKind", BsonNull.Value).Set("claimToken", BsonNull.Value)
            .Set("claimUntil", BsonNull.Value).Set("claimRequestId", BsonNull.Value);
        await data.Rows("evidenceUploadAttempts").UpdateOneAsync(new BsonDocument("_id", frozen.Reservation.AttemptId), clear);
        await Reset(data);
        var before = await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "evidenceUploadAttempts");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(data.DB, data.Catalog.Clock).ApplyAsync());
        Assert.Equal(before, await EvidenceAdmissionMigrationTests.OptionsAsync(data.DB, "evidenceUploadAttempts"));
    }
    private static Task Reset(EvidenceAdmissionTestData data) =>
        data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 13));
}
