using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public enum ProcurementSupplierPayableKind { Recognize, ApplyAdvance }
public enum ProcurementSupplierPayableDecision { Approve, Reject }

public sealed record ProcurementSupplierPayableInput(
    ProcurementSupplierPayableKind Kind, ProcurementLocation Location, long? ValuationAllocationId,
    long? PayableId, long? SettlementId, Money? Amount, long PolicyId, long PolicyRevision,
    DateOnly PostingDate, string Reason);

public static class ProcurementSupplierPayableRules
{
    public static ProcurementSupplierPayableInput Copy(ProcurementSupplierPayableInput input)
    {
        if (input is null || !Enum.IsDefined(input.Kind) || input.PolicyId <= 0 || input.PolicyRevision <= 0) throw Invalid();
        ProcurementRequestRules.Location(input.Location);
        ProcurementRequestRules.Text(input.Reason, 1000);
        AccountingCalendar.ValidateMonth($"{input.PostingDate.Year:D4}-{input.PostingDate.Month:D2}");
        if (input.Kind == ProcurementSupplierPayableKind.Recognize &&
            (input.ValuationAllocationId is not > 0 || input.PayableId is not null || input.SettlementId is not null || input.Amount is not null) ||
            input.Kind == ProcurementSupplierPayableKind.ApplyAdvance &&
            (input.ValuationAllocationId is not null || input.PayableId is not > 0 || input.SettlementId is not > 0 ||
             input.Amount is not { Value: > 0 })) throw Invalid();
        return input with { Location = input.Location with { } };
    }

    public static DomainValidationException Invalid(string code = "procurement_supplier_payable_invalid") =>
        new(code, "Review an exact approved valuation or a bounded verified advance application");
}
