using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementDisbursementInput(ProcurementLocation Location, long FundingRequestId, long FundingDecisionId, long AuthorizationId,
    long SupplierId, long BeneficiaryVersionId, long BeneficiaryVersion, long PayingAccountId, long PayingAccountVersionId,
    long PolicyId, long PolicyRevision, Money Amount, DateOnly TransferDate, string TransferReference);

public static class ProcurementDisbursementRules
{
    public static ProcurementDisbursementInput Copy(ProcurementDisbursementInput input) {
        if (input is null) throw Invalid(); ProcurementRequestRules.Location(input.Location);
        if (new[] { input.FundingRequestId, input.FundingDecisionId, input.AuthorizationId, input.SupplierId, input.BeneficiaryVersionId, input.BeneficiaryVersion,
                input.PayingAccountId, input.PayingAccountVersionId, input.PolicyId, input.PolicyRevision }.Any(id => id <= 0) ||
            input.Amount.Value <= 0 || input.TransferDate.Year < 2000 || !ReceivingAccountPolicy.Text(input.TransferReference, 160)) throw Invalid();
        return input with { Location = input.Location with { }, Amount = Money.Parse(input.Amount.ToString()) };
    }
    public static DomainValidationException Invalid(string code = "procurement_disbursement_invalid") => new(code, "Select an approved advance and verified accounts, and enter the actual transfer and private evidence");
}
