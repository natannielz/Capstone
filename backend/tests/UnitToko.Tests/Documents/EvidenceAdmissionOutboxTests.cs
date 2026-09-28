using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionOutboxTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_quarantine_event_survives_bounded_queue_lifecycle_without_changing_evidence()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await data.ReserveAsync();
        byte[] bytes = [1, 2, 3, 4];
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var source = new MemoryStream(bytes);
        var upload = await data.Service.UploadAsync(data.Actor, reservation.ReservationId, reservation.Revision,
            source, EvidenceAdmissionTestData.Meta());
        Assert.Equal("quarantined", upload.State);

        const string kind = "evidence.validation.requested";
        var sourceKey = $"evidence.quarantined:{reservation.VersionRowId}:1";
        var expectedPayload = new BsonDocument
        {
            { "lineageId", reservation.LineageId }, { "versionRowId", reservation.VersionRowId },
            { "reservationId", reservation.ReservationId }, { "attemptId", reservation.AttemptId },
            { "contentVersion", 1L }, { "contentRevision", 1L }, { "sha256", hash }, { "lifecycleRevision", 2L }
        };
        var message = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", sourceKey)).SingleAsync();
        AssertBinding(message);
        Assert.Equal("pending", message["status"]);
        var versionBefore = await data.RowAsync("documentVersions", reservation.VersionRowId);
        var reservationBefore = await data.RowAsync("evidenceUploadReservations", reservation.ReservationId);
        var attemptBefore = await data.RowAsync("evidenceUploadAttempts", reservation.AttemptId);
        var policyBefore = await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync();

        var queue = new OutboxStore(data.DB, data.Catalog.Clock);
        // Explicit synthetic test limits; no production consumer or policy is registered.
        var claimPolicy = new OutboxClaimPolicy([kind], 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var first = Assert.IsType<BsonDocument>(await queue.ClaimAsync("evidence-test-first", claimPolicy));
        Assert.Equal(message["_id"], first["_id"]);
        AssertBinding(first);
        Assert.Equal("leased", first["status"]);
        Assert.Equal(1, first["attempts"].AsInt32);
        Assert.Equal(3, first["attemptLimit"].AsInt32);

        var renewed = Assert.IsType<BsonDocument>(await queue.RenewAsync(first, TimeSpan.FromSeconds(10)));
        AssertBinding(renewed);
        Assert.Equal(first["leaseToken"], renewed["leaseToken"]);
        Assert.Equal(first["claimDeadline"], renewed["claimDeadline"]);
        Assert.True(renewed["leaseUntil"].AsBsonDateTime.MillisecondsSinceEpoch >
            first["leaseUntil"].AsBsonDateTime.MillisecondsSinceEpoch);
        Assert.True(await queue.RetryAsync(renewed, TimeSpan.FromSeconds(2)));
        var retry = await data.Rows("outboxEvents").Find(new BsonDocument("_id", message["_id"])).SingleAsync();
        AssertBinding(retry);
        Assert.Equal("retry", retry["status"]);
        Assert.Equal("processing_retry", retry["lastFailureCode"]);
        Assert.False(retry.Contains("leaseToken"));
        Assert.Null(await queue.ClaimAsync("evidence-test-too-early", claimPolicy));

        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(2);
        var reclaimed = Assert.IsType<BsonDocument>(await queue.ClaimAsync("evidence-test-second", claimPolicy));
        Assert.Equal(message["_id"], reclaimed["_id"]);
        AssertBinding(reclaimed);
        Assert.Equal(2, reclaimed["attempts"].AsInt32);
        Assert.Equal(3, reclaimed["attemptLimit"].AsInt32);
        Assert.NotEqual(first["leaseToken"], reclaimed["leaseToken"]);
        Assert.False(await queue.CompleteAsync(renewed));
        Assert.True(await queue.CompleteAsync(reclaimed));

        var completed = await data.Rows("outboxEvents").Find(new BsonDocument("_id", message["_id"])).SingleAsync();
        AssertBinding(completed);
        Assert.Equal("completed", completed["status"]);
        Assert.Equal(2, completed["attempts"].AsInt32);
        Assert.False(completed.Contains("leaseToken"));
        Assert.Null(await queue.ClaimAsync("evidence-test-after-complete", claimPolicy));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("sourceKey", sourceKey)));

        var versionAfter = await data.RowAsync("documentVersions", reservation.VersionRowId);
        Assert.Equal(versionBefore, versionAfter);
        Assert.Equal(reservationBefore, await data.RowAsync("evidenceUploadReservations", reservation.ReservationId));
        Assert.Equal(attemptBefore, await data.RowAsync("evidenceUploadAttempts", reservation.AttemptId));
        Assert.Equal(policyBefore, await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync());
        Assert.Equal("quarantined", versionAfter["state"]);
        Assert.Equal("unavailable", versionAfter["scanStatus"]);
        Assert.Equal("unavailable", versionAfter["decoderStatus"]);
        Assert.True(versionAfter["detectedMime"].IsBsonNull);
        Assert.Equal(hash, versionAfter["sha256"]);
        Assert.Equal(3072L, policyBefore["chargedBytes"].AsInt64);
        Assert.Equal(1L, policyBefore["chargedReservations"].AsInt64);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(data.Root, "*.original"))));
        Assert.Equal(upload, await data.Service.RecoverAsync(data.Actor, reservation.ReservationId, EvidenceAdmissionTestData.Meta()));

        void AssertBinding(BsonDocument row)
        {
            Assert.Equal(kind, row["kind"]);
            Assert.Equal(sourceKey, row["sourceKey"]);
            Assert.Equal(8, row["payload"].AsBsonDocument.ElementCount);
            Assert.Equal(expectedPayload, row["payload"].AsBsonDocument);
            Assert.All(row["payload"].AsBsonDocument.Where(field => field.Name != "sha256"),
                field => Assert.Equal(BsonType.Int64, field.Value.BsonType));
        }
    }
}
