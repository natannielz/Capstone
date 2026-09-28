using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionIdentityTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Same_uploader_with_new_valid_session_can_recover_after_original_session_revocation()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var frozen = await EvidenceAdmissionBoundaryTests.FreezeAsync(data);
        var actor = data.Actor;
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "catalog-fixture@example.invalid",
            true, "fixture-connection", data.Catalog.Clock.UtcNow, data.Catalog.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await data.Unit.ExecuteAsync((tx, ct) => data.Catalog.Sessions.CreateAsync(tx,
            new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic-new-session", ct));
        await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[actor.UserId].OpaqueToken, "synthetic prior session revoked");
        var fresh = actor with { Session = issued.Session.Reference };
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.RecoverAsync(actor, frozen.Reservation.ReservationId, EvidenceAdmissionTestData.Meta()));
        Assert.Equal("quarantined", (await data.Service.RecoverAsync(fresh, frozen.Reservation.ReservationId, EvidenceAdmissionTestData.Meta())).State);
    }

    [WindowsStorageTheory]
    [InlineData("freeze")]
    [InlineData("recover")]
    [InlineData("publication")]
    [InlineData("complete")]
    public async Task Same_scope_staff_cannot_mutate_another_upload_at_any_later_phase(string phase)
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var other = await data.Catalog.Base.AddActorAsync(7100, CatalogAuthoringTestData.Shop, Permissions.CatalogManage);
        var r = await data.ReserveAsync(); var stage = await data.StageClaimAsync(r);
        var seal = Assert.IsType<StageOutcome.Sealed>(await data.Registration.Store!.StageAsync(stage.Attempt, new MemoryStream([1, 2]), EvidenceAdmissionBoundaryTests.Budget, default));
        if (phase == "freeze")
            await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.FreezeContentAsync(tx, other, stage, seal.Stage, EvidenceAdmissionTestData.Meta(), ct)));
        else
        {
            var expected = await data.Unit.ExecuteAsync((tx, ct) => data.Service.FreezeContentAsync(tx, data.Actor, stage, seal.Stage, EvidenceAdmissionTestData.Meta(), ct));
            if (phase == "recover") await Assert.ThrowsAnyAsync<Exception>(() => data.Service.RecoverAsync(other, r.ReservationId, EvidenceAdmissionTestData.Meta()));
            if (phase == "publication")
            {
                var current = await data.RowAsync("evidenceUploadReservations", r.ReservationId);
                await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.ClaimPublicationAsync(tx, other, r.ReservationId,
                    current["revision"].AsInt64, new string('d', 64), data.Catalog.Clock.UtcNow.AddSeconds(30), EvidenceAdmissionTestData.Meta(), ct)));
            }
            if (phase == "complete")
            {
                var claim = await EvidenceAdmissionBoundaryTests.ClaimAsync(data, r.ReservationId, stage.RequestId, stage.Until);
                await data.Registration.Store.PublishAsync(expected, EvidenceAdmissionBoundaryTests.Budget, default);
                var opened = Assert.IsType<OpenOutcome.Opened>(await data.Registration.Store.OpenVerifiedAsync(expected, EvidenceAdmissionBoundaryTests.Budget, default));
                await using var handle = opened.Handle;
                await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => data.Service.CompleteUploadAsync(tx, other, claim, handle, EvidenceAdmissionTestData.Meta(), ct)));
            }
        }
        Assert.Equal("reserved", (await data.RowAsync("documentVersions", r.VersionRowId))["state"]);
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "evidence.validation.requested")));
    }

    [WindowsStorageFact]
    public async Task Current_sku_definition_may_advance_during_staging_without_rewriting_admitted_snapshot_or_creating_association()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var reserved = await data.ReserveAsync();
        var admitted = await data.RowAsync("evidenceUploadReservations", reserved.ReservationId);
        using var source = new EvidenceAdmissionBoundaryTests.PausedSource();
        var upload = data.Service.UploadAsync(data.Actor, reserved.ReservationId, reserved.Revision, source, EvidenceAdmissionTestData.Meta());
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.Catalog.Service.ReviseDefinitionAsync(data.Actor, new(new(data.Target.Id), 1,
                CatalogAuthoringTestData.Definition(label: "Updated package")), EvidenceAdmissionTestData.Meta("revise-definition"));
        }
        finally { source.Continue.TrySetResult(); }
        Assert.Equal("quarantined", (await upload).State);
        var after = await data.RowAsync("evidenceUploadReservations", reserved.ReservationId);
        Assert.Equal(admitted["admittedDefinitionId"], after["admittedDefinitionId"]);
        var sku = await data.RowAsync("skus", data.Target.Id);
        Assert.NotEqual(admitted["admittedDefinitionId"], sku["currentDefinitionId"]);
        var definition = await data.RowAsync("variantDefinitions", sku["currentDefinitionId"].AsInt64);
        Assert.Empty(definition["imageAssetIds"].AsBsonArray);
    }

    [WindowsStorageFact]
    public async Task Completion_event_timestamp_is_identical_in_version_and_required_audit_under_advancing_clock()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var service = new EvidenceService(data.DB, data.Unit, new(data.DB), new AdvancingClock(data.Catalog.Clock.UtcNow), data.Registration);
        var reserved = await data.Unit.ExecuteAsync((tx, ct) => service.ReserveAsync(tx, data.Actor, new(data.Target), EvidenceAdmissionTestData.Meta(), ct));
        await service.UploadAsync(data.Actor, reserved.ReservationId, reserved.Revision, new MemoryStream([1]), EvidenceAdmissionTestData.Meta());
        var r = await data.RowAsync("evidenceUploadReservations", reserved.ReservationId);
        var v = await data.RowAsync("documentVersions", reserved.VersionRowId);
        var audit = await data.RowAsync("auditEvents", r["completionAuditId"].AsInt64);
        Assert.Equal(v["quarantinedAt"], audit["createdAt"]);
    }

    private sealed class AdvancingClock(DateTimeOffset start) : IClock
    {
        private long elapsed;
        public DateTimeOffset UtcNow => start.AddMilliseconds(Interlocked.Increment(ref elapsed));
    }
}
