using System.Collections.Immutable;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Tests.Ordering;
public sealed class PaymentCorrectionPolicyTests
{
    private static ImmutableArray<PaymentReviewField> Fields(string name, string? value) => [.. PaymentFieldExtractor.Fields.Select(field => new PaymentReviewField(field, field == name ? value : null))];
    [Theory]
    [InlineData("amount", "999999999999999.99")] [InlineData("currency", "IDR")] [InlineData("transfer_date", "2024-02-29")]
    [InlineData("transfer_time", "23:59:59")] [InlineData("account", "123***")] [InlineData("reference", null)]
    public void Unknown_or_canonical_observations_remain_observations(string field, string? value) => Assert.Equal(value, PaymentReviewCorrectionPolicy.Copy(Fields(field, value)).Single(row => row.Field == field).Value);
    [Theory]
    [InlineData("amount", "1000000000000000.00")] [InlineData("amount", "-1.00")] [InlineData("amount", "2.001")] [InlineData("currency", "USD")]
    [InlineData("transfer_date", "2023-02-29")] [InlineData("transfer_time", "24:00:00")] [InlineData("account", " 123")]
    [InlineData("reference", "abc\n")] [InlineData("reference", "abc\u202e")]
    public void Invalid_values_are_rejected(string field, string value) => Assert.Throws<DomainValidationException>(() => PaymentReviewCorrectionPolicy.Copy(Fields(field, value)));
    [Fact]
    public void Exactly_one_of_each_field_and_exact_binding_are_required() {
        var rows = Fields("amount", "2"); Assert.Equal("2.00", PaymentReviewCorrectionPolicy.Copy(rows)[0].Value);
        Assert.Throws<DomainValidationException>(() => PaymentReviewCorrectionPolicy.Copy(rows.SetItem(1, rows[0])));
        Assert.Throws<DomainValidationException>(() => PaymentReviewCorrectionPolicy.Copy(rows.RemoveAt(0)));
        var binding = new PaymentCorrectionBinding(1, 2, new(3, 1, new string('a',64)), 4);
        PaymentReviewCorrectionPolicy.Validate(binding, 0);
        Assert.Throws<DomainValidationException>(() => PaymentReviewCorrectionPolicy.Validate(binding, long.MaxValue - 1));
        Assert.Throws<DomainValidationException>(() => PaymentReviewCorrectionPolicy.Validate(binding with { ExtractionId = 0 }, 0));
    }
}
