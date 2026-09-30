using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;
public sealed class PaymentVerificationPolicyTests
{
    private static PaymentDraftInput Declaration => new([new(1)], new(4000m), new(2026, 9, 30), "Synthetic payer", "reference", null);
    private static ImmutableArray<PaymentReviewField> Fields => [
        new("amount", "4000.00"), new("currency", "IDR"), new("transfer_date", "2026-09-30"), new("transfer_time", null),
        new("recipient", "Synthetic recipient"), new("account", "0000123456789"), new("bank", "FIXTUREBANK"), new("reference", "reference")];
    [Theory]
    [InlineData("amount", "3999.99")] [InlineData("amount", null)] [InlineData("currency", "USD")]
    [InlineData("account", "123456789")] [InlineData("account", "****6789")]
    public void Missing_or_changed_critical_image_facts_cannot_be_explained_away(string field, string? value) {
        var fields = Fields.Select(item => item.Field == field ? item with { Value = value } : item).ToImmutableArray();
        Assert.Throws<DomainValidationException>(() => PaymentVerificationPolicy.RequireExactFunds(Declaration, new(4000m), fields, "0000123456789"));
    }
    [Fact]
    public void Exact_funds_and_all_discrepancies_remain_distinct_from_payer_identity() {
        PaymentVerificationPolicy.RequireExactFunds(Declaration, new(4000m), Fields, "0000123456789");
        Assert.Throws<DomainValidationException>(() => PaymentVerificationPolicy.RequireExactFunds(Declaration, new(4000.01m), Fields, "0000123456789"));
        var differences = PaymentVerificationPolicy.Discrepancies(Declaration, Fields, "2026-09-27", "Synthetic recipient", "FIXTUREBANK", "reference");
        Assert.Equal(new[] { "transfer_date", "transfer_time" }, differences.Select(item => item.Field));
        var input = new PaymentVerificationInput(new string('a', 64), "Independent payer procedure", true, true, true,
            [new("transfer_date", "Explained booking difference"), new("transfer_time", "Transfer time unavailable")]);
        PaymentVerificationPolicy.RequireResolutions(differences, input);
        Assert.Throws<DomainValidationException>(() => PaymentVerificationPolicy.RequireResolutions(differences, input with { PayerVerified = false }));
        Assert.Throws<DomainValidationException>(() => PaymentVerificationPolicy.RequireResolutions(differences, input with { Resolutions = [input.Resolutions[0]] }));
    }
}
