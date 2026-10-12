namespace UnitToko.Platform.Integrity;

/// <summary>Process-level seal key ring. Configured once by the API host from the protected key directory
/// (or by tests). Null keeps every owner on its existing full proof with no seal reads or writes.</summary>
public static class ProvenanceSealRing
{
    private static SealKeyRing? current;
    public static SealKeyRing? Current => Volatile.Read(ref current);
    public static void Configure(SealKeyRing? ring) => Volatile.Write(ref current, ring);
}
