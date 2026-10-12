namespace UnitToko.Platform.Integrity;

/// <summary>A present provenance seal that failed verification: possible tampering, never routine unavailability.</summary>
public sealed record SealIntegrityAlert(string Kind, long OwnerId, string Check);

/// <summary>Integrity alert channel. The refusal itself is unchanged; the host subscribes to log and page the
/// named integrity responder. A failing subscriber never alters or masks the refusal.</summary>
public static class SealIntegrityAlerts
{
    public static event Action<SealIntegrityAlert>? Raised;

    internal static void Raise(string kind, long ownerId, string check)
    {
        if (Raised is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<SealIntegrityAlert>>())
            try { handler(new(kind, ownerId, check)); } catch (Exception) { /* alerting must not change the refusal */ }
    }
}
