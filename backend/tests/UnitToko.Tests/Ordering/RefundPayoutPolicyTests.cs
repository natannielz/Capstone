using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Tests.Ordering;
public sealed class RefundPayoutPolicyTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static PaymentPartyKey Party => new(new(10), new(100), new(101), new(103));
    private static RefundPayoutBeneficiary Beneficiary => new(9007199254740993L, 2, 10, "IDR");
    private static RefundPayoutPayingAccount Paying => new(long.MaxValue, long.MaxValue - 1, new(100, 101, 103, "IDR"));
    private static RefundPayoutInput Input(decimal amount = 1000m) => new(Beneficiary.Id, Beneficiary.Revision, Paying.Id, Paying.VersionId, new(amount), Today, "BANK-TX-001");
    private static RefundPayoutResult Review(RefundPayoutInput input, Money? reserved = null, RefundPayoutBeneficiary? beneficiary = null, RefundPayoutPayingAccount? paying = null, string rule = RefundPayoutPolicy.ExactReservedRefund) =>
        RefundPayoutPolicy.Review(reserved ?? new Money(1000m), Party, "IDR", beneficiary ?? Beneficiary, paying ?? Paying, input, rule, Today);

    [Fact]
    public void Exact_reserved_amount_is_recordable_without_amount_or_bank_identity_reinterpretation() {
        var input = Input(); var result = Review(input);
        Assert.Equal(new Money(1000m), result.Amount); Assert.Equal(input, result.Input);
        Assert.Equal(9007199254740993L, result.Input.BeneficiaryId); Assert.Equal(long.MaxValue, result.Input.PayingAccountId);
        Assert.Equal(Paying.VersionId, result.Input.PayingAccountVersionId); Assert.Equal("BANK-TX-001", result.Input.TransferReference);
    }
    [Fact]
    public void Underpayment_overpayment_and_zero_reservation_cannot_record_an_exact_request() {
        foreach (var amount in new[] { 999.99m, 1000.01m })
            Assert.Equal("refund_payout_amount_invalid", Assert.Throws<DomainValidationException>(() => Review(Input(amount))).Code);
        Assert.Equal("refund_payout_amount_invalid", Assert.Throws<DomainValidationException>(() => Review(Input(), new Money(0m))).Code);
        Assert.Throws<DomainValidationException>(() => RefundPayoutPolicy.Copy(Input(0m)));
    }
    [Fact]
    public void Original_buyer_currency_and_operational_book_must_all_match() {
        Assert.Equal("refund_payout_source_invalid", Assert.Throws<DomainValidationException>(() => Review(Input(), beneficiary: Beneficiary with { BuyingOrganizationId = 11 })).Code);
        Assert.Equal("refund_payout_source_invalid", Assert.Throws<DomainValidationException>(() => Review(Input(), beneficiary: Beneficiary with { Currency = "USD" })).Code);
        foreach (var book in new[] { new OperationalBookKey(999, 101, 103, "IDR"), new(100, 999, 103, "IDR"), new(100, 101, 999, "IDR"), new(100, 101, 103, "USD") })
            Assert.Equal("refund_payout_source_invalid", Assert.Throws<DomainValidationException>(() => Review(Input(), paying: Paying with { Book = book })).Code);
    }
    [Fact]
    public void Changed_beneficiary_or_paying_account_version_requires_fresh_consent() {
        foreach (var input in new[] { Input() with { BeneficiaryId = 3 }, Input() with { ExpectedBeneficiaryRevision = 1 }, Input() with { PayingAccountId = 1 }, Input() with { PayingAccountVersionId = 1 } })
            Assert.Equal("refund_payout_stale", Assert.Throws<DomainValidationException>(() => Review(input)).Code);
    }
    [Fact]
    public void Explicit_route_and_actual_business_date_are_required_without_invented_transfer_deadline() {
        foreach (var rule in new[] { "", "automatic", "partial_transfer" })
            Assert.Equal("refund_payout_policy_unavailable", Assert.Throws<DomainValidationException>(() => Review(Input(), rule: rule)).Code);
        Assert.Equal("refund_payout_transfer_date_invalid", Assert.Throws<DomainValidationException>(() => Review(Input() with { TransferDate = Today.AddDays(1) })).Code);
        Assert.Equal(new DateOnly(2000, 1, 1), Review(Input() with { TransferDate = new(2000, 1, 1) }).Input.TransferDate);
        foreach (var reference in new[] { "", " padded ", "bad\nreference", new string('x', 161) })
            Assert.Throws<DomainValidationException>(() => RefundPayoutPolicy.Copy(Input() with { TransferReference = reference }));
    }
    [Fact]
    public void Tiny_cents_and_maximum_money_remain_exact_and_direct_HTTP_scales_canonicalize() {
        foreach (var amount in new[] { .01m, DomainValues.MaximumMoney }) {
            var result = Review(Input(amount), new Money(amount)); Assert.Equal(new Money(amount), result.Amount);
        }
        var direct = RefundPayoutPolicy.Copy(Input()); var parsed = RefundPayoutPolicy.Copy(Input() with { Amount = Money.Parse("1000.00") });
        Assert.Equal(decimal.GetBits(direct.Amount.Value), decimal.GetBits(parsed.Amount.Value));
        Assert.NotEqual(direct.Amount, RefundPayoutPolicy.Copy(Input() with { Amount = Money.Parse("1000.01") }).Amount);
    }
    [Fact]
    public void Actual_binding_preserves_exact_input_and_evidence_while_association_fencing_does_not_rewrite_the_immutable_draft() {
        var source = new BsonDocument("approval", new BsonDocument { { "id", 9007199254740993L }, { "reservedRefund", new BsonDecimal128(1000m) } });
        var direct = RefundPayoutBson.Binding(source, Input());
        Assert.Equal(direct, RefundPayoutBson.Binding(source, Input() with { Amount = Money.Parse("1000.00") }));
        Assert.NotEqual(direct, RefundPayoutBson.Binding(source, Input(1000.01m)));
        Assert.NotEqual(direct, RefundPayoutBson.Binding(source, Input() with { TransferReference = "BANK-TX-002" }));
        var draft = new BsonDocument { { "_id", 9007199254740993L }, { "input", RefundPayoutBson.Input(Input()) }, { "binding", direct }, { "recordedPayoutId", BsonNull.Value }, { "fence", 0L } };
        var draftHash = RefundPayoutBson.DraftHash(draft); draft["recordedPayoutId"] = 77L; draft["fence"] = 1L;
        Assert.Equal(draftHash, RefundPayoutBson.DraftHash(draft));
        draft["input"]["transferReference"] = "BANK-TX-002"; Assert.NotEqual(draftHash, RefundPayoutBson.DraftHash(draft));
        var reference = new EvidenceRef(91, 1, new string('a', 64)); var binding = RefundPayoutBson.RecordBinding(draftHash, 1, 81, reference);
        Assert.NotEqual(binding, RefundPayoutBson.RecordBinding(draftHash, 1, 82, reference));
        Assert.NotEqual(binding, RefundPayoutBson.RecordBinding(draftHash, 1, 81, new(91, 1, new string('b', 64))));
    }
}
