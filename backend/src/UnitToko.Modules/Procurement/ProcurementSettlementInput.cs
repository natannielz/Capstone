using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementSettlementInput(long PolicyId, long PolicyRevision, long BankImportId, int BankRowNumber,
    ReceivingAccountDetails ObservedBeneficiary, string BeneficiaryVerificationReference, string TransferVerificationReference);

public static class ProcurementSettlementRules
{
    public static ProcurementSettlementInput Copy(ProcurementSettlementInput value) {
        if (value is null || value.PolicyId <= 0 || value.PolicyRevision <= 0 || value.BankImportId <= 0 ||
            value.BankRowNumber < 2 || value.BankRowNumber > BankStatementParser.MaximumRows + 1 ||
            !ReceivingAccountPolicy.Text(value.BeneficiaryVerificationReference, 256) || !ReceivingAccountPolicy.Text(value.TransferVerificationReference, 256)) throw Invalid();
        return value with { ObservedBeneficiary = ReceivingAccountPolicy.Copy(value.ObservedBeneficiary) };
    }
    public static DomainValidationException Invalid(string code = "procurement_settlement_invalid") => new(code, "Review the exact recorded advance against an independently verified bank debit");
}
