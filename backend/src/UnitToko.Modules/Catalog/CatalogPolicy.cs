using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

/// <summary>Pure transitions for explicit counted package facts. These rules neither authorize
/// a command nor reserve identifiers, establish image eligibility, or change stock.</summary>
public static class CatalogPolicy
{
    public static string NormalizeSkuCode(string code)
    {
        if (code is null || code.Any(char.IsControl)) throw Invalid("invalid_sku_code");
        var value = code.Trim();
        // Validate ASCII before case conversion so a Unicode lookalike cannot become a code.
        if (value.Length is < 1 or > 64 || value.Any(character =>
            !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw Invalid("invalid_sku_code");
        return value.ToUpperInvariant();
    }

    public static void Validate(Sku sku, VariantDefinitionVersion definition)
    {
        if (sku is null) throw Invalid();
        RequireId(sku.Id); RequireId(sku.OrganizationId); RequireId(sku.FamilyId);
        RequireId(sku.CurrentDefinitionId); RequirePublicId(sku.PublicId);
        if (sku.Revision <= 0 || NormalizeSkuCode(sku.Code) != sku.Code ||
            Text(sku.PhysicalItemKey, 128) != sku.PhysicalItemKey) throw Invalid();
        if (sku.PredecessorSkuId is { } predecessor)
        {
            RequireId(predecessor);
            if (predecessor == sku.Id) throw Invalid();
        }
        ValidateDefinition(definition);
        if (definition.SkuId != sku.Id || definition.Id != sku.CurrentDefinitionId) throw Invalid();
    }

    public static SkuDefinitionChange CreateSku(LongId skuId, LongId organizationId, LongId familyId,
        Guid publicId, string code, string physicalItemKey, LongId definitionId,
        DefinitionInput definition, DateTimeOffset effectiveAt)
    {
        RequireId(skuId); RequireId(organizationId); RequireId(familyId); RequireId(definitionId);
        RequirePublicId(publicId);
        var normalized = NormalizeDefinition(definition);
        var sku = new Sku(skuId, organizationId, familyId, publicId, NormalizeSkuCode(code),
            Text(physicalItemKey, 128), true, definitionId, 1);
        var version = Version(definitionId, skuId, 1, normalized, Utc(effectiveAt));
        return new(sku, version);
    }

    public static SkuDefinitionChange ReviseDefinition(Sku current, VariantDefinitionVersion currentDefinition,
        long expectedRevision, LongId definitionId, DefinitionInput definition, DateTimeOffset effectiveAt)
    {
        RequireCurrent(current, currentDefinition, expectedRevision);
        RequireNewDefinition(currentDefinition, definitionId);
        var normalized = NormalizeDefinition(definition);
        var at = NotBefore(effectiveAt, currentDefinition.EffectiveAt);
        if (normalized.PackageKind != currentDefinition.PackageKind ||
            normalized.BaseUnitCode != currentDefinition.BaseUnitCode ||
            normalized.BaseUnitsPerPackage != currentDefinition.BaseUnitsPerPackage ||
            !normalized.Attributes.SequenceEqual(currentDefinition.Attributes))
            throw Invalid("catalog_material_change");
        var nextSkuRevision = Next(current.Revision);
        var nextDefinitionVersion = Next(currentDefinition.Version);
        return new(current with { CurrentDefinitionId = definitionId, Revision = nextSkuRevision },
            Version(definitionId, current.Id, nextDefinitionVersion, normalized, at));
    }

    public static SkuDefinitionChange CreateReplacementSku(Sku predecessor, VariantDefinitionVersion predecessorDefinition,
        long expectedRevision, LongId skuId, Guid publicId, string code, string physicalItemKey,
        LongId definitionId, DefinitionInput definition, DateTimeOffset effectiveAt)
    {
        RequireCurrent(predecessor, predecessorDefinition, expectedRevision);
        RequireNewDefinition(predecessorDefinition, definitionId);
        var normalizedCode = NormalizeSkuCode(code);
        if (skuId == predecessor.Id || publicId == predecessor.PublicId || normalizedCode == predecessor.Code)
            throw Invalid("catalog_replacement_identity_reused");
        // Equivalent contents do not imply the same commercial identity. Replacement is explicit;
        // the original identity and its active/retired status remain unchanged.
        var result = CreateSku(skuId, predecessor.OrganizationId, predecessor.FamilyId, publicId,
            normalizedCode, physicalItemKey, definitionId, definition, NotBefore(effectiveAt, predecessorDefinition.EffectiveAt));
        return result with { Sku = result.Sku with { PredecessorSkuId = predecessor.Id } };
    }

    /// <summary>Count arithmetic only. MOQ/increment admissibility, availability and permission
    /// belong to Ordering; this calculation does not assert that an order can be submitted.</summary>
    public static long BaseUnitsForPackages(VariantDefinitionVersion definition, int packageCount)
    {
        ValidateDefinition(definition);
        return checked((long)definition.BaseUnitsPerPackage * DomainValues.ParseQuantity(packageCount));
    }

    private static DefinitionInput NormalizeDefinition(DefinitionInput input)
    {
        if (input is null || input.BaseUnitsPerPackage <= 0 || input.Attributes is null ||
            input.Attributes.Count > 32 || input.ImageAssetIds.IsDefault || input.ImageAssetIds.Length > 16)
            throw Invalid();
        var label = Text(input.PackageLabel, 160);
        var kind = Text(input.PackageKind, 16);
        if (kind is not ("loose" or "sealed" or "other")) throw Invalid();
        var baseUnit = Text(input.BaseUnitCode, 64);
        DomainValues.ParseQuantity(input.MinimumOrderPackages);
        DomainValues.ParseQuantity(input.OrderIncrementPackages);
        var attributes = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in input.Attributes)
        {
            var normalizedKey = Text(key, 64);
            if (attributes.ContainsKey(normalizedKey)) throw Invalid();
            attributes.Add(normalizedKey, Text(value, 256));
        }
        foreach (var id in input.ImageAssetIds) RequireId(id);
        if (input.ImageAssetIds.Distinct().Count() != input.ImageAssetIds.Length) throw Invalid();
        return new(label, kind, baseUnit, input.BaseUnitsPerPackage, attributes.ToImmutable(),
            input.ImageAssetIds, input.MinimumOrderPackages, input.OrderIncrementPackages);
    }

    private static void ValidateDefinition(VariantDefinitionVersion definition)
    {
        if (definition is null || definition.Version <= 0) throw Invalid();
        RequireId(definition.Id); RequireId(definition.SkuId);
        var normalized = NormalizeDefinition(new(definition.PackageLabel, definition.PackageKind, definition.BaseUnitCode,
            definition.BaseUnitsPerPackage, definition.Attributes, definition.ImageAssetIds,
            definition.MinimumOrderPackages, definition.OrderIncrementPackages));
        // A read model must already contain canonical text and ordinal immutable attributes.
        // Repairing a malformed current record is not an implicit side effect of a new revision.
        if (normalized.PackageLabel != definition.PackageLabel || normalized.PackageKind != definition.PackageKind ||
            normalized.BaseUnitCode != definition.BaseUnitCode ||
            !ReferenceEquals(definition.Attributes.KeyComparer, StringComparer.Ordinal) ||
            !normalized.Attributes.SequenceEqual(definition.Attributes) || definition.EffectiveAt.Offset != TimeSpan.Zero)
            throw Invalid();
        Utc(definition.EffectiveAt);
    }

    private static VariantDefinitionVersion Version(LongId id, LongId skuId, long version,
        DefinitionInput value, DateTimeOffset at) => new(id, skuId, version, value.PackageLabel, value.PackageKind,
            value.BaseUnitCode, value.BaseUnitsPerPackage, value.Attributes, value.ImageAssetIds,
            value.MinimumOrderPackages, value.OrderIncrementPackages, at);

    private static void RequireCurrent(Sku sku, VariantDefinitionVersion definition, long expectedRevision)
    {
        Validate(sku, definition);
        if (expectedRevision <= 0 || expectedRevision != sku.Revision) throw Invalid("catalog_revision_conflict");
    }

    private static void RequireNewDefinition(VariantDefinitionVersion current, LongId id)
    {
        RequireId(id);
        if (id == current.Id) throw Invalid("catalog_definition_identity_reused");
    }

    private static long Next(long version)
    {
        try { return checked(version + 1); }
        catch (OverflowException) { throw Invalid("catalog_revision_exhausted"); }
    }

    private static DateTimeOffset NotBefore(DateTimeOffset value, DateTimeOffset previous)
    {
        var utc = Utc(value);
        if (utc < previous) throw Invalid("catalog_definition_backdated");
        return utc;
    }

    private static DateTimeOffset Utc(DateTimeOffset value)
    {
        if (value == default) throw Invalid();
        return value.ToUniversalTime();
    }

    private static void RequireId(LongId id)
    {
        if (id.Value <= 0) throw Invalid("invalid_catalog_identity");
    }

    private static void RequirePublicId(Guid value)
    {
        var text = value.ToString("D");
        if (text[14] != '4' || text[19] is not ('8' or '9' or 'a' or 'b')) throw Invalid("invalid_catalog_public_id");
    }

    // These are bounded implementation inputs, not a unit registry or commercial policy.
    private static string Text(string value, int maximum)
    {
        if (value is null || value.Any(char.IsControl)) throw Invalid();
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) throw Invalid();
                index++;
            }
            else if (char.IsLowSurrogate(value[index])) throw Invalid();
        }
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maximum) throw Invalid();
        return trimmed;
    }

    private static DomainValidationException Invalid(string code = "invalid_catalog_definition") =>
        new(code, "Catalog identity or package definition is invalid");
}
