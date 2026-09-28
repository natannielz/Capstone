using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

/// <summary>Settings changes preserve physical identity; this policy does not activate a stock source.</summary>
public static class StockSourceRevisionPolicy
{
    public static void Validate(StockSourceSetupInput before, StockSourceSetupInput after)
    {
        StockSourceSetupPolicy.Validate(before); StockSourceSetupPolicy.Validate(after);
        if (before.Location != after.Location || before.PhysicalKey != after.PhysicalKey)
            throw new DomainValidationException("stock_source_identity_immutable", "Source settings cannot move or rename physical stock identity");
        if (before == after)
            throw new DomainValidationException("stock_source_revision_unchanged", "A source revision must change explicit settings");
    }
}
