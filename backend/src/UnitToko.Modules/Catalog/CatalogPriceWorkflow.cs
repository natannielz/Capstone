namespace UnitToko.Modules.Catalog;

public enum CatalogPriceAccess { Prepare = 1, Review }
public enum CatalogPriceDecision { Approve = 1, Reject, Withdraw }
public sealed record CatalogPricePreview(Sku Sku, VariantDefinitionVersion Definition,
    CatalogPriceExpectation Expected, PriceVersion? Current);
public sealed record CatalogPriceChoice(Sku Sku, string FamilyName, VariantDefinitionVersion Definition);
