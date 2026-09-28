using System.Collections.Immutable;
using System.Text.Json;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Catalog;

public sealed class CatalogPolicyTests
{
    private static readonly DateTimeOffset EffectiveAt = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PublicId = Guid.Parse("12345678-1234-4123-8123-123456789abc");
    private static readonly Guid ReplacementPublicId = Guid.Parse("12345678-1234-4123-8123-123456789abd");

    [Fact]
    public void Same_package_label_does_not_merge_distinct_explicit_families_or_contents()
    {
        var first = Create();
        var second = CatalogPolicy.CreateSku(new(101), new(10), new(21), ReplacementPublicId,
            "WATER-24", "WATER-ITEM", new(1001), Input() with { BaseUnitsPerPackage = 24 }, EffectiveAt);
        Assert.Equal("Dus", first.Definition.PackageLabel);
        Assert.Equal("Dus", second.Definition.PackageLabel);
        Assert.NotEqual(first.Sku.Id, second.Sku.Id);
        Assert.NotEqual(first.Sku.FamilyId, second.Sku.FamilyId);
        Assert.Equal(12, first.Definition.BaseUnitsPerPackage);
        Assert.Equal(24, second.Definition.BaseUnitsPerPackage);
        Assert.Equal(24L, CatalogPolicy.BaseUnitsForPackages(first.Definition, 2));
        Assert.Equal(48L, CatalogPolicy.BaseUnitsForPackages(second.Definition, 2));
    }

    [Fact]
    public void Creation_has_one_explicit_identity_and_initial_definition_without_inferred_predecessor()
    {
        var created = Create();
        Assert.Equal("PEN-12", created.Sku.Code);
        Assert.Equal(100L, created.Sku.Id.Value);
        Assert.Equal(10L, created.Sku.OrganizationId.Value);
        Assert.Equal(20L, created.Sku.FamilyId.Value);
        Assert.Equal(1000L, created.Sku.CurrentDefinitionId.Value);
        Assert.Equal(created.Sku.Id, created.Definition.SkuId);
        Assert.Equal(1L, created.Sku.Revision);
        Assert.Equal(1L, created.Definition.Version);
        Assert.True(created.Sku.Active);
        Assert.Null(created.Sku.PredecessorSkuId);
        CatalogPolicy.Validate(created.Sku, created.Definition);
    }

    [Fact]
    public void Cosmetic_and_order_rule_revisions_leave_the_prior_definition_and_sku_unchanged()
    {
        var created = Create();
        var revised = CatalogPolicy.ReviseDefinition(created.Sku, created.Definition, 1, new(1001),
            Input() with { PackageLabel = "Karton dua belas", MinimumOrderPackages = 3, OrderIncrementPackages = 2 }, EffectiveAt);
        Assert.Equal(created.Sku.Id, revised.Sku.Id);
        Assert.Equal(PublicId, revised.Sku.PublicId);
        Assert.Equal("PEN-12", revised.Sku.Code);
        Assert.Equal(2L, revised.Sku.Revision);
        Assert.Equal(2L, revised.Definition.Version);
        Assert.Equal(1001L, revised.Sku.CurrentDefinitionId.Value);
        Assert.Equal("Karton dua belas", revised.Definition.PackageLabel);
        Assert.Equal(3, revised.Definition.MinimumOrderPackages);
        Assert.Equal(2, revised.Definition.OrderIncrementPackages);
        Assert.Equal(EffectiveAt, revised.Definition.EffectiveAt);
        Assert.Equal("Dus", created.Definition.PackageLabel);
        Assert.Equal(1L, created.Sku.Revision);
        Assert.Equal(1L, created.Definition.Version);
        Assert.Equal(1000L, created.Sku.CurrentDefinitionId.Value);
    }

    [Theory]
    [InlineData("contents")]
    [InlineData("base-unit")]
    [InlineData("package-kind")]
    [InlineData("attribute-value")]
    [InlineData("attribute-key")]
    [InlineData("attribute-addition")]
    public void Material_package_changes_cannot_reinterpret_an_existing_sellable_identity(string change)
    {
        var created = Create();
        var input = change switch
        {
            "contents" => Input() with { BaseUnitsPerPackage = 10 },
            "base-unit" => Input() with { BaseUnitCode = "BOX" },
            "package-kind" => Input() with { PackageKind = "loose" },
            "attribute-value" => Input() with { Attributes = Attributes(("color", "BLACK")) },
            "attribute-key" => Input() with { Attributes = Attributes(("Color", "blue")) },
            _ => Input() with { Attributes = Attributes(("color", "blue"), ("size", "M")) }
        };
        var exception = Assert.Throws<DomainValidationException>(() =>
            CatalogPolicy.ReviseDefinition(created.Sku, created.Definition, 1, new(1001), input, EffectiveAt));
        Assert.Equal("catalog_material_change", exception.Code);
        Assert.Equal(12, created.Definition.BaseUnitsPerPackage);
        Assert.Equal("blue", created.Definition.Attributes["color"]);
    }

