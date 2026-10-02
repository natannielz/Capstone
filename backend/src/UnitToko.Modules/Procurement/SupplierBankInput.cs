using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record SupplierBankInput(SupplierOwner Owner, long SupplierId, long SupplierVersionId, long SupplierVersion,
    long BaseVersion, long PolicyId, long PolicyRevision, ReceivingAccountDetails Details);
public enum SupplierBankDecision { Approve, Reject }
public static class SupplierBankRules
{
    public static SupplierBankInput Copy(SupplierBankInput input) {
        if (input is null || input.SupplierId <= 0 || input.SupplierVersionId <= 0 || input.SupplierVersion <= 0 ||
            input.BaseVersion is < 0 or >= long.MaxValue - 1 || input.PolicyId <= 0 || input.PolicyRevision <= 0) throw Invalid();
        SupplierRules.Owner(input.Owner); return input with { Owner = input.Owner with { }, Details = ReceivingAccountPolicy.Copy(input.Details) };
    }
    public static DomainValidationException Invalid() => new("invalid_supplier_bank", "Choose the approved supplier, exact bank details and independent bank verification policy");
}
