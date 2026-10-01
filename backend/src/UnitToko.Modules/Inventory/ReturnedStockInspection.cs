using System.Collections.Immutable;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;
public sealed record ReturnedStockInspectionLine(long ReceiptId, long SkuId, long InspectedBaseUnits, string Identity, string Condition, string ObservedIdentity, string? ObservedLotLabel);
public sealed record ReturnedStockInspectionInput(string PublicNote, ImmutableArray<ReturnedStockInspectionLine> Lines);
public static class ReturnedStockInspectionPolicy
{
    public static ReturnedStockInspectionInput Copy(ReturnedStockInspectionInput input) {
        if (input is null || !ReceivingAccountPolicy.Text(input.PublicNote, 1000) || input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100 || input.Lines.Any(line => line is null ||
            line.ReceiptId <= 0 || line.SkuId <= 0 || line.InspectedBaseUnits is <= 0 or > DomainValues.MaximumLineQuantity || line.Identity is not ("matched" or "different" or "unverified") ||
            line.Condition is not ("sealed" or "opened" or "damaged" or "mixed") || !ReceivingAccountPolicy.Text(line.ObservedIdentity, 160) || line.ObservedLotLabel is { } lot && !ReceivingAccountPolicy.Text(lot, 160)) ||
            input.Lines.Select(line => (line.ReceiptId, line.SkuId)).Distinct().Count() != input.Lines.Length) throw Invalid();
        return input with { Lines = [.. input.Lines.OrderBy(line => line.ReceiptId).ThenBy(line => line.SkuId).Select(line => line with { })] };
    }
    public static void RequireReceipt(ReturnedStockInspectionInput input, ReturnedStockInput receipt) {
        input = Copy(input); receipt = ReturnedStockPolicy.Copy(receipt);
        if (input.Lines.Length != receipt.Lines.Length || input.Lines.Any(line => !receipt.Lines.Any(received => received.ReceiptId == line.ReceiptId && received.SkuId == line.SkuId && received.ReceivedBaseUnits == line.InspectedBaseUnits))) throw Invalid();
    }
    public static DomainValidationException Invalid() => new("invalid_return_inspection", "Inspect the exact received base units and record physical identity and condition; goods remain quarantined");
}