    [Fact]
    public void Replacement_carries_predecessor_and_preserves_owner_family_and_original_stock_meaning()
    {
        var old = Create();
        var replacement = CatalogPolicy.CreateReplacementSku(old.Sku, old.Definition, 1,
            new(101), ReplacementPublicId, "PEN-10", "PEN-ITEM", new(1001),
            Input() with { BaseUnitsPerPackage = 10 }, EffectiveAt.AddMinutes(1));
        Assert.Equal(101L, replacement.Sku.Id.Value);
        Assert.Equal(ReplacementPublicId, replacement.Sku.PublicId);
        Assert.Equal(old.Sku.Id, replacement.Sku.PredecessorSkuId);
        Assert.Equal(old.Sku.OrganizationId, replacement.Sku.OrganizationId);
        Assert.Equal(old.Sku.FamilyId, replacement.Sku.FamilyId);
        Assert.Equal(1L, replacement.Sku.Revision);
        Assert.Equal(1L, replacement.Definition.Version);
        Assert.Equal(10, replacement.Definition.BaseUnitsPerPackage);
        Assert.True(old.Sku.Active);
        Assert.Equal(12, old.Definition.BaseUnitsPerPackage);
        Assert.Equal(1L, old.Sku.Revision);
    }

    [Theory]
    [InlineData("sku-id")]
    [InlineData("uuid")]
    [InlineData("code")]
    [InlineData("definition-id")]
    public void Replacement_cannot_reuse_a_predecessor_identity_or_definition(string reused)
    {
        var old = Create();
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateReplacementSku(old.Sku, old.Definition, 1,
            reused == "sku-id" ? old.Sku.Id : new(101), reused == "uuid" ? PublicId : ReplacementPublicId,
            reused == "code" ? "  pen-12  " : "PEN-10", "PEN-ITEM",
            reused == "definition-id" ? old.Definition.Id : new(1001), Input() with { BaseUnitsPerPackage = 10 }, EffectiveAt));
    }

    [Fact]
    public void Intrinsic_attribute_comparison_is_ordinal_and_never_uses_the_input_dictionary_comparer()
    {
        var old = Create();
        var insensitive = ImmutableSortedDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase).Add("COLOR", "blue");
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.ReviseDefinition(old.Sku, old.Definition, 1, new(1001),
            Input() with { Attributes = insensitive }, EffectiveAt));
        var normalized = CatalogPolicy.CreateSku(new(101), new(10), new(20), ReplacementPublicId,
            "PEN-OTHER", "PEN-ITEM", new(1001), Input() with { Attributes = insensitive }, EffectiveAt);
        Assert.Equal("blue", normalized.Definition.Attributes["COLOR"]);
        Assert.False(normalized.Definition.Attributes.ContainsKey("color"));
    }

    [Fact]
    public void Technical_text_normalization_preserves_unicode_and_does_not_infer_units_or_ratios()
    {
        var created = CatalogPolicy.CreateSku(new(100), new(10), new(20), PublicId, " pen-12 ", " Barang 原本 ", new(1000),
            Input() with { PackageLabel = " Dus 原本 ", BaseUnitCode = " unit-khusus ",
                Attributes = Attributes((" warna ", " biru muda 原本 ")) }, EffectiveAt.ToOffset(TimeSpan.FromHours(7)));
        Assert.Equal("Barang 原本", created.Sku.PhysicalItemKey);
        Assert.Equal("Dus 原本", created.Definition.PackageLabel);
        Assert.Equal("unit-khusus", created.Definition.BaseUnitCode);
        Assert.Equal("biru muda 原本", created.Definition.Attributes["warna"]);
        Assert.Equal(TimeSpan.Zero, created.Definition.EffectiveAt.Offset);
        Assert.Equal(EffectiveAt, created.Definition.EffectiveAt);
        Assert.Equal(12, created.Definition.BaseUnitsPerPackage);
    }

    [Fact]
    public void Input_collections_cannot_change_an_already_created_definition()
    {
        var source = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        source.Add("color", "blue");
        var images = ImmutableArray.CreateBuilder<LongId>(); images.Add(new(900));
        var created = Create(Input() with { Attributes = source.ToImmutable(), ImageAssetIds = images.ToImmutable() });
        source["color"] = "red"; images[0] = new(901);
        Assert.Equal("blue", created.Definition.Attributes["color"]);
        Assert.Equal(900L, Assert.Single(created.Definition.ImageAssetIds).Value);
    }

    [Fact]
    public void Minimum_and_increment_are_independent_from_package_contents_and_the_count_calculation()
    {
        var definition = Create(Input() with { MinimumOrderPackages = 3, OrderIncrementPackages = 2 }).Definition;
        Assert.Equal(3, definition.MinimumOrderPackages);
        Assert.Equal(2, definition.OrderIncrementPackages);
        // This is an exact count calculation, not an order-admissibility decision.
        Assert.Equal(12L, CatalogPolicy.BaseUnitsForPackages(definition, 1));
        Assert.Equal(48L, CatalogPolicy.BaseUnitsForPackages(definition, 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000001)]
    public void Out_of_contract_order_quantities_do_not_produce_base_counts(int value)
    {
        var created = Create();
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.BaseUnitsForPackages(created.Definition, value));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { MinimumOrderPackages = value }));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { OrderIncrementPackages = value }));
    }

    [Theory]
    [InlineData("sku-id")]
    [InlineData("organization-id")]
    [InlineData("family-id")]
    [InlineData("definition-id")]
    public void Default_long_ids_cannot_enter_a_new_catalog_transition(string field) =>
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateSku(
            field == "sku-id" ? default : new(100), field == "organization-id" ? default : new(10),
            field == "family-id" ? default : new(20), PublicId, "PEN-12", "PEN-ITEM",
            field == "definition-id" ? default : new(1000), Input(), EffectiveAt));

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("12345678-1234-1123-8123-123456789abc")]
    [InlineData("12345678-1234-4123-7123-123456789abc")]
    public void Public_ids_must_be_uuid_v4_with_the_correct_variant(string publicId) =>
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateSku(new(100), new(10), new(20),
            Guid.Parse(publicId), "PEN-12", "PEN-ITEM", new(1000), Input(), EffectiveAt));

    [Theory]
    [InlineData("current-pointer")]
    [InlineData("definition-sku")]
    [InlineData("sku-revision-zero")]
    [InlineData("definition-version-zero")]
    [InlineData("self-predecessor")]
    [InlineData("noncanonical-code")]
    public void Malformed_current_aggregate_is_rejected_before_revising_it(string malformed)
    {
        var current = Create(); var sku = current.Sku; var definition = current.Definition;
        switch (malformed)
        {
            case "current-pointer": sku = sku with { CurrentDefinitionId = new(1999) }; break;
            case "definition-sku": definition = definition with { SkuId = new(199) }; break;
            case "sku-revision-zero": sku = sku with { Revision = 0 }; break;
            case "definition-version-zero": definition = definition with { Version = 0 }; break;
            case "self-predecessor": sku = sku with { PredecessorSkuId = sku.Id }; break;
            default: sku = sku with { Code = "pen-12" }; break;
        }
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.ReviseDefinition(sku, definition, sku.Revision, new(1001), Input(), EffectiveAt));
    }

    [Fact]
    public void Revision_conflicts_reused_definition_ids_and_backdated_effectivity_are_denied()
    {
        var old = Create();
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.ReviseDefinition(old.Sku, old.Definition, 2, new(1001), Input(), EffectiveAt));
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.ReviseDefinition(old.Sku, old.Definition, 1, old.Definition.Id, Input(), EffectiveAt));
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.ReviseDefinition(old.Sku, old.Definition, 1, new(1001), Input(), EffectiveAt.AddTicks(-1)));
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateSku(new(100), new(10), new(20), PublicId, "PEN-12", "PEN-ITEM", new(1000), Input(), default));
    }

    [Fact]
    public void Exhausted_sku_or_definition_revision_does_not_wrap_or_alter_the_prior_objects()
    {
        var old = Create();
        foreach (var exhausted in new[]
        {
            new SkuDefinitionChange(old.Sku with { Revision = long.MaxValue }, old.Definition),
            new SkuDefinitionChange(old.Sku with { Revision = 8 }, old.Definition with { Version = long.MaxValue })
        })
        {
            var before = JsonSerializer.Serialize(exhausted);
            Assert.Throws<DomainValidationException>(() => CatalogPolicy.ReviseDefinition(exhausted.Sku, exhausted.Definition,
                exhausted.Sku.Revision, new(1001), Input() with { PackageLabel = "Karton" }, EffectiveAt));
            Assert.Equal(before, JsonSerializer.Serialize(exhausted));
        }
    }

    [Fact]
    public void Transition_ids_above_javascript_integer_precision_keep_exact_string_json_identity()
    {
        var current = CatalogPolicy.CreateSku(new(9007199254740993), new(9007199254740995), new(9007199254740997),
            PublicId, "PEN-LARGE", "PEN-ITEM", new(9007199254740999), Input(), EffectiveAt);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(current));
        Assert.Equal("9007199254740993", json.RootElement.GetProperty("Sku").GetProperty("Id").GetString());
        Assert.Equal("9007199254740999", json.RootElement.GetProperty("Definition").GetProperty("Id").GetString());
        Assert.Equal("9007199254740993", JsonSerializer.Deserialize<LongId>("\"9007199254740993\"").ToString());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LongId>("9007199254740993"));
    }

    [Fact]
    public void Aggregate_and_definition_revisions_advance_independently()
    {
        var old = Create();
        var current = old.Sku with { Revision = 8 };
        var definition = old.Definition with { Version = 2 };
        var revised = CatalogPolicy.ReviseDefinition(current, definition, 8, new(1001),
            Input() with { PackageLabel = "Karton" }, EffectiveAt);
        Assert.Equal(9L, revised.Sku.Revision);
        Assert.Equal(3L, revised.Definition.Version);
    }

    [Fact]
    public void Large_explicit_contents_are_multiplied_as_long_before_any_narrow_integer_operation()
    {
        var definition = Create(Input() with { BaseUnitsPerPackage = int.MaxValue }).Definition;
        Assert.Equal(2147483647000000L, CatalogPolicy.BaseUnitsForPackages(definition, 1000000));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { BaseUnitsPerPackage = 0 }));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { BaseUnitsPerPackage = -1 }));
    }

    [Fact]
    public void Equivalent_content_replacement_is_explicit_and_never_inferred_as_the_same_commercial_identity()
    {
        var old = Create();
        var replacement = CatalogPolicy.CreateReplacementSku(old.Sku, old.Definition, 1,
            new(101), ReplacementPublicId, "PEN-12-B", "PEN-ITEM", new(1001), Input(), EffectiveAt);
        Assert.Equal(101L, replacement.Sku.Id.Value);
        Assert.Equal(old.Sku.Id, replacement.Sku.PredecessorSkuId);
        Assert.Equal(12, replacement.Definition.BaseUnitsPerPackage);
        Assert.True(old.Sku.Active);
    }

    [Fact]
    public void Retired_correction_and_replacement_preserve_the_original_retired_state()
    {
        var old = Create(); var retired = old.Sku with { Active = false };
        var corrected = CatalogPolicy.ReviseDefinition(retired, old.Definition, 1, new(1001),
            Input() with { PackageLabel = "Nama diperbaiki" }, EffectiveAt);
        Assert.False(corrected.Sku.Active);
        var replacement = CatalogPolicy.CreateReplacementSku(retired, old.Definition, 1,
            new(101), ReplacementPublicId, "PEN-10", "pen-item", new(1002), Input(), EffectiveAt);
        Assert.Equal("pen-item", replacement.Sku.PhysicalItemKey);
        Assert.False(retired.Active);
        Assert.Equal("PEN-ITEM", retired.PhysicalItemKey);
    }

    [Theory]
    [InlineData("loose")]
    [InlineData("sealed")]
    [InlineData("other")]
    public void Known_package_kinds_preserve_explicit_contents_without_global_conversion_rules(string kind)
    {
        var created = Create(Input() with { PackageKind = kind, BaseUnitsPerPackage = 24 });
        Assert.Equal(kind, created.Definition.PackageKind);
        Assert.Equal(24, created.Definition.BaseUnitsPerPackage);
    }

    [Theory]
    [InlineData("Loose")]
    [InlineData("box")]
    [InlineData("")]
    [InlineData("sealed\n")]
    public void Unknown_or_noncanonical_package_kind_codes_are_rejected(string kind) =>
        Assert.Throws<DomainValidationException>(() => Create(Input() with { PackageKind = kind }));

    [Fact]
    public void Normalized_attribute_key_collisions_are_rejected_instead_of_overwriting_physical_facts()
    {
        var input = Input() with { Attributes = Attributes(("color", "blue"), (" color ", "black")) };
        Assert.Throws<DomainValidationException>(() => Create(input));
    }

    [Fact]
    public void Technical_text_and_collection_limits_fail_without_truncation_or_partial_transition()
    {
        var largest = Input() with
        {
            PackageLabel = new string('L', 160), BaseUnitCode = new string('U', 64),
            Attributes = Enumerable.Range(0, 32).ToImmutableSortedDictionary(i => i == 0 ? new string('K', 64) : "key" + i,
                _ => new string('V', 256), StringComparer.Ordinal),
            ImageAssetIds = Enumerable.Range(1, 16).Select(i => new LongId(i)).ToImmutableArray()
        };
        var accepted = Create(largest);
        Assert.Equal(32, accepted.Definition.Attributes.Count);
        Assert.Equal(16, accepted.Definition.ImageAssetIds.Length);
        foreach (var invalid in new[]
        {
            largest with { PackageLabel = new string('L', 161) },
            largest with { BaseUnitCode = new string('U', 65) },
            largest with { Attributes = Attributes((new string('K', 65), "value")) },
            largest with { Attributes = Attributes(("key", new string('V', 257))) },
            largest with { Attributes = largest.Attributes.Add("overflow", "value") },
            largest with { ImageAssetIds = largest.ImageAssetIds.Add(new(17)) },
            largest with { ImageAssetIds = [new(1), new(1)] },
            largest with { ImageAssetIds = [default] },
            largest with { ImageAssetIds = default },
            largest with { Attributes = null! }
        }) Assert.Throws<DomainValidationException>(() => Create(invalid));
        Assert.Equal(160, accepted.Definition.PackageLabel.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nvalue")]
    [InlineData("bad\u0000value")]
    public void Empty_or_control_bearing_descriptions_and_physical_keys_are_rejected(string invalid)
    {
        Assert.Throws<DomainValidationException>(() => Create(Input() with { PackageLabel = invalid }));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { BaseUnitCode = invalid }));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { Attributes = Attributes((invalid, "value")) }));
        Assert.Throws<DomainValidationException>(() => Create(Input() with { Attributes = Attributes(("key", invalid)) }));
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateSku(new(100), new(10), new(20), PublicId,
            "PEN-12", invalid, new(1000), Input(), EffectiveAt));
    }

    [Fact]
    public void Physical_item_key_has_a_bounded_ordinal_identity_and_is_never_case_folded()
    {
        var accepted = CatalogPolicy.CreateSku(new(100), new(10), new(20), PublicId,
            "PEN-12", new string('k', 128), new(1000), Input(), EffectiveAt);
        Assert.Equal(new string('k', 128), accepted.Sku.PhysicalItemKey);
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateSku(new(101), new(10), new(20), ReplacementPublicId,
            "PEN-12-B", new string('k', 129), new(1001), Input(), EffectiveAt));
    }

    [Fact]
    public void Invalid_utf16_cannot_silently_change_physical_text_during_serialization()
    {
        foreach (var invalid in new[] { "item\ud800", "item\udc00", "item\ud800text" })
        {
            Assert.Throws<DomainValidationException>(() => Create(Input() with { PackageLabel = invalid }));
            Assert.Throws<DomainValidationException>(() => Create(Input() with { BaseUnitCode = invalid }));
            Assert.Throws<DomainValidationException>(() => Create(Input() with { Attributes = Attributes((invalid, "value")) }));
            Assert.Throws<DomainValidationException>(() => Create(Input() with { Attributes = Attributes(("key", invalid)) }));
            Assert.Throws<DomainValidationException>(() => CatalogPolicy.CreateSku(new(100), new(10), new(20), PublicId,
                "PEN-12", invalid, new(1000), Input(), EffectiveAt));
        }
        var valid = Create(Input() with { PackageLabel = "Barang \U0001f4e6", Attributes = Attributes(("label", "Kotak \U0001f4e6")) });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(valid.Definition));
        Assert.Equal("Barang \U0001f4e6", json.RootElement.GetProperty("PackageLabel").GetString());
        Assert.Equal("Kotak \U0001f4e6", json.RootElement.GetProperty("Attributes").GetProperty("label").GetString());
    }

    private static SkuDefinitionChange Create(DefinitionInput? input = null) => CatalogPolicy.CreateSku(
        new(100), new(10), new(20), PublicId, " pen-12 ", "PEN-ITEM", new(1000), input ?? Input(), EffectiveAt);
    private static DefinitionInput Input() => new("Dus", "sealed", "EA", 12, Attributes(("color", "blue")), [], 1, 1);
    private static ImmutableSortedDictionary<string, string> Attributes(params (string Key, string Value)[] pairs) =>
        pairs.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
