using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Inventory;

/// <summary>Explicit reviewed source tuple. Never inferred from a warehouse or a typed lot label.</summary>
public sealed record StockCountPoolSelection(long PoolId, long ExpectedPoolRevision, string ConfigurationHash)
{
    public void Validate() {
        if (PoolId is <= 0 or >= long.MaxValue - 1 || ExpectedPoolRevision is <= 0 or >= long.MaxValue - 1 ||
            ConfigurationHash is null || ConfigurationHash.Length != 64 || ConfigurationHash.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new DomainValidationException("invalid_stock_count", "Select the exact reviewed stock source and current configuration");
    }
}

/// <summary>Immutable source metadata at observation time. Active is descriptive,
/// not acceptance of a writer, stock availability, reservation, adjustment or release.</summary>
public sealed record StockCountPoolBinding(long PoolId, long PoolRevision, long SetupDecisionId,
    string ConfigurationHash, string PhysicalKey, string Label, bool Active)
{
    [JsonIgnore] public StockCountPoolSelection Selection => new(PoolId, PoolRevision, ConfigurationHash);
    public void Validate() {
        Selection.Validate();
        if (SetupDecisionId is <= 0 or >= long.MaxValue - 1 || !StockSourceSetupPolicy.Key(PhysicalKey) || !StockCountPolicy.CanonicalText(Label, 160))
            throw new DomainValidationException("invalid_stock_count", "The explicit physical source identity is unavailable");
    }
}

public sealed record StockCountSourceChoice(StockCountPoolBinding Pool, GrantScope RequestedLocation);
public sealed record StockCountSourcePage(long Total, ImmutableArray<StockCountSourceChoice> Items);
