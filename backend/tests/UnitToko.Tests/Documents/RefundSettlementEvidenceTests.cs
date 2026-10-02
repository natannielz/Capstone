using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundSettlementEvidenceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Independent_verifier_reads_only_the_exact_recorded_original_under_its_distinct_purpose_and_current_grant() {
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        var original = await data.PermanentAsync(); var state = await data.SettlementStateAsync();
        var policies = await data.Rows("evidenceReadPolicies").Find(new BsonDocument()).ToListAsync();
        Assert.NotEmpty(policies); Assert.All(policies, row => { Assert.Equal(5000L, row["leaseDurationMs"].AsInt64); Assert.Equal(10000L, row["verificationDurationMs"].AsInt64); });
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Verifier.UserId)).SingleAsync();
        Assert.DoesNotContain(new BsonString(Permissions.DisbursementRecord), grant["permissions"].AsBsonArray);
        var leases = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        var unit = new MongoUnitOfWork(data.Catalog.Client);
        var core = new EvidenceService(data.Database, unit, new(data.Database), data.Clock, data.Payout.Source.Allocation.Source.Read.Files.Registration,
            refundPayoutTargets: new RefundSettlementEvidenceTarget(data.Database, data.Service, data.PayoutId, data.Payout.Reservation.ReservationId));
        await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync((tx, ct) => core.AcquireRefundSettlementReadAsync(tx, data.Verifier,
            new(EvidenceTargetKind.RefundPayout, data.Payout.DraftId), 1, data.Payout.Reservation.ReservationId, data.Payout.Reference,
            new("refund-payout-evidence-view", "wrong-settlement-read-purpose"), ct)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Payout.Evidence.ReadSettlementOriginalAsync(data.Verifier, long.MaxValue,
            data.Payout.Reservation.ReservationId, data.Payout.Reference));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Payout.Evidence.ReadSettlementOriginalAsync(data.Verifier, data.PayoutId,
            data.Payout.Reservation.ReservationId + 1, data.Payout.Reference));
        var wrong = new EvidenceRef(data.Payout.Reference.Id, data.Payout.Reference.Version, new string(data.Payout.Reference.Sha256[0] == 'a' ? 'b' : 'a', 64));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Payout.Evidence.ReadSettlementOriginalAsync(data.Verifier, data.PayoutId,
            data.Payout.Reservation.ReservationId, wrong));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", grant["_id"]), Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementVerify));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => OpenAsync(data));
        await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
        Assert.Equal(leases, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        await using (var opened = await OpenAsync(data)) {
            Assert.Equal(data.Payout.Reference, opened.Reference); Assert.Equal("image/png", opened.Mime); Assert.Equal(data.Payout.Bytes.Length, opened.Length);
            using var bytes = new MemoryStream(); await opened.Content.CopyToAsync(bytes); Assert.Equal(data.Payout.Bytes, bytes.ToArray());
        }
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", data.Verifier.UserId)).SingleAsync();
        Assert.Equal("released", lease["state"]); Assert.Equal("RefundPayout", lease["target"]["kind"]);
        Assert.Equal(data.Payout.DraftId, lease["target"]["id"].AsInt64); Assert.Equal(data.Payout.Reservation.ReservationId, lease["reservationId"].AsInt64);
        Assert.Equal(PaymentSubmissionBson.Reference(data.Payout.Reference), lease["reference"]);
        var reservation = await data.Rows("evidenceUploadReservations").Find(new BsonDocument("_id", data.Payout.Reservation.ReservationId)).SingleAsync();
        Assert.Equal("RefundPayoutImage", reservation["purpose"]); Assert.Equal("RefundPayout", reservation["target"]["kind"]);
        Assert.Equal(original, await data.PermanentAsync()); Assert.Equal(state, await data.SettlementStateAsync());
    }

    [WindowsStorageFact]
    public async Task Recorder_permission_and_equivalent_current_person_never_grant_independent_original_review() {
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        var original = await data.PermanentAsync(); var state = await data.SettlementStateAsync();
        var leases = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => OpenAsync(data, data.Payout.Actor));
        await data.Catalog.Base.AddGrantAsync(data.Payout.Actor, 990000 + data.Payout.Actor.UserId, CatalogAuthoringTestData.Shop, [Permissions.DisbursementVerify]);
        var recorder = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => OpenAsync(data, data.Payout.Actor)); Assert.Equal("independent_person_required", recorder.Code);
        await RefundSettlementTests.MergePeopleAsync(data, data.Payout.Actor.UserId, data.Verifier.UserId);
        var people = await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock)
            .ResolveAsync(tx, [data.Verifier.UserId, data.Payout.Actor.UserId], ct));
        Assert.Equal(people[0].PersonId, people[1].PersonId);
        var alias = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => OpenAsync(data)); Assert.Equal("independent_person_required", alias.Code);
        Assert.Equal(leases, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(original, await data.PermanentAsync()); Assert.Equal(state, await data.SettlementStateAsync());
    }

    [WindowsStorageFact]
    public async Task Case_read_revoked_after_actual_document_target_capture_denies_final_fence_and_rolls_back() {
        await using var data = await RefundSettlementTests.Data.CreateAsync(fixture);
        var original = await data.PermanentAsync(); var state = await data.SettlementStateAsync();
        var filter = new BsonDocument("userId", data.Verifier.UserId);
        var grants = new BsonArray(await data.Rows("authorizationGrants").Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var targets = new RefundSettlementEvidenceTarget(data.Database, data.Service, data.PayoutId, data.Payout.Reservation.ReservationId);
        var reads = new AuthorizationStore(data.Database, new(data.Catalog.Client), data.Clock); var captured = 0;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => reads.ReadAsync(data.Verifier, (_, tx, ct) => targets.WithReadTargetAsync(tx,
            data.Verifier, new(EvidenceTargetKind.RefundPayout, data.Payout.DraftId), 1, data.Payout.Reference, async (target, inner) => {
                captured++; Assert.Equal(data.PayoutId, target.PayoutId); Assert.Equal(data.Payout.Reservation.ReservationId, target.PayoutReservationId);
                Assert.Equal(data.Payout.Actor.UserId, target.PayoutUploaderId); Assert.Equal(1L, target.Revision);
                var revoke = new BsonDocument { { "userId", data.Verifier.UserId }, { "permissions", Permissions.CaseRead } };
                var changed = await data.Rows("authorizationGrants").UpdateManyAsync(tx, revoke, Builders<BsonDocument>.Update.Pull("permissions", Permissions.CaseRead), cancellationToken: inner);
                Assert.True(changed.ModifiedCount > 0); Assert.Equal(0, await data.Rows("authorizationGrants").CountDocumentsAsync(tx, revoke, cancellationToken: inner));
                return true;
            }, ct), default));
        Assert.Equal(1, captured); Assert.Equal(grants, new BsonArray(await data.Rows("authorizationGrants").Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(original, await data.PermanentAsync()); Assert.Equal(state, await data.SettlementStateAsync());
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", data.Verifier.UserId)).AnyAsync());
    }

    private static Task<ProtectedEvidenceRead> OpenAsync(RefundSettlementTests.Data data, UnitToko.Modules.Identity.Contracts.ActorContext? actor = null) =>
        data.Payout.Evidence.ReadSettlementOriginalAsync(actor ?? data.Verifier, data.PayoutId, data.Payout.Reservation.ReservationId, data.Payout.Reference);
}
