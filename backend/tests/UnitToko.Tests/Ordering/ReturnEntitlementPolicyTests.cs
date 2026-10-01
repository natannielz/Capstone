using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;

public sealed class ReturnEntitlementPolicyTests
{
    private static IssuedReturnLine Issued() => new(11, 21, 3, 0, new(0m), new(10m), new(1m), new(2m), new(10m), 3, "half_even");
    private static ReturnLineCapacity Capacity(IssuedReturnLine line, long previous = 0, decimal previousNet = 0m, long inspected = 1,
        decimal previousTax = 0m, decimal previousCharges = 0m) => new(line, previous, new(previousNet), new(previousTax), new(previousCharges), inspected);
    private static ReturnEntitlementInput Input(decimal tax = 0m, decimal charges = 0m) => new([new(11, 21, new(tax), new(charges))]);
    private static ReturnEntitlementResult Review(ReturnLineCapacity capacity, ReturnEntitlementInput? input = null) =>
        ReturnEntitlementPolicy.Review([capacity], input ?? Input(), "tail_backout", "manual_component_allocation");

    [Fact]
    public void Tail_returns_conserve_the_issued_rounding_remainder_and_full_return() {
        var line = Issued();
        var first = Review(Capacity(line));
        var second = Review(Capacity(line, 1, first.Net.Value));
        var third = Review(Capacity(line, 2, Money.Add(first.Net, second.Net).Value));
        Assert.Equal(3.33m, first.Net.Value); Assert.Equal(3.34m, second.Net.Value); Assert.Equal(3.33m, third.Net.Value);
        Assert.Equal(line.Net, Money.Add(Money.Add(first.Net, second.Net), third.Net));
        Assert.Equal(10m, Review(Capacity(line, inspected: 3)).Credit.Value);
        // This invoice contains the second accepted unit; its issued 3.34 remainder is preserved.
        var middle = line with { PreviousBaseUnits = 1, PreviousNet = new(3.33m), AcceptedBaseUnits = 1, Net = new(3.34m) };
        Assert.Equal(3.34m, Review(Capacity(middle)).Net.Value);
    }
    [Theory]
    [InlineData(1, 3.34)]
    [InlineData(2, 6.66)]
    [InlineData(-1, 0)]
    [InlineData(4, 10)]
    public void Incorrect_prior_quantity_or_tail_net_is_refused(long previous, decimal net) =>
        Assert.Equal("return_entitlement_history_invalid", Assert.Throws<DomainValidationException>(() => Review(Capacity(Issued(), previous, net))).Code);

    [Fact]
    public void Corrupted_issued_history_and_exhausted_quantity_are_refused() {
        Assert.Equal("return_entitlement_history_invalid", Assert.Throws<DomainValidationException>(() => Review(Capacity(Issued() with { Net = new(9.99m) }))).Code);
        Assert.Equal("return_entitlement_history_invalid", Assert.Throws<DomainValidationException>(() => Review(Capacity(Issued() with {
            PreviousBaseUnits = 2, PreviousNet = new(6.66m), AcceptedBaseUnits = 1, Net = new(3.34m) }))).Code);
        Assert.Equal("return_entitlement_capacity_exceeded", Assert.Throws<DomainValidationException>(() => Review(Capacity(Issued(), 3, 10m))).Code);
        Assert.Equal("return_entitlement_capacity_exceeded", Assert.Throws<DomainValidationException>(() => Review(Capacity(Issued(), inspected: 4))).Code);
    }
    [Fact]
    public void Tax_and_fees_are_explicit_bounded_components_without_automatic_full_return_remainder() {
        var line = Issued(); var capacity = Capacity(line, 1, 3.33m, 2, .4m, .5m);
        var result = Review(capacity, Input(.6m, 1.5m));
        Assert.Equal(6.67m, result.Net.Value); Assert.Equal(.6m, result.Tax.Value); Assert.Equal(1.5m, result.Charges.Value); Assert.Equal(8.77m, result.Credit.Value);
        var manual = Review(capacity);
        Assert.Equal(0m, manual.Tax.Value); Assert.Equal(0m, manual.Charges.Value); Assert.Equal(6.67m, manual.Credit.Value);
        Assert.Equal("return_entitlement_capacity_exceeded", Assert.Throws<DomainValidationException>(() => Review(capacity, Input(.61m))).Code);
        Assert.Equal("return_entitlement_capacity_exceeded", Assert.Throws<DomainValidationException>(() => Review(capacity, Input(charges: 1.51m))).Code);
        Assert.Equal("return_entitlement_history_invalid", Assert.Throws<DomainValidationException>(() => Review(Capacity(line, previousTax: .01m))).Code);
    }
    [Fact]
    public void Zero_net_is_valid_with_explicit_positive_components_but_an_empty_total_is_refused() {
        var line = Issued() with { PackagePrice = new(0m), Net = new(0m) };
        Assert.Equal(.01m, Review(Capacity(line), Input(.01m)).Credit.Value);
        Assert.Equal("return_entitlement_amount_invalid", Assert.Throws<DomainValidationException>(() => Review(Capacity(line))).Code);
    }
    [Fact]
    public void Frozen_rounding_modes_control_tail_attribution() {
        var line = Issued() with { PackagePrice = new(.05m), BaseUnitsPerPackage = 2, AcceptedBaseUnits = 2, Net = new(.05m) };
        Assert.Equal(.03m, Review(Capacity(line)).Net.Value);
        Assert.Equal(.02m, Review(Capacity(line with { Rounding = "half_up" })).Net.Value);
        Assert.Throws<DomainValidationException>(() => Review(Capacity(line with { Rounding = "unknown" })));
    }
    [Fact]
    public void Rules_input_keys_and_complete_inspected_set_are_required() {
        var capacity = Capacity(Issued()); var input = Input();
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Review([capacity], input, "", "manual_component_allocation"));
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Review([capacity], input, "tail_backout", "proportional"));
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Copy(new([input.Lines[0], input.Lines[0]])));
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Copy(new(ImmutableArray<ReturnEntitlementLineInput>.Empty)));
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Copy(new([input.Lines[0] with { ReceiptId = 0 }])));
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Review([], input, "tail_backout", "manual_component_allocation"));
        Assert.Throws<DomainValidationException>(() => Review(capacity, new([input.Lines[0] with { SkuId = 22 }])));
        Assert.Throws<DomainValidationException>(() => ReturnEntitlementPolicy.Review([capacity, capacity], new([input.Lines[0], input.Lines[0] with { SkuId = 22 }]), "tail_backout", "manual_component_allocation"));
        var input2 = input.Lines[0] with { ReceiptId = 9007199254740993L };
        var sorted = ReturnEntitlementPolicy.Copy(new([input2, input.Lines[0]]));
        Assert.Equal(11, sorted.Lines[0].ReceiptId); Assert.Equal(9007199254740993L, sorted.Lines[1].ReceiptId);
    }
}
