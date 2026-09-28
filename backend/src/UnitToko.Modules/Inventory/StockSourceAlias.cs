using UnitToko.Modules.Contracts;
namespace UnitToko.Modules.Inventory;

/// <summary>An alias changes the requesting channel, never physical ownership or stock location.</summary>
public static class StockSourceAliasPolicy
{
    public static void Validate(StockLocation owner, StockLocation requested)
    {
        if (owner is null || requested is null) throw Invalid();
        owner.Validate(); requested.Validate();
        if (owner.OrganizationId != requested.OrganizationId || owner.ShopId != requested.ShopId ||
            owner.WarehouseId != requested.WarehouseId || owner.ChannelId == requested.ChannelId) throw Invalid();
    }
    private static DomainValidationException Invalid() => new("invalid_stock_source_alias", "A stock alias must name a different channel at the same physical warehouse");
}
