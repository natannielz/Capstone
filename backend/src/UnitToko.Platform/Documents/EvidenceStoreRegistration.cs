using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

/// <summary>Explicit local activation, never a configuration boolean or arbitrary non-null adapter.</summary>
internal sealed class EvidenceStoreRegistration
{
    internal string StoreId { get; }
    internal IPrivateEvidenceStore? Store { get; }
    internal string Reason { get; }
    private EvidenceStoreRegistration(string storeId, StoreActivation activation)
    {
        StoreId = storeId; Store = activation.Store; Reason = activation.Reason;
    }
    internal static EvidenceStoreRegistration Unavailable() => new("", new(null, "storage_unavailable"));
    internal static EvidenceStoreRegistration OpenLocal(string root, string storeId, IReadOnlyCollection<string> trustedOperatorSids)
    {
        StorageValidation.Token(storeId);
        return new(storeId, LocalPrivateEvidenceStore.TryOpen(root, storeId, trustedOperatorSids));
    }
}
