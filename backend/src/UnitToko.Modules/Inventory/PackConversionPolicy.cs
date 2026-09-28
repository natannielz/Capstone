using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

public sealed record PackConversionDefinition(LongId Id, long Version, LongId SourceSkuId, LongId SourceDefinitionId,
    LongId DestinationSkuId, LongId DestinationDefinitionId, int SourcePackagesPerStep, int DestinationPackagesPerStep);
public sealed record StockPackageDefinition(Sku Sku, VariantDefinitionVersion CurrentDefinition, VariantDefinitionVersion StoredDefinition);
public sealed record ConversionQuantities(long SourcePackages, long DestinationPackages, long BaseUnits);

/// <summary>Exact quantities for one explicitly directed definition. Construction/calculation proves no approval,
/// persisted provenance, sealed-package condition, cost allocation, scope or permission.</summary>
public static class PackConversionPolicy
{
    public static ConversionQuantities Quantities(PackConversionDefinition rule, StockPackageDefinition source,
        StockPackageDefinition destination, int steps)
    {
        _ = DomainValues.ParseQuantity(steps);
        RequirePackage(source);
        RequirePackage(destination);
        if (source.Sku.OrganizationId != destination.Sku.OrganizationId ||
            source.Sku.PhysicalItemKey != destination.Sku.PhysicalItemKey)
            throw Invalid("stock_conversion_incompatible");
        return Calculate(rule, source.StoredDefinition, destination.StoredDefinition, steps);
    }

    private static void RequirePackage(StockPackageDefinition package)
    {
        if (package is null) throw Invalid("invalid_stock_definition");
        CatalogPolicy.Validate(package.Sku, package.CurrentDefinition);
        _ = CatalogPolicy.BaseUnitsForPackages(package.StoredDefinition, 1);
        var current = package.CurrentDefinition;
        var stored = package.StoredDefinition;
        if (stored.SkuId != package.Sku.Id || stored.EffectiveAt > current.EffectiveAt)
            throw Invalid("stock_definition_identity_mismatch");
        if (stored.Id == current.Id)
        {
            if (stored.Version != current.Version || stored.PackageLabel != current.PackageLabel ||
                stored.PackageKind != current.PackageKind || stored.BaseUnitCode != current.BaseUnitCode ||
                stored.BaseUnitsPerPackage != current.BaseUnitsPerPackage || !stored.Attributes.SequenceEqual(current.Attributes) ||
                !stored.ImageAssetIds.SequenceEqual(current.ImageAssetIds) || stored.MinimumOrderPackages != current.MinimumOrderPackages ||
                stored.OrderIncrementPackages != current.OrderIncrementPackages || stored.EffectiveAt != current.EffectiveAt)
                throw Invalid("stock_definition_identity_mismatch");
        }
        else if (stored.Version >= current.Version) throw Invalid("stock_definition_identity_mismatch");
        if (stored.PackageKind != current.PackageKind || stored.BaseUnitCode != current.BaseUnitCode ||
            stored.BaseUnitsPerPackage != current.BaseUnitsPerPackage || !stored.Attributes.SequenceEqual(current.Attributes))
            throw Invalid("stock_definition_material_mismatch");
    }

    private static ConversionQuantities Calculate(PackConversionDefinition rule, VariantDefinitionVersion source,
        VariantDefinitionVersion destination, int steps)
    {
        _ = DomainValues.ParseQuantity(steps);
        if (rule is null || rule.Id.Value <= 0 || rule.Version <= 0 || rule.SourceSkuId.Value <= 0 ||
            rule.SourceDefinitionId.Value <= 0 || rule.DestinationSkuId.Value <= 0 || rule.DestinationDefinitionId.Value <= 0 ||
            rule.SourceSkuId == rule.DestinationSkuId || rule.SourceDefinitionId == rule.DestinationDefinitionId ||
            rule.SourcePackagesPerStep <= 0 || rule.DestinationPackagesPerStep <= 0)
            throw Invalid("invalid_stock_conversion");
        // Reuse Catalog's complete immutable-definition validation, including canonical units and attributes.
        _ = CatalogPolicy.BaseUnitsForPackages(source, 1);
        _ = CatalogPolicy.BaseUnitsForPackages(destination, 1);
        if (source.Id != rule.SourceDefinitionId || source.SkuId != rule.SourceSkuId ||
            destination.Id != rule.DestinationDefinitionId || destination.SkuId != rule.DestinationSkuId)
            throw Invalid("stock_conversion_definition_mismatch");
        if (source.BaseUnitCode != destination.BaseUnitCode || !source.Attributes.SequenceEqual(destination.Attributes))
            throw Invalid("stock_conversion_incompatible");
        try
        {
            var sourcePackages = checked((long)steps * rule.SourcePackagesPerStep);
            var destinationPackages = checked((long)steps * rule.DestinationPackagesPerStep);
            var sourceUnits = checked(sourcePackages * source.BaseUnitsPerPackage);
            var destinationUnits = checked(destinationPackages * destination.BaseUnitsPerPackage);
            if (sourceUnits != destinationUnits) throw Invalid("stock_conversion_not_conserved");
            return new(sourcePackages, destinationPackages, sourceUnits);
        }
        catch (OverflowException) { throw Invalid("stock_quantity_overflow"); }
    }
    private static DomainValidationException Invalid(string code) => new(code, "Conversion requires matching definitions and exact conserved quantities");
}
