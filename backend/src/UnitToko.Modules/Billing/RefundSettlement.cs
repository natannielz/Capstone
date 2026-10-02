using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Modules.Billing;

public sealed record RefundSettlementInput(long BankImportId, int BankRowNumber, string BeneficiaryVerificationReference, string TransferVerificationReference);
public sealed record RefundSettlementPayout(LongId PayoutId, LongId ApprovalId, PaymentPartyKey Party, Money Amount,
    RefundPayoutPayingAccount PayingAccount, long RecordedBy);
public sealed record RefundSettlementDebit(RefundPayoutPayingAccount PayingAccount, string PhysicalIdentity,
    BankStatementFormat Format, BankStatementRow Row, int CandidateOccurrences, long ImportedBy);
public sealed record RefundSettlementPaid(LongId PayoutId, LongId ApprovalId, PaymentPartyKey Party, string CanonicalIdentity,
    Money Amount, ImmutableArray<ReturnFinancialLinkDelta> Releases);
public sealed record RefundSettlementReceipt(ReceiptFunds Before, ReceiptFunds After, Money PaidBefore, Money PaidAfter,
    Money Pending, Money Available);
public sealed record RefundSettlementResult(string CanonicalIdentity, Money Amount, ImmutableArray<RefundSettlementReceipt> Receipts);

