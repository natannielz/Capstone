using UnitToko.Modules.Catalog;

namespace UnitToko.Tests.Catalog;

public sealed class ProductMatchRuleTests
{
    [Fact]
    public void Exact_codes_outrank_bounded_name_hints_without_guessing_contents()
    {
        Assert.Equal("exact_code", ProductMatchRule.Match("  pen-001 ", "PEN-001", "Pensil", "Dus")!.Kind);
        Assert.True(ProductMatchRule.Match("PEN-001", "PEN-001", "Pensil", "Dus")!.Rank < ProductMatchRule.Match("PEN-001", "OTHER", "PEN-001", "Dus")!.Rank);
        Assert.Equal("exact_name", ProductMatchRule.Match("Pensil warna", "P-1", "Pensil warna", "Dus")!.Kind);
        Assert.Equal("token_prefix", ProductMatchRule.Match("pen war", "P-1", "Pensil warna", "Dus")!.Kind);
        Assert.Equal(new ProductMatch("similar_name", "name", 1, 4), ProductMatchRule.Match("pensil warnb", "P-1", "Pensil warna", "Dus"));
        Assert.Null(ProductMatchRule.Match("12", "P-1", "Pensil", "Dus"));
        Assert.Null(ProductMatchRule.Match("00012", "00013", "Pensil", "Dus"));
        Assert.Null(ProductMatchRule.Match("ab", "P-1", "Ac", "Dus"));
        Assert.Null(ProductMatchRule.Match("abcdefgh", "P-1", "abcdzzzz", "Dus"));
        Assert.Null(ProductMatchRule.Match("satu dua tiga empat lima", "P-1", "saty duy tigy empat limy", "Dus"));
        Assert.Equal("contains", ProductMatchRule.Match("Dus", "P-1", "Pensil", "Dus")!.Kind);
        Assert.Equal(ProductMatchRule.Match("Pénsíl", "P-1", "Pénsíl", "Dus"), ProductMatchRule.Match("Pe\u0301nsi\u0301l", "P-1", "Pénsíl", "Dus"));
    }
}
