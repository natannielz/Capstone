using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementStockValuationInput(ProcurementLocation Location, long ReceiptId, long ReceiptRevision,
    long InvoiceDocumentId, long InvoiceDocumentRevision, long InvoiceMatchId, int MatchLineNumber, int Packages,
    Money NetAmount, Money TaxAmount, Money ChargeAmount, Money CapitalizedAmount,
    long PolicyId, long PolicyRevision, DateOnly PostingDate, string Reason);

public enum ProcurementStockValuationDecision { Approve, Reject }

public static class ProcurementStockValuationRules
{
    public static ProcurementStockValuationInput Copy(ProcurementStockValuationInput input)
    {
        if (input is null || input.ReceiptId <= 0 || input.ReceiptRevision != 1 || input.InvoiceDocumentId <= 0 ||
            input.InvoiceDocumentRevision != 1 || input.InvoiceMatchId <= 0 || input.MatchLineNumber is < 1 or > 100 ||
            input.Packages is < 1 or > DomainValues.MaximumLineQuantity || input.PolicyId <= 0 ||
            input.PolicyRevision is <= 0 or >= long.MaxValue - 1 || input.PostingDate.Year < 2000 ||
            input.NetAmount.Value <= 0 || input.TaxAmount.Value < 0 || input.ChargeAmount.Value < 0 ||
            input.CapitalizedAmount.Value <= 0) throw Invalid();
        ProcurementRequestRules.Location(input.Location);
        ProcurementStockReceiptRules.Text(input.Reason, 1000);
        _ = new Money(input.NetAmount.Value); _ = new Money(input.TaxAmount.Value);
        _ = new Money(input.ChargeAmount.Value); _ = new Money(input.CapitalizedAmount.Value);
        return input with { Location = input.Location with { } };
    }

    public static DomainValidationException Invalid(string code = "invalid_procurement_stock_valuation") =>
        new(code, "Review the exact held receipt, matched invoice components, source recognition and accounting policy");
}
