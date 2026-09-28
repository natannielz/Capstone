using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

/// <summary>A requested setup, not proof of operational acceptance or usable stock authority.</summary>
public sealed record StockSourceSetupInput(StockLocation Location, string PhysicalKey, string Label,
    StockAuthorityMode Mode, StockPoolControl Control, long MaximumAgeMilliseconds, long SafetyBufferBaseUnits,
    string? ExternalNamespace, string? ReservationProtocolReference,
    string DiscrepancyOwnerReference, string HandoffReference);

public enum StockSourceSetupDecision { Approve, Reject, Withdraw }

public static class StockSourceSetupPolicy
{
    public static void Validate(StockSourceSetupInput input)
    {
        if (input is null || input.Location is null) throw Invalid();
        input.Location.Validate();
        if (!Key(input.PhysicalKey) || !Text(input.Label, 160) ||
            !Text(input.DiscrepancyOwnerReference, 160) || !Text(input.HandoffReference, 160) ||
            input.MaximumAgeMilliseconds <= 0 || input.MaximumAgeMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond ||
            input.SafetyBufferBaseUnits < 0 ||
            input.Mode is not (StockAuthorityMode.WebLedger or StockAuthorityMode.ExternalLedger))
            throw Invalid();
        if (input.Mode == StockAuthorityMode.WebLedger)
        {
            if (input.Control is not (StockPoolControl.ExclusiveWebAllocation or StockPoolControl.SharedWebLedger) ||
                input.ExternalNamespace is not null || input.ReservationProtocolReference is not null) throw Invalid();
        }
        else if (input.Control != StockPoolControl.ExternalHold || !Key(input.ExternalNamespace) ||
            !Text(input.ReservationProtocolReference, 160)) throw Invalid();
    }
    // One case and ASCII alphabet prevent visually equivalent pool keys bypassing uniqueness.
    public static bool Key(string? value) => value is { Length: > 0 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    public static bool Text(string? value, int maximum) => value is not null && StockCountPolicy.CanonicalText(value, maximum);
    private static DomainValidationException Invalid() => new("invalid_stock_source_setup", "Stock source setup requires complete explicit canonical facts");
}
