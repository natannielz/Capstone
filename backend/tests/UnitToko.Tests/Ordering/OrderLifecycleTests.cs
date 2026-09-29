using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Ordering;

public sealed class OrderLifecycleTests
{
    [Fact]
    public void Unknown_spending_policy_never_becomes_an_implicit_waiver_even_for_zero()
    {
        Assert.Equal("order_spending_policy_unavailable", Assert.Throws<DomainValidationException>(() => OrderSpendingPolicy.Initial(null, new(0))).Code);
        Assert.Equal(OrderSpendingState.Pending, OrderSpendingPolicy.Initial(new(OrderSpendingMode.EveryOrder, null), new(0)));
        Assert.Equal(OrderSpendingState.NotRequired, OrderSpendingPolicy.Initial(new(OrderSpendingMode.NotRequired, null), new(100)));
        Assert.False(OrderSpendingPolicy.CanAcceptAtStore(OrderSpendingState.Pending));
        Assert.False(OrderSpendingPolicy.CanAcceptAtStore(OrderSpendingState.Rejected));
        Assert.False(OrderSpendingPolicy.CanAcceptAtStore((OrderSpendingState)999));
        Assert.True(OrderSpendingPolicy.CanAcceptAtStore(OrderSpendingState.Approved));
    }
    [Fact]
    public void Explicit_above_threshold_rule_compares_exact_amounts_and_rejects_ambiguous_configuration()
    {
        var rule = new OrderSpendingRule(OrderSpendingMode.AboveThreshold, Money.Parse("100000.00"));
        Assert.Equal(OrderSpendingState.NotRequired, OrderSpendingPolicy.Initial(rule, Money.Parse("100000.00")));
        Assert.Equal(OrderSpendingState.Pending, OrderSpendingPolicy.Initial(rule, Money.Parse("100000.01")));
        Assert.Throws<DomainValidationException>(() => OrderSpendingPolicy.Initial(rule with { Threshold = null }, new(1)));
        Assert.Throws<DomainValidationException>(() => OrderSpendingPolicy.Initial(rule with { Mode = OrderSpendingMode.NotRequired }, new(1)));
        Assert.Throws<DomainValidationException>(() => OrderSpendingPolicy.Initial(rule with { Mode = (OrderSpendingMode)99 }, new(1)));
    }
    [Fact]
    public void Ten_requested_eight_dispatched_two_cancelled_releases_only_the_reserved_remainder()
    {
        var start = new OrderLineProgress(10, 0, 0, 0);
        var reserved = OrderLinePolicy.Reserve(start, 10); var shipped = OrderLinePolicy.Dispatch(reserved, 8);
        var cancelled = OrderLinePolicy.CancelRemainder(shipped);
        Assert.Equal(new(10, 0, 8, 2), cancelled.Next); Assert.Equal(2, cancelled.ReleaseReserved); Assert.Equal(0, cancelled.Next.Unallocated);
        Assert.Equal(new(10, 0, 0, 0), start);
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.CancelRemainder(cancelled.Next));
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Reserve(cancelled.Next, 1));
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Dispatch(cancelled.Next, 1));
    }
    [Fact]
    public void Partial_allocation_cancellation_does_not_release_stock_that_was_never_reserved()
    {
        var line = OrderLinePolicy.Dispatch(OrderLinePolicy.Reserve(new(10, 0, 0, 0), 5), 3);
        var cancelled = OrderLinePolicy.CancelRemainder(line);
        Assert.Equal(new(10, 0, 3, 7), cancelled.Next); Assert.Equal(2, cancelled.ReleaseReserved);
    }
    [Theory]
    [InlineData(0, 0, 0, 0)] [InlineData(1000001, 0, 0, 0)] [InlineData(10, -1, 0, 0)]
    [InlineData(10, 0, -1, 0)] [InlineData(10, 0, 0, -1)] [InlineData(10, 5, 5, 1)]
    [InlineData(1000000, int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Invalid_or_overflowing_progress_cannot_create_or_release_demand(int requested, int reserved, int shipped, int cancelled)
    {
        var line = new OrderLineProgress(requested, reserved, shipped, cancelled);
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Reserve(line, 1));
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Dispatch(line, 1));
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.CancelRemainder(line));
    }
    [Fact]
    public void Unallocated_packages_cannot_be_dispatched_and_overreservation_is_rejected()
    {
        var line = new OrderLineProgress(10, 3, 0, 0);
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Dispatch(line, 4));
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Reserve(line, 8));
        Assert.Throws<DomainValidationException>(() => OrderLinePolicy.Dispatch(line, 0));
    }
    private static OrderQuotedLine Line(long sku, long channel, string amount, int contents = 12)
    {
        var at = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var definition = new VariantDefinitionVersion(new(sku + 100), new(sku), 1, "Dus", "sealed", "piece", contents,
            ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1, at);
        var price = new PriceVersion(new(sku + 200), new(sku), 1, "IDR", Money.Parse(amount), at);
        return new(new(1), "ATK", Guid.Parse("b614f866-0d96-4b0d-8fbe-c9faf7ee67a0"), "SKU-" + sku,
            new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: channel), definition, price, 2, 2L * contents, Money.Multiply(price.UnitPrice, 2));
    }
    [Fact]
    public void Mixed_channels_split_once_without_duplicating_parent_demand_or_losing_package_meaning()
    {
        var omi = Line(1001, 103, "10000.25"); var smart = Line(1002, 104, "5000.10", 10); var otherOmi = Line(1003, 103, "0.00");
        var quote = new OrderCatalogQuote([omi, smart, otherOmi], "IDR", Money.Parse("30000.70"));
        var groups = OrderFulfillmentPolicy.Split(quote);
        Assert.Equal(2, groups.Length); Assert.Equal(new long?[] { 103, 104 }, groups.Select(group => group.Owner.ChannelId));
        Assert.Equal(new[] { omi.Definition.SkuId, otherOmi.Definition.SkuId }, groups[0].Lines.Select(line => line.Definition.SkuId));
        Assert.Equal(20, Assert.Single(groups[1].Lines).BaseUnits); Assert.Equal(24, groups[0].Lines[0].BaseUnits);
        Assert.Equal(quote.CatalogSubtotal.Value, groups.Sum(group => group.CatalogSubtotal.Value));
        Assert.Equal(3, groups.Sum(group => group.Lines.Length));
        Assert.NotSame(omi.Definition, groups[0].Lines[0].Definition);
    }
    [Theory]
    [InlineData("contents")] [InlineData("amount")] [InlineData("currency")] [InlineData("priceSku")] [InlineData("duplicate")] [InlineData("definition")]
    public void Corrupt_quote_cannot_become_operational_suborders(string fault)
    {
        var line = Line(1001, 103, "10.00");
        line = fault switch {
            "contents" => line with { BaseUnits = 2 }, "amount" => line with { CatalogSubtotal = new(1) },
            "currency" => line with { Price = line.Price with { Currency = "USD" } }, "priceSku" => line with { Price = line.Price with { SkuId = new(999) } },
            "definition" => line with { Definition = line.Definition with { BaseUnitsPerPackage = -12 }, BaseUnits = -24 }, _ => line
        };
        Assert.Throws<DomainValidationException>(() => OrderFulfillmentPolicy.Split(new(fault == "duplicate" ? [line, line] : [line], "IDR", new(20))));
    }
}
