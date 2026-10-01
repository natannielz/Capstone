using System.Collections.Immutable;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;
/// <summary>Physical custody in base units. An observed package condition never authorizes resale or a financial remedy.</summary>
public sealed record ReturnedStockLine(long ReceiptId, long SkuId, long ReceivedBaseUnits, string ObservedCondition);
public sealed record ReturnedStockInput(string PublicNote, ImmutableArray<ReturnedStockLine> Lines);
public static class ReturnedStockPolicy
{
    public static ReturnedStockInput Copy(ReturnedStockInput input) {
        if (input is null || !ReceivingAccountPolicy.Text(input.PublicNote, 1000) || input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100 ||
            input.Lines.Any(line => line is null || line.ReceiptId <= 0 || line.SkuId <= 0 || line.ReceivedBaseUnits is < 1 or > DomainValues.MaximumLineQuantity ||
                line.ObservedCondition is not ("uninspected" or "sealed" or "opened" or "damaged")) || input.Lines.Select(line => (line.ReceiptId, line.SkuId)).Distinct().Count() != input.Lines.Length) throw Invalid();
        return input with { Lines = [.. input.Lines.OrderBy(line => line.ReceiptId).ThenBy(line => line.SkuId).Select(line => line with { })] };
    }
    public static DomainValidationException Invalid() => new("invalid_returned_stock", "Return intake requires actual positive base quantities and an observed condition");
}
