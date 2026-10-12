using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Ordering;

public enum OrderSubstitutionDecision { Accept, Decline }
public sealed record OrderSubstitutionSelection(LongId OriginalSkuId, int OriginalPackages, OrderPackageSelection Replacement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long OriginalLineRevision = 0);
public sealed record OrderSubstitutionInput(LongId OrderId, LongId SuborderId, long ExpectedOrderRevision,
    long ExpectedDispatchSequence, OrderSubstitutionSelection Selection);
public sealed record OrderSubstitutionChange(OrderCatalogQuote Catalog, int ReleasePackages, int ReservePackages, Money RemovedCatalogAmount);

/// <summary>Exact demand amendment only. The application owner must authenticate the current
/// reservation history, replacement catalog, commercial terms and independent buyer command,
/// then release and reserve in one transaction. Proposals and declined offers have no effects.</summary>
public static class OrderSubstitutionPolicy
{
    public static bool WritesReservation(OrderSubstitutionDecision decision) => decision switch {
        OrderSubstitutionDecision.Accept => true, OrderSubstitutionDecision.Decline => false, _ => throw Invalid() };

    public static void Validate(OrderSubstitutionInput input) {
        if (input is null || input.OrderId.Value <= 0 || input.SuborderId.Value <= 0 || input.ExpectedOrderRevision <= 0 ||
            input.ExpectedDispatchSequence < 0 || input.Selection is null || input.Selection.OriginalSkuId.Value <= 0 ||
            input.Selection.Replacement is not { } replacement || replacement.SkuId.Value <= 0 || replacement.DefinitionId.Value <= 0 ||
            replacement.DefinitionVersion <= 0 || replacement.PriceId.Value <= 0 || replacement.PriceVersion <= 0) throw Invalid();
        new OrderLineIdentity(input.Selection.OriginalSkuId, input.Selection.OriginalLineRevision).Validate();
        DomainValues.ParseQuantity(input.Selection.OriginalPackages); DomainValues.ParseQuantity(replacement.Packages);
    }

    public static void RequireReview(string expected, string actual) {
        if (expected is null || actual is null || expected.Length != 64 || expected.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            !string.Equals(expected, actual, StringComparison.Ordinal)) throw Invalid("order_substitution_review_changed");
    }

    public static void RequireApprovalCapacity(long currentRevision, int suborders, OrderSpendingState renewedSpending) {
        // Keep room for every renewed store decision, independent spending when needed,
        // and a final cancel-remainder command. Consent must never strand an unapprovable order.
        if (currentRevision is < 1 or > 102 || suborders is < 1 or > 100 ||
            renewedSpending is not (OrderSpendingState.Pending or OrderSpendingState.NotRequired) ||
            currentRevision + 1L + suborders + (renewedSpending == OrderSpendingState.Pending ? 1L : 0L) + 1L > 103)
            throw Invalid("order_substitution_history_limit");
    }

    public static OrderSubstitutionChange Replace(OrderCatalogQuote accepted, OrderLineProgress originalProgress,
        OrderSubstitutionSelection selected, OrderQuotedLine replacement, long newLineRevision) {
        if (accepted is null || accepted.Lines.IsDefaultOrEmpty || accepted.Lines.Length > 100 || selected is null || selected.Replacement is null ||
            replacement is null || replacement.Definition is null || replacement.Price is null ||
            accepted.Lines.Any(line => line is null || line.Definition is null || line.Price is null) ||
            accepted.Lines.Select(OrderLineIdentity.Of).Distinct().Count() != accepted.Lines.Length || newLineRevision is < 2 or > 103 ||
            accepted.Lines.Any(line => line.LineRevision >= newLineRevision)) throw Invalid();
        _ = OrderLinePolicy.Unallocated(originalProgress);
        DomainValues.ParseQuantity(selected.OriginalPackages);
        var originalKey = new OrderLineIdentity(selected.OriginalSkuId, selected.OriginalLineRevision).Validate();
        var original = accepted.Lines.SingleOrDefault(line => OrderLineIdentity.Of(line) == originalKey) ?? throw Invalid();
        var choice = selected.Replacement;
        if (originalProgress.Requested != original.Packages || selected.OriginalPackages > originalProgress.Reserved ||
            replacement.Definition.SkuId != choice.SkuId || replacement.Definition.Id != choice.DefinitionId || replacement.Definition.Version != choice.DefinitionVersion ||
            replacement.Price.Id != choice.PriceId || replacement.Price.Version != choice.PriceVersion || replacement.Packages != choice.Packages ||
            replacement.Price.Currency != accepted.Currency || replacement.FulfillmentOwner != original.FulfillmentOwner || replacement.LineRevision != 0) throw Invalid();
        // A replacement is new demand and obeys current minimum/increment rules. A retained
        // original fragment can be smaller after partial dispatch; it is never re-submitted.
        _ = OrderFulfillmentPolicy.Split(new([replacement], accepted.Currency, replacement.CatalogSubtotal));
        var sum = new Money(0);
        foreach (var line in accepted.Lines) {
            CatalogPricePolicy.Validate(line.Price); DomainValues.ParseQuantity(line.Packages);
            if (line.Price.SkuId != line.Definition.SkuId || line.Price.Currency != accepted.Currency ||
                line.BaseUnits != CatalogPolicy.BaseUnitsForPackages(line.Definition, line.Packages) ||
                line.CatalogSubtotal != Money.Multiply(line.Price.UnitPrice, line.Packages)) throw Invalid();
            sum = Money.Add(sum, line.CatalogSubtotal);
        }
        if (sum != accepted.CatalogSubtotal) throw Invalid();
        var retained = original.Packages - selected.OriginalPackages;
        var lines = ImmutableArray.CreateBuilder<OrderQuotedLine>();
        foreach (var line in accepted.Lines) {
            if (OrderLineIdentity.Of(line) != originalKey) lines.Add(line);
            else if (retained > 0) lines.Add(line with { Packages = retained,
                BaseUnits = CatalogPolicy.BaseUnitsForPackages(line.Definition, retained), CatalogSubtotal = Money.Multiply(line.Price.UnitPrice, retained) });
        }
        lines.Add(replacement with { LineRevision = newLineRevision });
        if (lines.Count > 100) throw Invalid();
        return new(new(lines.ToImmutable(), accepted.Currency, lines.Aggregate(new Money(0), (total, line) => Money.Add(total, line.CatalogSubtotal))),
            selected.OriginalPackages, replacement.Packages, Money.Multiply(original.Price.UnitPrice, selected.OriginalPackages));
    }
    private static DomainValidationException Invalid(string code = "invalid_order_substitution") =>
        new(code, "Review an exact replacement for currently reserved, unshipped demand in the same fulfillment scope");
}
