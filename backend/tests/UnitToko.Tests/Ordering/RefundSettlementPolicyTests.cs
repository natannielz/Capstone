using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Tests.Ordering;

public sealed class RefundSettlementPolicyTests
{
    private static PaymentPartyKey Party => new(new(10), new(100), new(101), new(103));
    private static RefundPayoutPayingAccount Account => new(50, 51, new(100, 101, 103, "IDR"));
    private static RefundSettlementPayout Payout => new(new(60), new(61), Party, new(300m), Account, 1);
    private static PersonEvidence Person(long user, long person) => new(user, person, PersonVerification.Verified, 1);
    private static ImmutableArray<ReturnFinancialLinkDelta> Releases => [new(new(70), new(80), new(1000m), new(0m), new(300m), new(300m))];
    private static ImmutableArray<ReceiptFunds> Receipts => [new(new(80), 2, Party, "IDR", new(1000m), new(700m), new(300m))];
    private static RefundSettlementDebit Debit {
        get {
            var physical = new string('a', 64); var format = new BankStatementFormat(",", "yyyy-MM-dd", ".", "C", "D", BankStatementParser.Fields, "bank-ledger");
            var identity = BankStatementParser.Candidate(physical, format.IdentityNamespace!, "bank-debit-1");
            return new(Account, physical, format, new(2, "bank-debit-1", new(2026, 10, 2), "debit", new(300m), null, null, identity), 1, 2);
        }
    }
    private static RefundSettlementResult Review(RefundSettlementPayout? payout = null, ImmutableArray<ReturnFinancialLinkDelta>? releases = null,
        ImmutableArray<ReceiptFunds>? receipts = null, ImmutableArray<RefundSettlementPaid>? paid = null,
        RefundSettlementDebit? debit = null, PersonEvidence? verifier = null, PersonEvidence? recorder = null, PersonEvidence? importer = null) =>
        RefundSettlementPolicy.Review(payout ?? Payout, releases ?? Releases, receipts ?? Receipts, paid ?? [], debit ?? Debit,
            verifier ?? Person(3, 103), recorder ?? Person(1, 101), importer ?? Person(2, 102));
    [Fact]
    public void Confirmed_payment_advances_receipt_version_without_reopening_blocked_funds() {
        var result = Review(); var receipt = Assert.Single(result.Receipts);
        Assert.Equal(300m, result.Amount.Value); Assert.Equal(Debit.Row.IdentityCandidate, result.CanonicalIdentity);
        Assert.Equal(Receipts[0] with { Version = 3 }, receipt.After);
        Assert.Equal(new Money(0m), receipt.Available); Assert.Equal(new Money(0m), receipt.Pending);
        Assert.Equal(new Money(0m), receipt.PaidBefore); Assert.Equal(new Money(300m), receipt.PaidAfter);
        Assert.Equal(300m, receipt.After.ReservedForRefund.Value); Assert.Equal(700m, receipt.After.Allocated.Value);
    }
    [Fact]
    public void Existing_paid_reservations_remain_blocked_and_reduce_only_pending_capacity() {
        var prior = new RefundSettlementPaid(new(90), new(91), Party, new string('b', 64), new(100m),
            [new(new(92), new(80), new(1000m), new(0m), new(100m), new(100m))]);
        var receipt = Receipts[0] with { Allocated = new(600m), ReservedForRefund = new(400m) };
        var result = Assert.Single(Review(receipts: [receipt], paid: [prior]).Receipts);
        Assert.Equal(receipt with { Version = 3 }, result.After); Assert.Equal(100m, result.PaidBefore.Value);
        Assert.Equal(400m, result.PaidAfter.Value); Assert.Equal(0m, result.Pending.Value); Assert.Equal(0m, result.Available.Value);
        Assert.Equal("refund_settlement_exceeds_reserved", Assert.Throws<DomainValidationException>(() => Review(paid: [prior])).Code);
    }
    [Fact]
    public void Already_confirmed_payout_approval_or_canonical_debit_cannot_pay_twice() {
        var prior = new RefundSettlementPaid(new(90), new(91), Party, new string('b', 64), new(300m), Releases);
        foreach (var conflict in new[] { prior with { PayoutId = Payout.PayoutId }, prior with { ApprovalId = Payout.ApprovalId }, prior with { CanonicalIdentity = Debit.Row.IdentityCandidate! } })
            Assert.Equal("refund_settlement_already_confirmed", Assert.Throws<DomainValidationException>(() => Review(paid: [conflict])).Code);
        Assert.Throws<DomainValidationException>(() => Review(paid: [prior, prior]));
    }
    [Fact]
    public void Credit_ambiguous_candidate_wrong_amount_and_changed_identity_are_denied() {
        foreach (var invalid in new[] { Debit with { Row = Debit.Row with { Direction = "credit" } }, Debit with { CandidateOccurrences = 2 },
            Debit with { Row = Debit.Row with { Amount = new(299m) } }, Debit with { Row = Debit.Row with { IdentityCandidate = new string('c', 64) } },
            Debit with { Format = Debit.Format with { IdentityNamespace = null } }, Debit with { PayingAccount = Account with { VersionId = 99 } } })
            Assert.Throws<DomainValidationException>(() => Review(debit: invalid));
    }
    [Fact]
    public void Wrong_release_vector_party_receipt_and_exhausted_version_are_denied() {
        Assert.Throws<DomainValidationException>(() => Review(releases: [Releases[0] with { Amount = new(299m), ReleasedAfter = new(299m) }]));
        Assert.Throws<DomainValidationException>(() => Review(releases: [Releases[0], Releases[0]]));
        Assert.Throws<DomainValidationException>(() => Review(receipts: [Receipts[0] with { ReceiptId = new(81) }]));
        Assert.Throws<DomainValidationException>(() => Review(receipts: [Receipts[0] with { Party = Party with { BuyingOrganizationId = new(11) } }]));
        Assert.Throws<DomainValidationException>(() => Review(receipts: [Receipts[0] with { Version = long.MaxValue - 2 }]));
    }
    [Fact]
    public void Recorder_importer_and_equivalent_humans_cannot_verify_their_own_payment() {
        foreach (var verifier in new[] { Person(1, 101), Person(2, 102), Person(3, 101), Person(3, 102), new(3, null, PersonVerification.Unknown, 1) })
            Assert.Equal("independent_person_required", Assert.Throws<DomainValidationException>(() => Review(verifier: verifier)).Code);
        Assert.Throws<DomainValidationException>(() => Review(recorder: Person(4, 101)));
        Assert.Throws<DomainValidationException>(() => Review(importer: Person(4, 102)));
    }
}
