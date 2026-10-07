using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Inventory;

/// <summary>Explicit operator intent only. The owner loads the approved rule,
/// actual source lot/value and destination identity; this input grants no stock authority.</summary>
public sealed record PackConversionInput(GrantScope Location, long RuleId, long RuleVersion, long SourceBalanceId, int Steps);
public sealed record PackConversionExpectation(long SourceBalanceRevision, long PoolRevision, long PoolFence, long LotValueRevision);

public static class PackConversionExecutionPolicy
{
    public static PackConversionInput Copy(PackConversionInput input) {
        if (input is null) throw Invalid();
        StockCountPolicy.Location(input.Location);
        if (new[] { input.RuleId, input.RuleVersion, input.SourceBalanceId }.Any(id => id is <= 0 or >= long.MaxValue - 2)) throw Invalid();
        _ = DomainValues.ParseQuantity(input.Steps);
        return input with { Location = input.Location with { } };
    }
    public static PackConversionExpectation Copy(PackConversionExpectation value) {
        if (value is null || new[] { value.SourceBalanceRevision, value.PoolRevision, value.LotValueRevision }
            .Any(revision => revision is <= 0 or >= long.MaxValue - 2) || value.PoolFence is < 0 or >= long.MaxValue - 2) throw Invalid();
        return value with { };
    }
    public static DomainValidationException Invalid() => new("invalid_pack_conversion", "Choose the exact approved rule, source lot and bounded conversion steps");
}
