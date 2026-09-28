namespace UnitToko.Platform.Storage;

/// <summary>Explicit local construction only; production registration remains unavailable.</summary>
internal static class LocalPrivateEvidenceStore
{
    internal static StoreActivation TryOpen(string root, string storeId, IReadOnlyCollection<string> trustedOperatorSids) =>
        OperatingSystem.IsWindows() ? WindowsLocalEvidenceStore.TryOpen(root, storeId, trustedOperatorSids) :
            new(null, "storage_platform_unsupported");
}
