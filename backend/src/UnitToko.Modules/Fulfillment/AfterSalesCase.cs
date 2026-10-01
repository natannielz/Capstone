using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Fulfillment;

public sealed record AfterSalesCaseLine(long ReceiptId, long SkuId, long ReportedBaseUnits);
public sealed record AfterSalesCaseInput(long InvoiceId, string Binding, string Issue, ImmutableArray<AfterSalesCaseLine> Lines);
public static class AfterSalesCasePolicy
{
    public const int MaximumLines = 100;
    public static AfterSalesCaseInput Copy(AfterSalesCaseInput input) {
        if (input is null || input.InvoiceId <= 0 || input.Binding is null || input.Binding.Length != 64 || input.Binding.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            input.Issue is not ("wrong_item" or "damaged" or "missing" or "other") || input.Lines.IsDefaultOrEmpty || input.Lines.Length > MaximumLines ||
            input.Lines.Any(line => line is null || line.ReceiptId <= 0 || line.SkuId <= 0 || line.ReportedBaseUnits is <= 0 or > DomainValues.MaximumLineQuantity) ||
            input.Lines.Select(line => (line.ReceiptId, line.SkuId)).Distinct().Count() != input.Lines.Length) throw Invalid();
        return input with { Lines = [.. input.Lines.OrderBy(line => line.ReceiptId).ThenBy(line => line.SkuId).Select(line => line with { })] };
    }
    public static DomainValidationException Invalid() => new("invalid_after_sales_case", "Select the finalized sale, actual item and reported quantity before requesting a case");
}
