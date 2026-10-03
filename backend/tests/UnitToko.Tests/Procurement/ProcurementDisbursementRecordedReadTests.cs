using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementDisbursementTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementDisbursementRecordedReadTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Independent_reader_opens_recorded_original_under_real_clock_and_revoked_grant_releases_unactivated_lease()
    {
        await using var data = await D.CreateAsync(fixture);
        await data.UploadAsync(); await data.ValidateAsync();
        var firstDraftId = data.DraftId; var firstReservation = data.Reservation.ReservationId; var firstReference = data.Reference;
        var first = await data.RecordAsync(await data.PreviewRecordAsync(), D.Meta("recorded-read-first"));
        Assert.Equal("recorded_unverified", first["status"].AsString);
        data.Draft = await data.CreateDraftAsync("recorded-read-second-draft", data.Input(4000, "SYNTHETIC-RECORDED-READ-SECOND"));
        (data.Reservation, data.Reference) = await data.UploadForAsync(data.DraftId, "recorded-read-second-image"); await data.ValidateAsync();
        var second = await data.RecordAsync(await data.PreviewRecordAsync(), D.Meta("recorded-read-second"));
        Assert.NotEqual(first["disbursementId"], second["disbursementId"]);
        Assert.Equal(2, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument()));
        var policy = await data.Rows("evidenceReadPolicies").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(5000L, policy["leaseDurationMs"].AsInt64);
        Assert.Equal(10000L, policy["verificationDurationMs"].AsInt64);
        Assert.Equal(60000L, policy["requestDurationMs"].AsInt64);
        var preserved = await data.PreservedAsync(); var state = await data.StateAsync();

        // The real browser uses an advancing clock. A frozen fixture clock would hide
        // expiration while the recorded owner's immutable source history is checked.
        data.Clock.UseSystemClock = true;
        Assert.NotEqual(data.Actor.UserId, data.EvidenceReader.UserId);
        var initialLeaseCount = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        foreach (var denied in new[] { data.Reader, data.Outsider })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenReadAsync(denied, firstDraftId,
                firstReservation, 1, firstReference, D.ReadMetadata));
        Assert.Equal(initialLeaseCount, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));

        var original = await data.Evidence.OpenReadAsync(data.EvidenceReader, firstDraftId, firstReservation, 1, firstReference, D.ReadMetadata);
        await using (original) {
            Assert.Equal("image/png", original.Mime); Assert.Equal(data.Bytes.Length, original.Length); Assert.Equal(firstReference, original.Reference);
            using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes);
            Assert.Equal(data.Bytes, bytes.ToArray());
            Assert.Equal(firstReference.Sha256, Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }
        Assert.False(original.Content.CanRead);
        var readLeases = await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", data.EvidenceReader.UserId)).ToListAsync();
        var successful = Assert.Single(readLeases);
        Assert.Equal("released", successful["state"].AsString); Assert.Equal(firstReservation, successful["reservationId"].AsInt64);
        Assert.Equal(new[] { "acquired", "activated", "released" }, (await data.Rows("evidenceReadAudits")
            .Find(new BsonDocument("leaseId", successful["_id"])).Sort(new BsonDocument("revision", 1)).ToListAsync()).Select(row => row["kind"].AsString));
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());

        var core = data.Core; var verified = 0;
        core.ProcurementDisbursementReadObserver = new(async () => {
            verified++;
            Assert.True((await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument {
                { "userId", data.EvidenceReader.UserId }, { "permissions", Permissions.ProcurementDisbursementEvidenceRead } },
                Builders<BsonDocument>.Update.Pull("permissions", Permissions.ProcurementDisbursementEvidenceRead))).ModifiedCount > 0);
        });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => core.OpenProcurementDisbursementReadAsync(data.EvidenceReader,
            new(EvidenceTargetKind.ProcurementDisbursement, firstDraftId), 1, firstReservation, firstReference, D.ReadMetadata));
        Assert.Equal(1, verified);
        var deniedLease = await data.Rows("evidenceReadLeases").Find(new BsonDocument {
            { "actor.userId", data.EvidenceReader.UserId }, { "_id", new BsonDocument("$ne", successful["_id"]) } }).SingleAsync();
        Assert.Equal("released", deniedLease["state"].AsString);
        Assert.Equal(new[] { "acquired", "released" }, (await data.Rows("evidenceReadAudits")
            .Find(new BsonDocument("leaseId", deniedLease["_id"])).Sort(new BsonDocument("revision", 1)).ToListAsync()).Select(row => row["kind"].AsString));
        var finalLeaseCount = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        Assert.Equal(initialLeaseCount + 2, finalLeaseCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenReadAsync(data.EvidenceReader,
            firstDraftId, firstReservation, 1, firstReference, D.ReadMetadata));
        Assert.Equal(finalLeaseCount, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument("state", new BsonDocument("$ne", "released"))).AnyAsync());
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        var currentPolicy = await data.Rows("evidenceReadPolicies").Find(new BsonDocument()).SingleAsync();
        policy.Remove("fence"); currentPolicy.Remove("fence"); Assert.Equal(policy, currentPolicy);
    }
}
