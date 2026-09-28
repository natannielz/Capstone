using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

public sealed record DefinitionInput(string PackageLabel, string PackageKind,
    string BaseUnitCode, int BaseUnitsPerPackage,
    ImmutableSortedDictionary<string, string> Attributes,
    ImmutableArray<LongId> ImageAssetIds, int MinimumOrderPackages,
    int OrderIncrementPackages);

/// <summary>Pure proposed state, not a persisted command receipt or stock operation.</summary>
public sealed record SkuDefinitionChange(Sku Sku, VariantDefinitionVersion Definition);
