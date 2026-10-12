using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Ordering;

public sealed class OrderSubstitutionPolicyTests
{
    private static OrderQuotedLine Line(long sku, int packages, decimal price, long channel = 103) {
        var definition = new VariantDefinitionVersion(new(sku + 10000), new(sku), 1, "Pcs", "loose", "piece", 1,
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal), [], 1, 1, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        return new(new(50), "Paper", Guid.NewGuid(), "P-" + sku, new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: channel),
            definition, new(new(sku + 20000), new(sku), 1, "IDR", new(price), DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            packages, packages, new(price * packages));
    }
    [Fact]
    public void Partial_replacement_retains_shipped_original_and_other_lines_with_explicit_new_price_and_quantity() {
        var original = Line(1, 10, 12.50m); var unchanged = Line(2, 3, 7m); var replacement = Line(3, 4, 8m);
        var quote = new OrderCatalogQuote([original, unchanged], "IDR", new(146m));
        var change = new OrderSubstitutionSelection(new(1), 2, new(new(3), replacement.Definition.Id, 1, replacement.Price.Id, 1, 4));
        var result = OrderSubstitutionPolicy.Replace(quote, new(10, 2, 8, 0), change, replacement, 4);
        Assert.Equal(2, result.ReleasePackages);
        Assert.Equal(4, result.ReservePackages);
        Assert.Equal(new Money(25m), result.RemovedCatalogAmount);
        Assert.Equal(new Money(153m), result.Catalog.CatalogSubtotal);
        Assert.Equal(8, result.Catalog.Lines.Single(line => line.Definition.SkuId.Value == 1).Packages);
        Assert.Equal(4, result.Catalog.Lines.Single(line => line.Definition.SkuId.Value == 3).Packages);
        Assert.Equal(unchanged, result.Catalog.Lines.Single(line => line.Definition.SkuId.Value == 2));
        Assert.Equal(10, original.Packages);
        Assert.Equal(new Money(146m), quote.CatalogSubtotal);
    }
    [Theory]
    [InlineData("dispatched")] [InlineData("released")] [InlineData("price")] [InlineData("definition")]
    [InlineData("currency")] [InlineData("owner")] [InlineData("duplicate")] [InlineData("quantity")]
    public void Replacement_cannot_borrow_dispatched_quantity_or_change_unreviewed_commercial_identity(string fault) {
        var original = Line(1, 10, 12m); var replacement = Line(2, 2, 8m);
        var progress = new OrderLineProgress(10, 2, 8, 0);
        var selected = new OrderSubstitutionSelection(new(1), 2, new(new(2), replacement.Definition.Id, 1, replacement.Price.Id, 1, 2));
        var quote = new OrderCatalogQuote([original], "IDR", new(120m));
        switch (fault) {
            case "dispatched": selected = selected with { OriginalPackages = 3 }; break;
            case "released": progress = new(10, 1, 8, 1); break;
            case "price": selected = selected with { Replacement = selected.Replacement with { PriceVersion = 2 } }; break;
            case "definition": selected = selected with { Replacement = selected.Replacement with { DefinitionVersion = 2 } }; break;
            case "currency": replacement = replacement with { Price = replacement.Price with { Currency = "USD" } }; break;
            case "owner": replacement = replacement with { FulfillmentOwner = replacement.FulfillmentOwner with { ChannelId = 104 } }; break;
            case "duplicate": replacement = original with { Packages = 2, BaseUnits = 2, CatalogSubtotal = new(24m) }; break;
            case "quantity": selected = selected with { Replacement = selected.Replacement with { Packages = 3 } }; break;
        }
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.Replace(quote, progress, selected, replacement, 4));
    }
    [Fact]
    public void Declining_has_no_inventory_or_commercial_effect_and_acceptance_requires_exact_review() {
        Assert.False(OrderSubstitutionPolicy.WritesReservation(OrderSubstitutionDecision.Decline));
        Assert.True(OrderSubstitutionPolicy.WritesReservation(OrderSubstitutionDecision.Accept));
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.WritesReservation((OrderSubstitutionDecision)42));
        OrderSubstitutionPolicy.RequireReview(new string('a', 64), new string('a', 64));
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.RequireReview(new string('a', 64), new string('b', 64)));
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.RequireReview("", ""));
    }
    [Fact]
    public void Acceptance_keeps_capacity_for_renewed_approvals_and_final_cancellation() {
        OrderSubstitutionPolicy.RequireApprovalCapacity(99, 1, OrderSpendingState.Pending);
        OrderSubstitutionPolicy.RequireApprovalCapacity(100, 1, OrderSpendingState.NotRequired);
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.RequireApprovalCapacity(100, 1, OrderSpendingState.Pending));
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.RequireApprovalCapacity(99, 2, OrderSpendingState.Pending));
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.RequireApprovalCapacity(long.MaxValue, 1, OrderSpendingState.NotRequired));
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.RequireApprovalCapacity(1, 1, OrderSpendingState.Approved));
    }
    [Fact]
    public void Repeated_sku_and_same_sku_price_revision_preserve_each_accepted_line_identity() {
        var firstA = Line(1, 10, 12m); var b = Line(2, 2, 8m);
        var original = new OrderCatalogQuote([firstA], "IDR", new(120m));
        OrderSubstitutionSelection Selection(OrderQuotedLine source, int replaced, OrderQuotedLine replacement) => new(source.Definition.SkuId, replaced,
            new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version, replacement.Price.Id, replacement.Price.Version, replacement.Packages), source.LineRevision);
        var one = OrderSubstitutionPolicy.Replace(original, new(10, 2, 8, 0), Selection(firstA, 2, b), b, 4).Catalog;
        var acceptedB = one.Lines.Single(line => line.Definition.SkuId.Value == 2);
        var nextA = firstA with { Packages = 2, BaseUnits = 2, Price = firstA.Price with { Version = 2, UnitPrice = new(15m) }, CatalogSubtotal = new(30m) };
        var two = OrderSubstitutionPolicy.Replace(one, new(2, 2, 0, 0), Selection(acceptedB, 2, nextA), nextA, 7).Catalog;
        Assert.Equal(2, two.Lines.Count(line => line.Definition.SkuId.Value == 1));
        Assert.Equal(new Money(96m), two.Lines.Single(line => line.LineRevision == 0).CatalogSubtotal);
        Assert.Equal(new Money(30m), two.Lines.Single(line => line.LineRevision == 7).CatalogSubtotal);
        var nextAcceptedA = two.Lines.Single(line => line.LineRevision == 7);
        var corrected = nextA with { Price = nextA.Price with { Version = 3, UnitPrice = new(17m) }, CatalogSubtotal = new(34m) };
        var three = OrderSubstitutionPolicy.Replace(two, new(2, 2, 0, 0), Selection(nextAcceptedA, 2, corrected), corrected, 10).Catalog;
        Assert.Equal(new Money(130m), three.CatalogSubtotal);
        Assert.DoesNotContain(three.Lines, line => line.LineRevision == 7);
        Assert.Equal(3, three.Lines.Single(line => line.LineRevision == 10).Price.Version);
        Assert.Equal(new Money(120m), original.CatalogSubtotal);
        Assert.Throws<DomainValidationException>(() => OrderSubstitutionPolicy.Replace(three, new(2, 2, 0, 0),
            Selection(nextAcceptedA, 2, corrected), corrected, 13));
    }
}
