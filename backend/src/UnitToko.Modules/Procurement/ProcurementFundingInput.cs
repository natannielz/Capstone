using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementFundingInput(ProcurementLocation Location, long OrderId, long OrderVersion, long OrderApprovalId,
    long PolicyId, long PolicyRevision, Money Amount, DateOnly BusinessDate, DateOnly NeededBy);
public enum ProcurementFundingDecision { Approve, Reject }
public static class ProcurementFundingRules
{
    public static ProcurementFundingInput Copy(ProcurementFundingInput input) {
        if (input is null || input.OrderId <= 0 || input.OrderVersion != 1 || input.OrderApprovalId <= 0 || input.PolicyId <= 0 || input.PolicyRevision <= 0 ||
            input.Amount.Value <= 0 || input.BusinessDate == DateOnly.MinValue || input.NeededBy < input.BusinessDate) throw Invalid();
        ProcurementRequestRules.Location(input.Location); _ = new Money(input.Amount.Value); return input with { Location = input.Location with { } };
    }
    public static DomainValidationException Invalid() => new("invalid_procurement_funding", "Choose an approved purchase order, funding policy, explicit advance amount and dates");
}
