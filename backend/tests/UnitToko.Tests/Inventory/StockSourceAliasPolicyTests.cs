using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
namespace UnitToko.Tests.Inventory;

public sealed class StockSourceAliasPolicyTests
{
    private static readonly StockLocation Owner = new(new(100), new(101), new(102), new(103));
    [Fact]
    public void Different_channel_at_same_physical_warehouse_is_an_alias()
        => StockSourceAliasPolicy.Validate(Owner, Owner with { ChannelId = new(104) });
    [Theory]
    [InlineData("same")] [InlineData("organization")] [InlineData("shop")] [InlineData("warehouse")] [InlineData("missing")]
    public void Alias_is_not_a_transfer_or_an_identity_rewrite(string fault)
    {
        var requested = fault switch
        {
            "same" => Owner,
            "organization" => Owner with { OrganizationId = new(200), ChannelId = new(104) },
            "shop" => Owner with { ShopId = new(201), ChannelId = new(104) },
            "warehouse" => Owner with { WarehouseId = new(202), ChannelId = new(104) },
            _ => null!
        };
        Assert.Throws<DomainValidationException>(() => StockSourceAliasPolicy.Validate(Owner, requested));
    }
}
