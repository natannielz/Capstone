using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

/// <summary>A named human observation of physical packages. It is not an external feed acknowledgment.</summary>
public sealed record StockSourceObservedBalance(long BalanceId, long Packages);

public sealed record StockSourceReconciliationObservation(DateTimeOffset ObservedAt, string SourceReference,
    ImmutableArray<StockSourceObservedBalance> Entries);

public sealed record StockSourceReconciliationInput(long PoolId, long PoolRevision, long PoolFence,
    long PolicyId, long PolicyRevision, string ConfigurationHash, StockSourceReconciliationObservation Observation);

public enum StockSourceReconciliationDecision { Confirm, Reject }

public static class StockSourceReconciliationRules
{
    public static void Validate(StockSourceReconciliationInput input)
    {
        if (input is null || input.PoolId <= 0 || input.PoolRevision <= 0 || input.PoolFence < 0 ||
            input.PolicyId <= 0 || input.PolicyRevision <= 0 || !Hash(input.ConfigurationHash)) throw Invalid();
        Validate(input.Observation);
    }

    public static void Validate(StockSourceReconciliationObservation observation)
    {
        if (observation is null || observation.ObservedAt.Offset != TimeSpan.Zero ||
            observation.ObservedAt == default || !StockCountPolicy.CanonicalText(observation.SourceReference, 160) ||
            observation.Entries.IsDefault || observation.Entries.Length > 1000 ||
            observation.Entries.Any(entry => entry is null || entry.BalanceId <= 0 || entry.Packages < 0) ||
            observation.Entries.Select(entry => entry.BalanceId).Distinct().Count() != observation.Entries.Length)
            throw Invalid();
    }

    private static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static DomainValidationException Invalid() => new("invalid_stock_source_reconciliation",
        "A named complete physical observation and exact reviewed source binding are required");
}
