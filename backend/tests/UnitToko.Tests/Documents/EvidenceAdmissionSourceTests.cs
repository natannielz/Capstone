using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Storage;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceAdmissionSourceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Exact_admitted_byte_limit_requires_EOF_and_quarantines_once()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        using var source = new EvidenceAdmissionTests.CountingSource(new byte[1024]);
        var result = await data.Service.UploadAsync(data.Actor, r.ReservationId, r.Revision, source, EvidenceAdmissionTestData.Meta());
        Assert.Equal("quarantined", result.State);
        Assert.True(source.Reads >= 2);
        Assert.Equal(1024L, (await data.RowAsync("documentVersions", r.VersionRowId))["length"].AsInt64);
    }

    [WindowsStorageFact]
    public async Task Source_exception_retains_partial_stage_and_charge_without_frozen_tuple()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync();
        using var source = new ThrowingSource();
        var outcome = await data.Service.UploadAsync(data.Actor, r.ReservationId, r.Revision, source, EvidenceAdmissionTestData.Meta());
        Assert.Equal("interrupted", outcome.State);
        var attempt = await data.RowAsync("evidenceUploadAttempts", r.AttemptId);
        Assert.Equal("storage_interrupted", attempt["lastSafeFailure"]);
        Assert.True(attempt["sha256"].IsBsonNull);
        Assert.Single(Directory.GetFiles(data.Root, "*.part"));
        Assert.Empty(Directory.GetFiles(data.Root, "*.ready"));
        Assert.Empty(Directory.GetFiles(data.Root, "*.original"));
        Assert.Equal(3072L, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedBytes"].AsInt64);
        Assert.Equal("unresolved", (await data.Service.RecoverAsync(data.Actor, r.ReservationId, EvidenceAdmissionTestData.Meta())).State);
    }

    [WindowsStorageFact]
    public async Task Sealed_file_without_committed_freeze_never_supplies_recovery_hash_or_new_stage()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var r = await data.ReserveAsync(); var claim = await data.StageClaimAsync(r);
        Assert.IsType<StageOutcome.Sealed>(await data.Registration.Store!.StageAsync(claim.Attempt,
            new MemoryStream([1, 2, 3]), EvidenceAdmissionBoundaryTests.Budget, default));
        Assert.Equal("unresolved", (await data.Service.RecoverAsync(data.Actor, r.ReservationId, EvidenceAdmissionTestData.Meta())).State);
        using var replacement = new EvidenceAdmissionTests.CountingSource([9]);
        Assert.Equal("recovery_required", (await data.Service.UploadAsync(data.Actor, r.ReservationId, r.Revision, replacement, EvidenceAdmissionTestData.Meta())).State);
        Assert.Equal(0, replacement.Reads);
        Assert.True((await data.RowAsync("documentVersions", r.VersionRowId))["sha256"].IsBsonNull);
        Assert.Single(Directory.GetFiles(data.Root, "*.ready"));
        Assert.Empty(Directory.GetFiles(data.Root, "*.original"));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "evidence.validation.requested")));
    }

    private sealed class ThrowingSource : MemoryStream
    {
        private bool emitted;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (emitted) throw new InvalidOperationException("Synthetic source interruption.");
            emitted = true; buffer.Span[0] = 1; return ValueTask.FromResult(1);
        }
    }
}
