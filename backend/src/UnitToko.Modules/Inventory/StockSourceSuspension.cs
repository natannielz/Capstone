using UnitToko.Modules.Contracts;
namespace UnitToko.Modules.Inventory;

/// <summary>A frozen review of source configuration, not drain, handoff or activation evidence.</summary>
public sealed record StockSourceSuspensionExpectation(long PoolRevision, long SuspensionRevision, string ConfigurationHash)
{
    public void Validate()
    {
        if (PoolRevision <= 0 || SuspensionRevision < 0 || SuspensionRevision == long.MaxValue ||
            ConfigurationHash is not { Length: 64 } || !ConfigurationHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new DomainValidationException("invalid_stock_source_suspension", "Suspension requires the exact reviewed source configuration");
    }
}
