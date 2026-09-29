using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Ordering;

public sealed record OrderBuyingContext(LongId OrganizationId, LongId BranchId, LongId? DivisionId = null)
{
    public GrantScope Scope => new(GrantScopeKind.BuyerOwned, OrganizationId.Value, BranchId.Value, DivisionId?.Value);
    public ResourceScope Resource(long actorId) => new(BuyingOrganizationId: OrganizationId.Value,
        BranchId: BranchId.Value, DivisionId: DivisionId?.Value, CreatedBy: actorId);
    public void Validate()
    {
        if (OrganizationId.Value <= 0 || BranchId.Value <= 0 || DivisionId is { Value: <= 0 })
            throw new DomainValidationException("invalid_order_context", "Choose an approved branch");
    }
}
public sealed record OrderBuyingOption(OrderBuyingContext Context, string? OrganizationName, string? BranchName, string? DivisionName);
public sealed record OrderQuotePreview(OrderBuyingContext Context, DateTimeOffset CalculatedAt, OrderCatalogQuote Catalog);

/// <summary>The buyer explicitly selects one exact package definition and approved price.
/// Construction supplies no entitlement, ordering permission, stock promise or tax policy.</summary>
public sealed record OrderPackageSelection(LongId SkuId, LongId DefinitionId, long DefinitionVersion,
    LongId PriceId, long PriceVersion, int Packages);
public sealed record OrderCatalogFact(ProductFamily Family, Sku Sku, VariantDefinitionVersion Definition, PriceVersion? Price);
public sealed record OrderQuotedLine(LongId FamilyId, string FamilyName, Guid PublicId, string SkuCode,
    GrantScope FulfillmentOwner, VariantDefinitionVersion Definition, PriceVersion Price, int Packages, long BaseUnits, Money CatalogSubtotal);
public sealed record OrderCatalogQuote(ImmutableArray<OrderQuotedLine> Lines, string Currency, Money CatalogSubtotal);

/// <summary>Exact catalog-price arithmetic, not a payable/tax-inclusive total. The owning transaction
/// must load and fence real catalog, price, buyer/branch, commercial policy and stock authority.</summary>
public static class OrderQuotePolicy
{
    public static OrderCatalogQuote Create(ImmutableArray<OrderPackageSelection> selections,
        ImmutableArray<OrderCatalogFact> facts)
    {
        if (selections.IsDefaultOrEmpty || selections.Length > 100 || facts.IsDefaultOrEmpty || facts.Length != selections.Length ||
            selections.Any(value => value is null || value.SkuId.Value <= 0 || value.DefinitionId.Value <= 0 || value.DefinitionVersion <= 0 ||
                value.PriceId.Value <= 0 || value.PriceVersion <= 0) ||
            facts.Any(value => value is null || value.Sku is null || value.Definition is null || value.Family is null) ||
            selections.Select(value => value.SkuId).Distinct().Count() != selections.Length ||
            facts.Select(value => value.Sku.Id).Distinct().Count() != facts.Length) throw Invalid();
        var byId = facts.ToDictionary(value => value.Sku.Id);
        var lines = ImmutableArray.CreateBuilder<OrderQuotedLine>(selections.Length);
        var total = new Money(0m); string? currency = null;
        foreach (var selected in selections)
        {
            if (!byId.TryGetValue(selected.SkuId, out var fact) || fact.Price is null) throw Invalid("order_package_unavailable");
            CatalogPolicy.Validate(fact.Sku, fact.Definition); CatalogPricePolicy.Validate(fact.Price);
            CatalogAuthoringPolicy.Owner(fact.Family.AuthoringOwner);
            if (!fact.Sku.Active || !fact.Family.Active || fact.Sku.FamilyId != fact.Family.Id ||
                fact.Sku.OrganizationId != fact.Family.OrganizationId || fact.Family.AuthoringOwner.OrganizationId != fact.Family.OrganizationId.Value ||
                fact.Price.SkuId != fact.Sku.Id || CatalogAuthoringPolicy.Text(fact.Family.Name, 160) != fact.Family.Name) throw Invalid("order_package_unavailable");
            if (selected.DefinitionId != fact.Definition.Id || selected.DefinitionVersion != fact.Definition.Version ||
                selected.PriceId != fact.Price.Id || selected.PriceVersion != fact.Price.Version) throw Invalid("order_selection_changed");
            var quantity = DomainValues.ParseQuantity(selected.Packages);
            if (quantity < fact.Definition.MinimumOrderPackages || quantity % fact.Definition.OrderIncrementPackages != 0)
                throw Invalid("order_package_quantity_invalid");
            currency ??= fact.Price.Currency;
            if (currency != fact.Price.Currency) throw Invalid("order_currency_mismatch");
            var amount = Money.Multiply(fact.Price.UnitPrice, quantity); total = Money.Add(total, amount);
            var definition = fact.Definition with {
                Attributes = fact.Definition.Attributes.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                ImageAssetIds = fact.Definition.ImageAssetIds.ToArray().ToImmutableArray()
            };
            lines.Add(new(fact.Family.Id, fact.Family.Name, fact.Sku.PublicId, fact.Sku.Code, fact.Family.AuthoringOwner,
                definition, fact.Price, quantity, checked((long)quantity * definition.BaseUnitsPerPackage), amount));
        }
        return new(lines.ToImmutable(), currency!, total);
    }
    private static DomainValidationException Invalid(string code = "invalid_order_quote") =>
        new(code, "Select current packages and approved prices with valid quantities");
}
