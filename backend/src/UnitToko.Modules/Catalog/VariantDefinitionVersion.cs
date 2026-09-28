using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

/// <summary>Immutable counted package facts. Image references do not establish document eligibility.</summary>
public sealed record VariantDefinitionVersion(LongId Id, LongId SkuId, long Version,
    string PackageLabel, string PackageKind, string BaseUnitCode,
    int BaseUnitsPerPackage, ImmutableSortedDictionary<string, string> Attributes,
    ImmutableArray<LongId> ImageAssetIds, int MinimumOrderPackages,
    int OrderIncrementPackages, DateTimeOffset EffectiveAt);
