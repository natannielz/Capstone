using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

/// <summary>Validation limits for staff authoring. These values grant no scope, price or asset eligibility.</summary>
public static class CatalogAuthoringPolicy
{
    public static GrantScope Owner(GrantScope value)
    {
        if (value is null || value.Kind is not (GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse) ||
            value.OrganizationId is not > 0 || value.ShopId is not > 0 || value.ChannelId is not > 0 ||
            value.BranchId is not null || value.DivisionId is not null ||
            value.Kind == GrantScopeKind.FulfillmentShop && value.WarehouseId is not null ||
            value.Kind == GrantScopeKind.FulfillmentWarehouse && value.WarehouseId is not > 0)
            throw Invalid("invalid_catalog_owner");
        return value;
    }
    public static string Name(string value) => Text(value, 160);
    public static string Description(string value)
    {
        if (value is null) throw Invalid();
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        RequireUnicode(value);
        if (value.Any(character => char.IsControl(character) && character is not ('\n' or '\t'))) throw Invalid();
        value = value.Trim();
        if (value.Length > 4000) throw Invalid();
        return value;
    }
    public static string Text(string value, int maximum)
    {
        if (value is null || value.Any(char.IsControl)) throw Invalid();
        RequireUnicode(value);
        value = value.Trim();
        if (value.Length is 0 || value.Length > maximum) throw Invalid();
        return value;
    }
    public static void Images(ImmutableArray<LongId> values)
    {
        if (values.IsDefault || !values.IsEmpty) throw Invalid("catalog_image_unavailable");
    }
    private static void RequireUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) throw Invalid();
                index++;
            }
            else if (char.IsLowSurrogate(value[index])) throw Invalid();
        }
    }
    private static DomainValidationException Invalid(string code = "invalid_catalog_request") =>
        new(code, "Catalog input is invalid or unavailable");
}
