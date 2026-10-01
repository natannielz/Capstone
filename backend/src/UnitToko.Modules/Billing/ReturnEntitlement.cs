using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record ReturnEntitlementLineInput(long ReceiptId, long SkuId, Money Tax, Money Charges);
public sealed record ReturnEntitlementInput(ImmutableArray<ReturnEntitlementLineInput> Lines);
public sealed record IssuedReturnLine(long ReceiptId, long SkuId, long AcceptedBaseUnits, long PreviousBaseUnits, Money PreviousNet,
    Money Net, Money Tax, Money Charges, Money PackagePrice, int BaseUnitsPerPackage, string Rounding);
public sealed record ReturnLineCapacity(IssuedReturnLine Line, long PreviouslyAttributedBaseUnits, Money PreviouslyAttributedNet,
    Money PreviouslyAttributedTax, Money PreviouslyAttributedCharges, long InspectedBaseUnits);
public sealed record ReturnAttributedLine(long ReceiptId, long SkuId, long AttributedBaseUnits, Money Net, Money Tax, Money Charges)
{
    public Money Total => Money.Add(Money.Add(Net, Tax), Charges);
}
public sealed record ReturnEntitlementResult(ImmutableArray<ReturnAttributedLine> Lines, Money Net, Money Tax, Money Charges)
{
    public Money Credit => Money.Add(Money.Add(Net, Tax), Charges);
}

/// <summary>The owner supplies verified issued lines, exact inspected quantities and all prior financial claims.
/// Attribution uses the frozen issued price and rounding; tax and fees require explicitly approved component rules.</summary>
public static class ReturnEntitlementPolicy
{
    public const int MaximumLines = 100;
    public static ReturnEntitlementInput Copy(ReturnEntitlementInput input) {
        if (input is null || input.Lines.IsDefaultOrEmpty || input.Lines.Length > MaximumLines ||
            input.Lines.Any(line => line is null || line.ReceiptId <= 0 || line.SkuId <= 0) ||
            input.Lines.Select(line => (line.ReceiptId, line.SkuId)).Distinct().Count() != input.Lines.Length) throw Invalid();
        foreach (var line in input.Lines) { _ = new Money(line.Tax.Value); _ = new Money(line.Charges.Value); }
        return input with { Lines = [.. input.Lines.OrderBy(line => line.ReceiptId).ThenBy(line => line.SkuId)] };
    }
    public static ReturnEntitlementResult Review(ImmutableArray<ReturnLineCapacity> capacities, ReturnEntitlementInput input,
        string netRule, string componentRule) {
        input = Copy(input);
        if (netRule != "tail_backout" || componentRule != "manual_component_allocation") throw Invalid("return_entitlement_policy_unavailable");
        if (capacities.IsDefault || capacities.Length != input.Lines.Length || capacities.Any(capacity => capacity is null || capacity.Line is null ||
                capacity.Line.ReceiptId <= 0 || capacity.Line.SkuId <= 0) ||
            capacities.Select(capacity => (capacity.Line.ReceiptId, capacity.Line.SkuId)).Distinct().Count() != capacities.Length) throw Invalid();
        var lines = ImmutableArray.CreateBuilder<ReturnAttributedLine>();
        var net = new Money(0m); var tax = new Money(0m); var charges = new Money(0m);
        foreach (var requested in input.Lines) {
            var capacity = capacities.SingleOrDefault(value => value.Line.ReceiptId == requested.ReceiptId && value.Line.SkuId == requested.SkuId) ?? throw Invalid();
            var issued = capacity.Line;
            _ = new Money(issued.PreviousNet.Value); _ = new Money(issued.Net.Value); _ = new Money(issued.Tax.Value); _ = new Money(issued.Charges.Value);
            _ = new Money(capacity.PreviouslyAttributedNet.Value); _ = new Money(capacity.PreviouslyAttributedTax.Value); _ = new Money(capacity.PreviouslyAttributedCharges.Value);
            if (issued.AcceptedBaseUnits <= 0 || issued.PreviousBaseUnits < 0 || issued.PreviousBaseUnits > long.MaxValue - issued.AcceptedBaseUnits ||
                capacity.PreviouslyAttributedBaseUnits < 0 || capacity.PreviouslyAttributedBaseUnits > issued.AcceptedBaseUnits) throw Invalid("return_entitlement_history_invalid");
            var end = checked(issued.PreviousBaseUnits + issued.AcceptedBaseUnits);
            var previous = Cumulative(issued, issued.PreviousBaseUnits); var original = Cumulative(issued, end);
            if (issued.PreviousNet != previous || issued.Net.Value != original.Value - previous.Value ||
                capacity.PreviouslyAttributedNet.Value != original.Value - Cumulative(issued, end - capacity.PreviouslyAttributedBaseUnits).Value ||
                capacity.PreviouslyAttributedTax.Value > issued.Tax.Value || capacity.PreviouslyAttributedCharges.Value > issued.Charges.Value ||
                capacity.PreviouslyAttributedBaseUnits == 0 && (capacity.PreviouslyAttributedTax.Value != 0 || capacity.PreviouslyAttributedCharges.Value != 0)) throw Invalid("return_entitlement_history_invalid");
            if (capacity.InspectedBaseUnits <= 0 || capacity.InspectedBaseUnits > issued.AcceptedBaseUnits - capacity.PreviouslyAttributedBaseUnits ||
                requested.Tax.Value > issued.Tax.Value - capacity.PreviouslyAttributedTax.Value ||
                requested.Charges.Value > issued.Charges.Value - capacity.PreviouslyAttributedCharges.Value) throw Invalid("return_entitlement_capacity_exceeded");
            var retained = end - capacity.PreviouslyAttributedBaseUnits - capacity.InspectedBaseUnits;
            var attributed = new Money(original.Value - Cumulative(issued, retained).Value - capacity.PreviouslyAttributedNet.Value);
            var line = new ReturnAttributedLine(issued.ReceiptId, issued.SkuId, capacity.InspectedBaseUnits, attributed, requested.Tax, requested.Charges);
            lines.Add(line); net = Money.Add(net, line.Net); tax = Money.Add(tax, line.Tax); charges = Money.Add(charges, line.Charges);
        }
        var result = new ReturnEntitlementResult(lines.ToImmutable(), net, tax, charges);
        if (result.Credit.Value <= 0) throw Invalid("return_entitlement_amount_invalid");
        return result;
    }
    private static Money Cumulative(IssuedReturnLine line, long baseUnits) => baseUnits == 0 ? new(0m) :
        InvoicePolicy.AcceptedNet(line.PackagePrice, line.BaseUnitsPerPackage, baseUnits, line.Rounding);
    public static DomainValidationException Invalid(string code = "invalid_return_entitlement") =>
        new(code, "Review the exact inspected return, original issued amounts and approved attribution rules");
}
