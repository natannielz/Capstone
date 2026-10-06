using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class LotValuePolicyTests
{
    [Fact]
    public void Selected_policy_conserves_minor_units_and_residual_without_overflow_or_stale_consumption()
    {
        var key = new StockBalanceKey(new(new(1), new(2), new(3), new(4)), new(5), new(6), new(7));
        var original = new LotValueBalance(key, 3, new Money(10m), 1);
        var first = LotValuePolicy.TakeProportionalFloor(original, 1, 1);
        var second = LotValuePolicy.TakeProportionalFloor(first.Remaining, 2, 1);
        var final = LotValuePolicy.TakeProportionalFloor(second.Remaining, 3, 1);
        Assert.Equal(new[] { 3.33m, 3.33m, 3.34m }, new[] { first.TransferredValue.Value, second.TransferredValue.Value, final.TransferredValue.Value });
        Assert.Equal(10m, first.TransferredValue.Value + second.TransferredValue.Value + final.TransferredValue.Value);
        Assert.Equal(0, final.Remaining.RemainingPackages);
        Assert.Equal(0m, final.Remaining.RemainingValue.Value);
        Assert.Equal(4, final.Remaining.Revision);
        Assert.Equal(new LotValueBalance(key, 3, new Money(10m), 1), original);
        Assert.Same(key, final.Remaining.Key);

        var largest = new LotValueBalance(key, long.MaxValue, new Money(DomainValues.MaximumMoney), 1);
        var almostAll = LotValuePolicy.TakeProportionalFloor(largest, 1, long.MaxValue - 1);
        Assert.Equal(DomainValues.MaximumMoney - 0.01m, almostAll.TransferredValue.Value);
        Assert.Equal(1, almostAll.Remaining.RemainingPackages);
        Assert.Equal(0.01m, almostAll.Remaining.RemainingValue.Value);
        Assert.Equal(0.01m, LotValuePolicy.TakeProportionalFloor(almostAll.Remaining, 2, 1).TransferredValue.Value);
        Assert.Equal(0m, LotValuePolicy.TakeProportionalFloor(original with { RemainingValue = new Money(0) }, 1, 3).TransferredValue.Value);

        foreach (var quantity in new[] { 0L, -1L, long.MinValue, 4L })
            Assert.Throws<DomainValidationException>(() => LotValuePolicy.TakeProportionalFloor(original, 1, quantity));
        Assert.Equal("lot_value_revision_conflict", Assert.Throws<DomainValidationException>(() =>
            LotValuePolicy.TakeProportionalFloor(first.Remaining, 1, 1)).Code);
        Assert.Equal("lot_value_revision_exhausted", Assert.Throws<DomainValidationException>(() =>
            LotValuePolicy.TakeProportionalFloor(original with { Revision = long.MaxValue }, long.MaxValue, 1)).Code);
        foreach (var invalid in new[] { original with { RemainingPackages = -1 }, original with { RemainingPackages = 0 },
            original with { Revision = 0 }, original with { Key = key with { LotId = default } },
            original with { Key = key with { Location = key.Location with { WarehouseId = default } } } })
            Assert.Throws<DomainValidationException>(() => LotValuePolicy.TakeProportionalFloor(invalid, invalid.Revision, 1));
        Assert.Throws<DomainValidationException>(() => LotValuePolicy.TakeProportionalFloor(final.Remaining, 4, 1));
    }
}
