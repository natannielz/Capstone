using System.Numerics;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

public sealed record LotValueBalance(StockBalanceKey Key, long RemainingPackages, Money RemainingValue, long Revision);
public sealed record LotValueAllocation(LotValueBalance Remaining, Money TransferredValue);

/// <summary>Arithmetic for an explicitly selected lot and policy only. The transaction owner must independently
/// approve this policy, authenticate physical release/acquisition value, fence quantity and value together,
/// and persist the original command and consumption history. These records establish no stock authority.</summary>
public static class LotValuePolicy
{
    public const string ProportionalFloorPolicy = "remaining-proportional-minor-unit-floor-v1";

    /// <summary>Allocate the selected fraction of remaining value, rounding down to a minor unit.
    /// The residual stays in the source lot; consuming its final packages transfers the exact remaining value.</summary>
    public static LotValueAllocation TakeProportionalFloor(LotValueBalance value, long expectedRevision, long packages)
    {
        if (value is null || value.Key is null || value.Key.Location is null ||
            value.Key.SkuId.Value <= 0 || value.Key.DefinitionId.Value <= 0 || value.Key.LotId.Value <= 0 ||
            value.RemainingPackages < 0 || value.Revision <= 0 ||
            (value.RemainingPackages == 0 && value.RemainingValue.Value != 0))
            throw Invalid("invalid_lot_value_balance");
        value.Key.Location.Validate();
        Money.Validate(value.RemainingValue.Value, signed: false);
        if (packages <= 0) throw Invalid("invalid_quantity");
        if (expectedRevision <= 0 || expectedRevision != value.Revision) throw Invalid("lot_value_revision_conflict");
        if (value.Revision == long.MaxValue) throw Invalid("lot_value_revision_exhausted");
        if (packages > value.RemainingPackages) throw Invalid("insufficient_lot_value_packages");

        // BigInteger keeps the multiplication exact even for near-Int64 quantities and maximum Money.
        var minorUnits = new BigInteger(value.RemainingValue.Value * 100m);
        var transferredMinorUnits = minorUnits * packages / value.RemainingPackages;
        var transferred = new Money((decimal)transferredMinorUnits / 100m);
        var remaining = value with
        {
            RemainingPackages = value.RemainingPackages - packages,
            RemainingValue = new Money(value.RemainingValue.Value - transferred.Value),
            Revision = value.Revision + 1
        };
        return new(remaining, transferred);
    }

    private static DomainValidationException Invalid(string code) =>
        new(code, "Lot value requires valid exact quantities, money and revisions");
}
