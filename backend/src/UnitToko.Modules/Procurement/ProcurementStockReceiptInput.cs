using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementStockReceiptInput(ProcurementLocation Location, long OrderId, long OrderVersion, long OrderApprovalId,
    long InspectionId, int InspectionLineNumber, int Packages, long PoolId, long PoolRevision,
    string ReceiptReference, string LotReference, DateOnly? ExpiresOn, string Reason);

public static class ProcurementStockReceiptRules
{
    public static ProcurementStockReceiptInput Copy(ProcurementStockReceiptInput input) {
        if (input is null || input.OrderId <= 0 || input.OrderVersion != 1 || input.OrderApprovalId <= 0 || input.InspectionId <= 0 ||
            input.InspectionLineNumber is < 1 or > 100 || input.Packages is < 1 or > DomainValues.MaximumLineQuantity ||
            input.PoolId <= 0 || input.PoolRevision is <= 0 or >= long.MaxValue - 1 || input.ExpiresOn is { Year: < 2000 }) throw Invalid();
        ProcurementRequestRules.Location(input.Location); Text(input.ReceiptReference, 128); Text(input.LotReference, 128); Text(input.Reason, 1000);
        return input with { Location = input.Location with { } };
    }
    public static void Text(string? value, int maximum) { if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid(); }
    public static DomainValidationException Invalid() => new("invalid_procurement_stock_receipt", "Select actual inspected packages and a reviewed stock source for held receiving");
}
