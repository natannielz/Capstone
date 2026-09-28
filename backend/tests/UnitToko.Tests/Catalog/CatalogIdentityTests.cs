using System.Text.Json;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Catalog;

public sealed class CatalogIdentityTests
{
    // Validation fixtures only; passing a checksum never allocates or authenticates a GTIN.
    [Theory]
    [InlineData("96385074", "00000096385074", 8)]
    [InlineData("036000291452", "00036000291452", 12)]
    [InlineData("4006381333931", "04006381333931", 13)]
    [InlineData("04006381333931", "04006381333931", 14)]
    public void Supplied_gtin_length_and_leading_zeros_survive_normalization(string raw, string canonical, int length)
    {
        var gtin = Gtin.Parse(raw);
        Assert.Equal(raw, gtin.Raw);
        Assert.Equal(canonical, gtin.Canonical14);
        Assert.Equal(length, gtin.SuppliedLength);
    }

    [Fact]
    public void Equivalent_supplied_packaging_identifiers_compare_by_canonical_value()
    {
        var shortForm = Gtin.Parse("4006381333931");
        var paddedForm = Gtin.Parse("04006381333931");
        Assert.Equal(shortForm.Canonical14, paddedForm.Canonical14);
        Assert.NotEqual(shortForm.Raw, paddedForm.Raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4006381333932")]
    [InlineData("96385075")]
    [InlineData("036000291453")]
    [InlineData("04006381333932")]
    [InlineData("400638133393")]
    [InlineData("000000000000000")]
    [InlineData("4006 381333931")]
    [InlineData(" 4006381333931")]
    [InlineData("4006381333931 ")]
    [InlineData("4006381333931\n")]
    [InlineData("４００６３８１３３３９３１")]
    [InlineData("٤٠٠٦٣٨١٣٣٣٩٣١")]
    [InlineData("+4006381333931")]
    [InlineData("4.006381333931E12")]
    public void Malformed_gtins_fail_without_guessing_a_code(string? raw) =>
        Assert.Throws<DomainValidationException>(() => Gtin.Parse(raw!));

    [Fact]
    public void Gtin_json_retains_original_string_and_rejects_number_or_object_forms()
    {
        var gtin = JsonSerializer.Deserialize<Gtin>("\"04006381333931\"");
        Assert.NotNull(gtin);
        Assert.Equal("04006381333931", gtin.Raw);
        Assert.Equal("\"04006381333931\"", JsonSerializer.Serialize(gtin));
        foreach (var json in new[] { "4006381333931", "4006381333931.0", "null", "{}", "[\"4006381333931\"]", "true" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Gtin>(json));
    }

    [Theory]
    [InlineData("  atk-01  ", "ATK-01")]
    [InlineData("water.case_12", "WATER.CASE_12")]
    [InlineData("a", "A")]
    public void Sku_codes_have_one_canonical_ascii_form(string supplied, string expected) =>
        Assert.Equal(expected, CatalogPolicy.NormalizeSkuCode(supplied));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("A B")]
    [InlineData("A/B")]
    [InlineData("SKU:123")]
    [InlineData("CAＦE")]
    [InlineData("café")]
    [InlineData("ß")]
    [InlineData("ſku")]
    [InlineData("a\u202eb")]
    [InlineData("A\nB")]
    public void Sku_codes_reject_ambiguous_or_non_ascii_identifiers(string? code) =>
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.NormalizeSkuCode(code!));

    [Fact]
    public void Sku_code_limit_is_applied_without_truncating_distinct_identifiers()
    {
        Assert.Equal(new string('A', 64), CatalogPolicy.NormalizeSkuCode(new string('a', 64)));
        Assert.Throws<DomainValidationException>(() => CatalogPolicy.NormalizeSkuCode(new string('a', 65)));
    }
}