// The owner must reconstruct and fence these actual sources, then atomically claim the bank
// identity, append the receipt event and post liability Dr / cash Cr. This policy sends no money.
public static class RefundSettlementPolicy
{
    public static RefundSettlementInput Copy(RefundSettlementInput input) {
        if (input is null || input.BankImportId <= 0 || input.BankRowNumber < 2 || input.BankRowNumber > BankStatementParser.MaximumRows + 1 ||
            !ReceivingAccountPolicy.Text(input.BeneficiaryVerificationReference, 256) || !ReceivingAccountPolicy.Text(input.TransferVerificationReference, 256)) throw Invalid();
        return input with { };
    }
    public static RefundSettlementResult Review(RefundSettlementPayout payout, ImmutableArray<ReturnFinancialLinkDelta> releases,
        ImmutableArray<ReceiptFunds> receipts, ImmutableArray<RefundSettlementPaid> paid, RefundSettlementDebit debit,
        PersonEvidence verifier, PersonEvidence recorder, PersonEvidence importer) {
        if (payout is null || payout.PayoutId.Value <= 0 || payout.ApprovalId.Value <= 0 || payout.Party is null ||
            payout.Amount.Value <= 0 || payout.PayingAccount is null || payout.RecordedBy <= 0 || debit is null ||
            debit.PayingAccount != payout.PayingAccount || debit.ImportedBy <= 0 || debit.Row is null || debit.Format is null ||
            !Digest(debit.PhysicalIdentity) || payout.PayingAccount.Id <= 0 || payout.PayingAccount.VersionId <= 0 ||
            payout.PayingAccount.Book != new OperationalBookKey(payout.Party.FulfillmentOrganizationId.Value,
                payout.Party.ShopId.Value, payout.Party.ChannelId.Value, "IDR")) throw Invalid();
        BankStatementParser.Validate(debit.Format);
        var row = debit.Row;
        if (row.Direction != "debit" || row.Number < 2 || !BankStatementParser.Text(row.TransactionId, 80) ||
            debit.Format.IdentityNamespace is null || debit.CandidateOccurrences != 1 || row.Amount != payout.Amount ||
            row.BookedDate.Year < 2000 || row.IdentityCandidate != BankStatementParser.Candidate(debit.PhysicalIdentity,
                debit.Format.IdentityNamespace, row.TransactionId!)) throw Invalid("refund_settlement_bank_mismatch");
        if (recorder is null || importer is null || recorder.UserId != payout.RecordedBy || importer.UserId != debit.ImportedBy ||
            !PersonIndependencePolicy.AreIndependent(verifier, recorder, importer)) throw Invalid("independent_person_required");
        if (ValidateReleases(releases) != payout.Amount || receipts.IsDefaultOrEmpty ||
            receipts.Any(receipt => receipt is null) || receipts.Select(receipt => receipt.ReceiptId).Distinct().Count() != receipts.Length ||
            !receipts.Select(receipt => receipt.ReceiptId.Value).Order().SequenceEqual(releases.Select(line => line.ReceiptId.Value).Distinct().Order()) ||
            paid.IsDefault) throw Invalid();
        var payouts = new HashSet<LongId>(); var approvals = new HashSet<LongId>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        var paidByReceipt = new Dictionary<LongId, Money>();
        foreach (var previous in paid) {
            if (previous is null || previous.PayoutId.Value <= 0 || previous.ApprovalId.Value <= 0 || previous.Party != payout.Party ||
                !Digest(previous.CanonicalIdentity) || !payouts.Add(previous.PayoutId) || !approvals.Add(previous.ApprovalId) ||
                !identities.Add(previous.CanonicalIdentity) || ValidateReleases(previous.Releases) != previous.Amount) throw Invalid();
            foreach (var line in previous.Releases)
                paidByReceipt[line.ReceiptId] = Money.Add(paidByReceipt.GetValueOrDefault(line.ReceiptId, new(0m)), line.Amount);
        }
        if (payouts.Contains(payout.PayoutId) || approvals.Contains(payout.ApprovalId) || identities.Contains(row.IdentityCandidate!))
            throw Invalid("refund_settlement_already_confirmed");
        var result = ImmutableArray.CreateBuilder<RefundSettlementReceipt>();
        foreach (var before in receipts.OrderBy(receipt => receipt.ReceiptId.Value)) {
            var available = PaymentAllocationPolicy.Available(before);
            if (before.Party != payout.Party || before.Version >= long.MaxValue - 2) throw Invalid();
            var paidBefore = paidByReceipt.GetValueOrDefault(before.ReceiptId, new(0m));
            var amount = releases.Where(line => line.ReceiptId == before.ReceiptId).Aggregate(new Money(0m), (sum, line) => Money.Add(sum, line.Amount));
            var paidAfter = Money.Add(paidBefore, amount);
            if (paidAfter.Value > before.ReservedForRefund.Value) throw Invalid("refund_settlement_exceeds_reserved");
            // reservedForRefund is TOTAL blocked funds, including confirmed payouts. Reducing it
            // here would incorrectly make the same money available for another allocation.
            var after = before with { Version = before.Version + 1 };
            if (PaymentAllocationPolicy.Available(after) != available) throw Invalid();
            result.Add(new(before, after, paidBefore, paidAfter, new(before.ReservedForRefund.Value - paidAfter.Value), available));
        }
        return new(row.IdentityCandidate!, payout.Amount, result.ToImmutable());
    }
    private static Money ValidateReleases(ImmutableArray<ReturnFinancialLinkDelta> releases) {
        if (releases.IsDefaultOrEmpty || releases.Length > ReturnFinancialApprovalPolicy.MaximumReleases ||
            releases.Any(line => line is null || line.OriginalAllocationId.Value <= 0 || line.ReceiptId.Value <= 0 || line.Amount.Value <= 0 ||
                line.OriginallyAllocated.Value <= 0 || line.PreviouslyReleased.Value > line.OriginallyAllocated.Value ||
                line.Amount.Value > line.OriginallyAllocated.Value - line.PreviouslyReleased.Value ||
                line.ReleasedAfter != Money.Add(line.PreviouslyReleased, line.Amount)) ||
            releases.Select(line => line.OriginalAllocationId).Distinct().Count() != releases.Length) throw Invalid();
        return releases.Aggregate(new Money(0m), (sum, line) => Money.Add(sum, line.Amount));
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    public static DomainValidationException Invalid(string code = "refund_settlement_source_invalid") =>
        new(code, "Verify the exact reserved refund against an independent, unclaimed bank debit");
}
