using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reserve_has_four_distinct_ids_exact_bindings_and_retained_three_copy_charge(bool sku)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku);
        var result = await data.ReserveAsync();
        Assert.Equal(4, new[] { result.ReservationId, result.LineageId, result.VersionRowId, result.AttemptId }.Distinct().Count());
        Assert.All(new[] { result.ReservationId, result.LineageId, result.VersionRowId, result.AttemptId }, id => Assert.True(id > 0));
        Assert.Equal(1, result.ContentVersion);
        var row = await data.RowAsync("evidenceUploadReservations", result.ReservationId);
        Assert.Equal(CatalogAuthoringTestData.Shop.OrganizationId, row["owner"]["organizationId"].AsInt64);
        Assert.Equal(data.Actor.UserId, row["uploader"]["userId"].AsInt64);
        Assert.Equal(3072, row["chargedBytes"].AsInt64);
        Assert.Equal("CatalogImage", row["purpose"]);
        Assert.Equal(sku, row["admittedDefinitionId"].IsInt64);
        var policy = await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1, policy["chargedReservations"].AsInt64); Assert.Equal(3072, policy["chargedBytes"].AsInt64);
        Assert.Null((await data.StatusAsync(result.ReservationId)).Reference);
    }

    [WindowsStorageFact]
    public async Task Concurrent_same_key_and_renamed_retry_return_original_identity_after_head_change()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => { for (var n = 0; ; n++) { try { return await data.ReserveAsync(request: new(data.Target, expectedTargetRevision: 1)); } catch (PersistenceConflictException error) when (error.Code == "version_conflict" && n < 2) { } } }));
        Assert.All(all, result => Assert.Equal(all[0], result));
        await data.Rows("productFamilies").UpdateOneAsync(new BsonDocument("_id", data.Target.Id), Builders<BsonDocument>.Update.Inc("revision", 1L));
        var replay = await data.ReserveAsync(request: new(data.Target, expectedTargetRevision: 1, filename: "renamed.png"));
        Assert.Equal(all[0], replay);
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ReserveAsync(request: new(data.Target, expectedTargetRevision: 1), reason: "different"));
        Assert.Equal("idempotency_key_reused", conflict.Code);
    }

    [WindowsStorageTheory]
    [InlineData("buyer")]
    [InlineData("other-owner")]
    [InlineData("inactive")]
    [InlineData("missing-policy")]
    [InlineData("unavailable-store")]
    public async Task Unsupported_authority_or_missing_configuration_creates_no_reservation(string denied)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, policy: denied != "missing-policy");
        if (denied == "inactive") await data.Rows("productFamilies").UpdateOneAsync(new BsonDocument("_id", data.Target.Id), Builders<BsonDocument>.Update.Set("active", false));
        var actor = denied == "buyer" ? data.Catalog.Buyer : denied == "other-owner" ? data.Catalog.Other : data.Actor;
        var service = denied == "unavailable-store" ? data.NewService(EvidenceStoreRegistration.Unavailable()) : data.Service;
        await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => service.ReserveAsync(tx, actor, new(data.Target), EvidenceAdmissionTestData.Meta(), ct)));
        Assert.Equal(0, await data.Rows("evidenceUploadReservations").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Simultaneous_reservations_respect_count_and_byte_cap()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        await data.Rows("evidenceStorePolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("maximumHeldReservations", 1L).Set("maximumHeldBytes", 3072L));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i => {
            try { await data.ReserveAsync("key-" + i); return true; } catch (PersistenceConflictException) { return false; }
        }));
        Assert.Equal(1, outcomes.Count(x => x));
        Assert.Equal(1, await data.Rows("evidenceUploadReservations").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Another_authorized_staff_actor_may_status_but_never_stage_original_upload()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var other = await data.Catalog.Base.AddActorAsync(7000, CatalogAuthoringTestData.Shop, Permissions.CatalogManage);
        var reserved = await data.ReserveAsync();
        Assert.Equal(reserved.ReservationId, (await data.StatusAsync(reserved.ReservationId, other)).ReservationId);
        await Assert.ThrowsAnyAsync<Exception>(() => data.StageClaimAsync(reserved, actor: other));
        Assert.Equal("fresh", (await data.RowAsync("evidenceUploadAttempts", reserved.AttemptId))["state"]);
    }

    [WindowsStorageFact]
    public async Task Real_upload_ends_quarantined_with_exact_receipt_hash_outbox_and_unavailable_verdicts()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var outcome = await data.Service.UploadAsync(data.Actor, reserved.ReservationId, reserved.Revision, new MemoryStream(bytes), EvidenceAdmissionTestData.Meta());
        Assert.Equal("quarantined", outcome.State);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), outcome.Evidence!.Reference.Sha256);
        Assert.Equal(reserved.LineageId, outcome.Evidence.Reference.Id);
        var row = await data.RowAsync("documentVersions", reserved.VersionRowId);
        Assert.Equal("quarantined", row["state"]); Assert.True(row["detectedMime"].IsBsonNull);
        Assert.Equal("unavailable", row["scanStatus"]); Assert.Equal("unavailable", row["decoderStatus"]);
        var message = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "evidence.validation.requested")).SingleAsync();
        Assert.Equal($"evidence.quarantined:{reserved.VersionRowId}:1", message["sourceKey"]);
        Assert.Equal(8, message["payload"].AsBsonDocument.ElementCount);
        var recovered = await data.Service.RecoverAsync(data.Actor, reserved.ReservationId, EvidenceAdmissionTestData.Meta());
        Assert.Equal(outcome, recovered);
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "evidence.validation.requested")));
        Assert.Equal(3072, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedBytes"].AsInt64);
    }

    [WindowsStorageFact]
    public async Task Existing_attempt_rejects_replacement_source_without_consuming_it()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync();
        await data.StageClaimAsync(reserved);
        using var replacement = new CountingSource([9]);
        var outcome = await data.Service.UploadAsync(data.Actor, reserved.ReservationId, reserved.Revision, replacement, EvidenceAdmissionTestData.Meta());
        Assert.Equal("recovery_required", outcome.State); Assert.Equal(0, replacement.Reads);
    }

    [WindowsStorageTheory]
    [InlineData(0)]
    [InlineData(1025)]
    public async Task Empty_or_oversize_source_never_freezes_or_quarantines_and_retains_quota(int length)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync();
        var outcome = await data.Service.UploadAsync(data.Actor, reserved.ReservationId, reserved.Revision, new MemoryStream(new byte[length]), EvidenceAdmissionTestData.Meta());
        Assert.Equal("interrupted", outcome.State);
        var attempt = await data.RowAsync("evidenceUploadAttempts", reserved.AttemptId);
        Assert.Equal("interrupted", attempt["state"]); Assert.True(attempt["sha256"].IsBsonNull);
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "evidence.validation.requested")));
        Assert.Equal(3072, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedBytes"].AsInt64);
    }

    [WindowsStorageFact]
    public async Task Expired_stage_claim_cannot_be_taken_over_or_frozen()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reserved = await data.ReserveAsync(); var claim = await data.StageClaimAsync(reserved);
        data.Catalog.Clock.UtcNow = claim.Until.AddMilliseconds(1);
        await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.ClaimStageAsync(tx, data.Actor, reserved.ReservationId, claim.ReservationRevision, new string('c', 64), claim.Until, EvidenceAdmissionTestData.Meta(), ct)));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.FreezeContentAsync(tx, data.Actor, claim,
            new SealedStage(claim.Attempt, new(1, new string('d', 64))), EvidenceAdmissionTestData.Meta(), ct)));
    }

    [WindowsStorageFact]
    public async Task Caught_required_audit_failure_aborts_earlier_business_write_and_quota()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        await data.DB.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" },
            { "validator", new BsonDocument("kind", new BsonDocument("$ne", "evidence.reserve")) } });
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Rows("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 987L }, { "schemaVersion", 1 } });
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.ReserveAsync(tx, data.Actor, new(data.Target), EvidenceAdmissionTestData.Meta()));
        Assert.False(tx.IsInTransaction);
        await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitTransactionAsync());
        Assert.Equal(0, await data.Rows("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument("_id", 987L)));
        Assert.Equal(0, await data.Rows("evidenceUploadReservations").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedBytes"].AsInt64);
    }

    internal sealed class CountingSource(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Reads++; return base.ReadAsync(buffer, ct); }
    }
}
