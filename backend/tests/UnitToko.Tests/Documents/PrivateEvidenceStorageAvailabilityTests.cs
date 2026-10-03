using UnitToko.Platform.Storage;

namespace UnitToko.Tests.Documents;

public sealed class PrivateEvidenceStorageAvailabilityTests
{
    [Fact]
    public async Task Default_storage_denies_every_operation_without_reading_input()
    {
        var token = new string('a', 64);
        var attempt = new StorageAttempt(token, 1, 2, 1, new string('b', 64), new string('c', 64));
        var expected = new ExpectedPublication(attempt, 1, new StorageContent(3,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        var budget = new StorageIoBudget(10, TimeSpan.FromSeconds(1), 128);
        using var input = new MemoryStream("abc"u8.ToArray());
        IPrivateEvidenceStore store = new UnavailablePrivateEvidenceStore();
        Assert.IsType<StageOutcome.Unavailable>(await store.StageAsync(attempt, input, budget, default));
        Assert.Equal(0, input.Position);
        Assert.Equal(InspectionState.Unavailable, (await store.InspectStageAsync(expected, budget, default)).State);
        Assert.Equal(InspectionState.Unavailable, (await store.InspectOriginalAsync(expected, budget, default)).State);
        Assert.Equal(PublishState.Unavailable, (await store.PublishAsync(expected, budget, default)).State);
        Assert.IsType<OpenOutcome.Rejected>(await store.OpenVerifiedAsync(expected, budget, default));
        var native = Assert.IsType<NativeOpenOutcome.Rejected>(await store.OpenVerifiedForNativeValidationAsync(expected, budget, default));
        Assert.Equal(InspectionState.Unavailable, native.Inspection.State);
    }

    [UnsupportedStorageHostFact]
    public void Unsupported_host_cannot_activate_local_storage()
    {
        var result = LocalPrivateEvidenceStore.TryOpen("/untrusted", new string('a', 64), []);
        Assert.Null(result.Store);
        Assert.Equal("storage_platform_unsupported", result.Reason);
    }
}
public sealed class UnsupportedStorageHostFactAttribute : FactAttribute
{
    public UnsupportedStorageHostFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a non-Windows host to exercise unsupported-host rejection.";
    }
}
