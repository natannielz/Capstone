using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

/// <summary>An immutable price for one package of one SKU. This fact alone grants no buyer
/// entitlement and does not establish tax, HET, discount or currency-conversion policy.</summary>
public sealed record PriceVersion(LongId Id, LongId SkuId, long Version,
    string Currency, Money UnitPrice, DateTimeOffset EffectiveAt);

public sealed record CatalogPriceInput(string Currency, Money UnitPrice);
public sealed record CatalogPriceExpectation(long SkuRevision, LongId DefinitionId,
    long DefinitionVersion, long PriceVersion);

/// <summary>Pure price transitions. The caller must authorize independent approval, load
/// and fence the actual catalog and price heads, and persist the resulting history atomically.</summary>
public static class CatalogPricePolicy
{
    public static void Validate(CatalogPriceInput input)
    {
        if (input is null || input.Currency is null || input.Currency.Length != 3 ||
            input.Currency.Any(character => character is < 'A' or > 'Z'))
            throw Invalid();
        Money.Validate(input.UnitPrice.Value, signed: false);
    }

    public static void Validate(PriceVersion price)
    {
        if (price is null || price.Id.Value <= 0 || price.SkuId.Value <= 0 || price.Version <= 0)
            throw Invalid();
        Validate(new CatalogPriceInput(price.Currency, price.UnitPrice));
        RequireTime(price.EffectiveAt);
    }

    public static PriceVersion Approve(Sku sku, VariantDefinitionVersion definition,
        PriceVersion? current, CatalogPriceExpectation expected, CatalogPriceInput input,
        LongId id, DateTimeOffset now)
    {
        CatalogPolicy.Validate(sku, definition);
        Validate(input);
        RequireTime(now);
        if (!sku.Active || id.Value <= 0 || expected is null || expected.SkuRevision <= 0 ||
            expected.DefinitionId.Value <= 0 || expected.DefinitionVersion <= 0 || expected.PriceVersion < 0)
            throw Invalid();
        if (current is not null)
        {
            Validate(current);
            if (current.SkuId != sku.Id || current.Id == id) throw Invalid();
        }
        if (expected.SkuRevision != sku.Revision || expected.DefinitionId != definition.Id ||
            expected.DefinitionVersion != definition.Version || expected.PriceVersion != (current?.Version ?? 0))
            throw Invalid("catalog_price_changed");
        if (now < definition.EffectiveAt || (current is not null && now < current.EffectiveAt))
            throw Invalid("catalog_price_backdated");
        if (current is not null && current.Currency == input.Currency && current.UnitPrice == input.UnitPrice)
            throw Invalid("catalog_price_unchanged");
        long revision;
        try { revision = checked((current?.Version ?? 0) + 1); }
        catch (OverflowException) { throw Invalid("catalog_price_revision_exhausted"); }
        return new(id, sku.Id, revision, input.Currency, input.UnitPrice, now);
    }

    private static void RequireTime(DateTimeOffset value)
    {
        // MongoDB dates preserve milliseconds; accepting finer precision would silently
        // change an immutable approved fact when it is read back from storage.
        if (value == default || value.Offset != TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw Invalid();
    }

    private static DomainValidationException Invalid(string code = "invalid_catalog_price") =>
        new(code, "Catalog price or reviewed catalog facts are invalid");
}
