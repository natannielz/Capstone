using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Inventory;

public sealed class StockCountPolicyTests
{
    private static readonly GrantScope Warehouse = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    [Theory]
    [InlineData(0, 12, 0L)] [InlineData(2, 12, 24L)] [InlineData(1000000, 2147483647, 2147483647000000L)]
    public void Observed_zero_and_large_base_equivalent_are_exact(int packages, int contents, long expected) =>
        Assert.Equal(expected, StockCountPolicy.BaseUnits(packages, contents));
    [Theory]
    [InlineData(-1, 12)] [InlineData(1000001, 12)] [InlineData(1, 0)] [InlineData(1, -1)]
    public void Invalid_package_or_content_count_is_not_accepted(int packages, int contents) =>
        Assert.Throws<DomainValidationException>(() => StockCountPolicy.BaseUnits(packages, contents));
    [Fact]
    public void A_count_requires_exact_warehouse_and_positive_catalog_preconditions()
    {
        var input = new StockCountInput(Warehouse, 1, 1, 2, 0);
        StockCountPolicy.Validate(input);
        foreach (var bad in new[] { input with { Location = Warehouse with { WarehouseId = null } },
            input with { Location = Warehouse with { Kind = GrantScopeKind.FulfillmentShop } },
            input with { ExpectedDefinitionId = 0 }, input with { ExpectedSkuRevision = 0 }, input with { SkuId = 0 },
            input with { ObservedLotLabel = " untrimmed" }, input with { ObservedLotLabel = "bad\nlabel" } })
            Assert.Throws<DomainValidationException>(() => StockCountPolicy.Validate(bad));
    }
}
