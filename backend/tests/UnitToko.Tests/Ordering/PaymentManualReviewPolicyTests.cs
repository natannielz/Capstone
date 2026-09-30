using System.Collections.Immutable;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Tests.Ordering;

public sealed class PaymentManualReviewPolicyTests
{
    [Theory]
    [InlineData("retry", "ocr_unavailable", true)] [InlineData("dead", "ocr_timeout", true)] [InlineData("dead", "ocr_invalid_output", true)]
    [InlineData("running", "ocr_timeout", false)] [InlineData("queued", "ocr_unavailable", false)] [InlineData("succeeded", "ocr_unavailable", false)]
    [InlineData("dead", "ocr_integrity_mismatch", false)] [InlineData("retry", "ocr_original_unavailable", false)] [InlineData("dead", "ocr_invalid_media", false)]
    [InlineData("dead", "infected_media", false)] [InlineData("dead", "processing_error", false)]
    public void Only_processing_failures_are_candidates_and_security_or_missing_originals_never_qualify(string state, string code, bool expected) =>
        Assert.Equal(expected, PaymentManualReviewPolicy.IsReviewableFailure(state, code));

    [Fact]
    public void Explicit_review_preserves_unknown_fields_and_exact_values() {
        var fields = PaymentFieldExtractor.Fields.Select(field => new PaymentReviewField(field, field == "amount" ? "90071992547409.93" : null)).ToImmutableArray();
        var input = new PaymentManualReviewInput(new string('a', 64), true, fields);
        var result = PaymentManualReviewPolicy.Copy(input);
        Assert.Equal("90071992547409.93", result.Fields.Single(field => field.Field == "amount").Value);
        Assert.All(result.Fields.Where(field => field.Field != "amount"), field => Assert.Null(field.Value));
        Assert.Throws<DomainValidationException>(() => PaymentManualReviewPolicy.Copy(input with { ImageReviewed = false }));
        Assert.Throws<DomainValidationException>(() => PaymentManualReviewPolicy.Copy(input with { Binding = "unbound" }));
        Assert.Throws<DomainValidationException>(() => PaymentManualReviewPolicy.Copy(input with { Fields = ImmutableArray<PaymentReviewField>.Empty }));
    }
}
