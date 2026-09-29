using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;

namespace UnitToko.Tests.Ordering;

public sealed class DeliveryReceiptPolicyTests
{
    private static readonly ImmutableArray<ReceiptDispatchedLine> Dispatched = [new(new(10), new(20), 1, 12)];
    [Fact]
    public void Partial_carton_retains_exact_accepted_rejected_and_missing_base_units_without_guessing_packages() {
        var selected = DeliveryReceiptPolicy.Require(Dispatched, [new(new(10), new(0, 6), new(0, 4), new(0, 2), "Empat rusak, dua tidak ditemukan")]);
        var line = Assert.Single(selected); Assert.Equal(6, line.AcceptedBaseUnits); Assert.Equal(4, line.RejectedBaseUnits); Assert.Equal(2, line.MissingBaseUnits);
        Assert.True(line.HasException); Assert.Equal(new(0, 6), line.Declaration.Accepted);
    }
    [Fact]
    public void Complete_packages_conserve_large_derived_totals_with_bounded_each_field() {
        var lines = DeliveryReceiptPolicy.Require([new(new(10), new(20), 1000000, 1000000)], [new(new(10), new(1000000, 0), new(0, 0), new(0, 0), null)]);
        Assert.Equal(1000000000000L, Assert.Single(lines).AcceptedBaseUnits); Assert.False(lines[0].HasException);
    }
    [Theory]
    [InlineData(-1, 0, 0, 0, 1, 0, "Rusak")]
    [InlineData(0, 12, 0, 0, 0, 0, "Rusak")]
    [InlineData(1, 1, 0, 0, 0, 0, "Rusak")]
    [InlineData(0, 6, 0, 0, 0, 5, "Kurang")]
    [InlineData(0, 6, 0, 6, 0, 0, null)]
    [InlineData(1000001, 0, 0, 0, 0, 0, null)]
    public void Invalid_or_unexplained_quantities_are_denied(int ap, int au, int rp, int ru, int mp, int mu, string? reason) =>
        Assert.Throws<DomainValidationException>(() => DeliveryReceiptPolicy.Require(Dispatched, [new(new(10), new(ap, au), new(rp, ru), new(mp, mu), reason)]));
    [Fact]
    public void Every_dispatched_sku_is_explicit_and_unique() {
        var line = new ReceiptLineDeclaration(new(10), new(1, 0), new(0, 0), new(0, 0), null);
        Assert.Throws<DomainValidationException>(() => DeliveryReceiptPolicy.Require(Dispatched, []));
        Assert.Throws<DomainValidationException>(() => DeliveryReceiptPolicy.Require(Dispatched, [line, line]));
        Assert.Throws<DomainValidationException>(() => DeliveryReceiptPolicy.Require(Dispatched, [line with { SkuId = new(11) }]));
    }
}
