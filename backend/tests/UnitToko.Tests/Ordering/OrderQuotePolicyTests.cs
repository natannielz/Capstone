using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Ordering;

public sealed class OrderQuotePolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly GrantScope Owner = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static OrderCatalogFact Fact(long id = 9007199254740993, int contents = 12, string amount = "12500.25", string currency = "IDR")
    {
        var definitionId = new LongId(id + 1);
        return new(new(new(200), new(100), Owner, Guid.Parse("62cc62ee-5821-4d42-8134-2c721ac58e9e"), new(300), "ATK", "Pensil", "Kemasan eksplisit", [], true, 1),
            new(new(id), new(100), new(200), Guid.Parse("b614f866-0d96-4b0d-8fbe-c9faf7ee67a0"), "PENCIL-" + contents, "pencil", true, definitionId, 1),
            new(definitionId, new(id), 1, "Dus", "sealed", "piece", contents, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1, At),
            new(new(id + 2), new(id), 1, currency, Money.Parse(amount), At));
    }
    private static OrderPackageSelection Select(OrderCatalogFact fact, int packages = 1) =>
        new(fact.Sku.Id, fact.Definition.Id, fact.Definition.Version, fact.Price!.Id, fact.Price.Version, packages);
    [Fact]
    public void Explicit_same_label_packages_keep_distinct_contents_and_exact_per_package_amounts()
    {
        var twelve = Fact(); var ten = Fact(9007199254741003, 10, "10000.10");
        var quote = OrderQuotePolicy.Create([Select(twelve, 2), Select(ten, 3)], [ten, twelve]);
        Assert.Equal(new long[] { 24, 30 }, quote.Lines.Select(line => line.BaseUnits));
        Assert.Equal("55000.80", quote.CatalogSubtotal.ToString()); Assert.Equal("IDR", quote.Currency);
        Assert.Equal(new[] { twelve.Definition.Id, ten.Definition.Id }, quote.Lines.Select(line => line.Definition.Id));
        Assert.Equal(twelve.Price, quote.Lines[0].Price);
    }
    [Theory]
    [InlineData("definitionId")] [InlineData("definitionVersion")] [InlineData("priceId")] [InlineData("priceVersion")]
    public void Changed_package_or_price_requires_fresh_buyer_selection(string change)
    {
        var fact = Fact(); var selected = Select(fact);
        selected = change switch {
            "definitionId" => selected with { DefinitionId = new(999) }, "definitionVersion" => selected with { DefinitionVersion = 2 },
            "priceId" => selected with { PriceId = new(999) }, _ => selected with { PriceVersion = 2 }
        };
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([selected], [fact]));
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000001)] [InlineData(5)] [InlineData(3)]
    public void Minimum_and_package_multiple_are_both_required(int quantity)
    {
        var fact = Fact(); fact = fact with { Definition = fact.Definition with { MinimumOrderPackages = 5, OrderIncrementPackages = 2 } };
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([Select(fact, quantity)], [fact]));
        Assert.Equal(72, Assert.Single(OrderQuotePolicy.Create([Select(fact, 6)], [fact]).Lines).BaseUnits);
    }
    [Fact]
    public void Approved_zero_is_valid_but_missing_price_duplicate_choices_or_mixed_currency_are_not()
    {
        var zero = Fact(amount: "0.00"); var selected = Select(zero);
        Assert.Equal("0.00", OrderQuotePolicy.Create([selected], [zero]).CatalogSubtotal.ToString());
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([selected], [zero with { Price = null }]));
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([selected, selected], [zero]));
        var other = Fact(9007199254741003, currency: "USD");
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([selected, Select(other)], [zero, other]));
    }
    [Fact]
    public void Line_and_document_amount_limits_are_checked_without_float_conversion()
    {
        var maximum = Fact(amount: "999999999999999.99");
        Assert.Equal("999999999999999.99", OrderQuotePolicy.Create([Select(maximum)], [maximum]).CatalogSubtotal.ToString());
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([Select(maximum, 2)], [maximum]));
        var extra = Fact(9007199254741003, amount: "0.01");
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([Select(maximum), Select(extra)], [maximum, extra]));
    }
    [Theory]
    [InlineData("retired")] [InlineData("family")] [InlineData("owner")] [InlineData("missing")]
    public void Invalid_or_unavailable_source_facts_cannot_become_a_quote(string change)
    {
        var fact = Fact(); var selected = Select(fact);
        fact = change switch {
            "retired" => fact with { Sku = fact.Sku with { Active = false } },
            "family" => fact with { Family = fact.Family with { Id = new(999) } },
            "owner" => fact with { Family = fact.Family with { AuthoringOwner = Owner with { OrganizationId = 99 } } },
            _ => fact
        };
        Assert.Throws<DomainValidationException>(() => OrderQuotePolicy.Create([selected], change == "missing" ? [] : [fact]));
    }
}
