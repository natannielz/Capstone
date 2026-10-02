using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

public sealed record RefundPayoutInput(long BeneficiaryId, long ExpectedBeneficiaryRevision, long PayingAccountId, long PayingAccountVersionId,
    Money Amount, DateOnly TransferDate, string TransferReference);
public sealed record RefundPayoutBeneficiary(long Id, long Revision, long BuyingOrganizationId, string Currency);
public sealed record RefundPayoutPayingAccount(long Id, long VersionId, OperationalBookKey Book);
public sealed record RefundPayoutResult(Money Amount, RefundPayoutInput Input);

/// <summary>The owner supplies a finalized original refund approval, independently verified beneficiary and bank source.
/// Recording binds evidence to the exact reserved amount; it neither settles money nor proves a bank debit.</summary>
public static class RefundPayoutPolicy
{
    public const string ExactReservedRefund = "exact_reserved_refund";
    public static RefundPayoutInput Copy(RefundPayoutInput input) {
        if (input is null || input.BeneficiaryId <= 0 || input.ExpectedBeneficiaryRevision is <= 0 or >= long.MaxValue ||
            input.PayingAccountId <= 0 || input.PayingAccountVersionId <= 0 || input.Amount.Value <= 0 || input.TransferDate.Year < 2000 ||
            !ReceivingAccountPolicy.Text(input.TransferReference, 160)) throw Invalid();
        _ = new Money(input.Amount.Value);
        return input with { Amount = Money.Parse(input.Amount.ToString()) };
    }
    public static RefundPayoutResult Review(Money reservedRefund, PaymentPartyKey party, string currency, RefundPayoutBeneficiary beneficiary,
        RefundPayoutPayingAccount paying, RefundPayoutInput input, string rule, DateOnly businessDate) {
        input = Copy(input); _ = new Money(reservedRefund.Value);
        if (rule != ExactReservedRefund) throw Invalid("refund_payout_policy_unavailable");
        if (reservedRefund.Value <= 0 || input.Amount != reservedRefund) throw Invalid("refund_payout_amount_invalid");
        if (party is null || party.BuyingOrganizationId.Value <= 0 || party.FulfillmentOrganizationId.Value <= 0 || party.ShopId.Value <= 0 || party.ChannelId.Value <= 0 ||
            currency != "IDR" || beneficiary is null || beneficiary.Id <= 0 || beneficiary.Revision is <= 0 or >= long.MaxValue || beneficiary.BuyingOrganizationId <= 0 ||
            beneficiary.Currency != currency || beneficiary.BuyingOrganizationId != party.BuyingOrganizationId.Value || paying is null || paying.Id <= 0 || paying.VersionId <= 0 ||
            paying.Book is null || paying.Book.OrganizationId != party.FulfillmentOrganizationId.Value || paying.Book.ShopId != party.ShopId.Value ||
            paying.Book.ChannelId != party.ChannelId.Value || paying.Book.Currency != currency) throw Invalid("refund_payout_source_invalid");
        if (beneficiary.Id != input.BeneficiaryId || beneficiary.Revision != input.ExpectedBeneficiaryRevision ||
            paying.Id != input.PayingAccountId || paying.VersionId != input.PayingAccountVersionId) throw Invalid("refund_payout_stale");
        if (businessDate.Year < 2000 || input.TransferDate > businessDate) throw Invalid("refund_payout_transfer_date_invalid");
        return new(input.Amount, input);
    }
    public static DomainValidationException Invalid(string code = "refund_payout_invalid") =>
        new(code, "Review the exact reserved refund, approved beneficiary, paying account and manual transfer evidence");
}
