using System.Collections.Immutable;
using System.Text.Json;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Tests.Billing;

public sealed class PaymentSupplementCorrectionContractTests
{
    private static readonly EvidenceRef Pdf = new(12, 1, new string('b', 64));
    private static PaymentSupplementReviewObservation Value(string field = "amount") => new(field, 11, 2, new(13, new string('c', 64), Pdf, 1, field), true);
    [Fact]
    public void Omitted_annotations_preserve_exact_old_body_and_explicit_observations_use_canonical_field_order()
    {
        var binding = new PaymentCorrectionBinding(1, 2, new(3, 1, new string('a', 64)), 4);
        var fields = PaymentReviewCorrectionPolicy.Copy([.. PaymentFieldExtractor.Fields.Select(field => new PaymentReviewField(field, null))]);
        var oldBody = JsonSerializer.Serialize(new { declarationId = 5L, expectedReviewRevision = 0L, binding, fields });
        Assert.Equal(oldBody, JsonSerializer.Serialize(PaymentCorrectionBson.Body(5, 0, binding, fields)));
        Assert.Null(PaymentReviewCorrectionPolicy.CopySupplementaryObservations(null));
        var copied = PaymentReviewCorrectionPolicy.CopySupplementaryObservations(ImmutableArray.Create(Value("reference"), Value("amount")))!.Value;
        Assert.Equal(new[] { "amount", "reference" }, copied.Select(value => value.Field));
        var body = JsonSerializer.Serialize(PaymentCorrectionBson.Body(5, 0, binding, fields, copied));
        Assert.Contains("supplementaryObservations", body); Assert.NotEqual(oldBody, body);
    }
    [Fact]
    public void Empty_duplicate_unreviewed_wrong_revision_and_field_mismatch_are_explicitly_refused()
    {
        var valid = Value();
        foreach (var values in new[] { ImmutableArray<PaymentSupplementReviewObservation>.Empty, default,
            ImmutableArray.Create(valid, valid), ImmutableArray.Create(valid with { OriginalReviewed = false }),
            ImmutableArray.Create(valid with { SupplementRevision = 1 }), ImmutableArray.Create(valid with { Field = "reference" }) })
            Assert.Equal("invalid_payment_correction", Assert.Throws<DomainValidationException>(() => PaymentReviewCorrectionPolicy.CopySupplementaryObservations(values)).Code);
    }
}
