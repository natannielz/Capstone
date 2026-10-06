using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

/// <summary>Observed empty physical handoff, signed by the submitting operator.
/// Zero is explicit: this workflow never imports balances, lots, custody or value.</summary>
public sealed record StockSourceEmptyHandoff(string Reference, DateTimeOffset ObservedAt,
    long PhysicalPackages, long CarriedValueMinorUnits, bool EmptyPoolConfirmed,
    bool PreviousWriterStopped, bool NoUnrecordedStock);

public sealed record StockSourceActivationExpectation(long PoolRevision, long PoolFence,
    long SuspensionRevision, long PolicyId, long PolicyRevision, long ActivationRevision,
    string ConfigurationHash)
{
    public void Validate()
    {
        if (PoolRevision <= 0 || PoolFence is < 0 or >= long.MaxValue - 2 || SuspensionRevision < 0 ||
            PolicyId <= 0 || PolicyRevision <= 0 || ActivationRevision is < 0 or >= long.MaxValue - 2 ||
            ConfigurationHash is not { Length: 64 } || ConfigurationHash.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw StockSourceActivationRules.Invalid();
    }
}

public static class StockSourceActivationRules
{
    public static void Validate(StockSourceEmptyHandoff handoff)
    {
        if (handoff is null || !StockSourceSetupPolicy.Text(handoff.Reference, 160) ||
            handoff.ObservedAt == default || handoff.ObservedAt.Offset != TimeSpan.Zero ||
            handoff.ObservedAt.Ticks % TimeSpan.TicksPerMillisecond != 0 || handoff.PhysicalPackages != 0 ||
            handoff.CarriedValueMinorUnits != 0 || !handoff.EmptyPoolConfirmed ||
            !handoff.PreviousWriterStopped || !handoff.NoUnrecordedStock) throw Invalid();
    }
    internal static DomainValidationException Invalid() => new("invalid_stock_source_activation",
        "Activation requires explicit current independently reviewed empty-pool handoff facts");
}
