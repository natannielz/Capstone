using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Tests.Documents;

public sealed class PaymentExtractionTests
{
    [Fact]
    public void Labelled_values_are_suggestions_with_locations_and_exact_decimal_amounts() {
        var result = PaymentFieldExtractor.Extract([
            Line("Nominal: Rp 1.234.567,89"), Line("Tanggal: 30/09/2026"), Line("Waktu: 12:34:56"),
            Line("Penerima: KOPERASI CONTOH"), Line("Rekening tujuan: 0012345678"),
            Line("Bank tujuan: BNI"), Line("No. Referensi: DEMO-20260930-1")]);
        Assert.Equal("1234567.89", Field(result, "amount").Value);
        Assert.Equal("IDR", Field(result, "currency").Value);
        Assert.Equal("2026-09-30", Field(result, "transfer_date").Value);
        Assert.Equal("12:34:56", Field(result, "transfer_time").Value);
        Assert.Equal("0012345678", Field(result, "account").Value);
        Assert.Equal("KOPERASI CONTOH", Field(result, "recipient").Value);
        Assert.Equal("BNI", Field(result, "bank").Value);
        Assert.Equal("DEMO-20260930-1", Field(result, "reference").Value);
        Assert.All(result.Fields, field => { Assert.Equal(.98m, field.Confidence); Assert.Equal(new decimal[] { .1m, .1m, .8m, .1m }, field.BoundingBox); });
        Assert.Contains("timezone_unverified", Field(result, "transfer_time").Flags);
    }

    [Theory]
    [InlineData("1O0.000", null)]
    [InlineData("1,234", null)]
    [InlineData("1.23.456", null)]
    [InlineData("1 234", null)]
    [InlineData("1000000000000000", null)]
    [InlineData("999.999.999.999.999,99", "999999999999999.99")]
    [InlineData("0", "0.00")]
    [InlineData("1234.56", "1234.56")]
    [InlineData("1.234", "1234.00")]
    public void Amount_parser_does_not_repair_digits_guess_ambiguous_separators_or_exceed_money_limit(string input, string? expected) {
        var field = Field(PaymentFieldExtractor.Extract([Line("Nominal: Rp " + input)]), "amount");
        Assert.Equal(expected, field.Value);
        if (expected is null) Assert.Contains("unparsed", field.Flags);
    }

    [Fact]
    public void Missing_currency_unknown_date_and_conflicting_labels_stay_unknown() {
        var result = PaymentFieldExtractor.Extract([Line("Nominal: 100000"), Line("Tanggal: 31/02/2026"),
            Line("Penerima: CONTOH A"), Line("Penerima: CONTOH B"), Line("Saldo: Rp 900.000,00"), Line("Biaya: Rp 2.500,00")]);
        Assert.Null(Field(result, "amount").Value); Assert.Null(Field(result, "currency").Value);
        Assert.Null(Field(result, "transfer_date").Value); Assert.Null(Field(result, "recipient").Value);
        Assert.Contains("ambiguous", Field(result, "recipient").Flags);
        Assert.Contains("unknown", Field(result, "reference").Flags);
        Assert.Contains("amount_unknown", result.QualityFlags);
    }

    [Fact]
    public void Low_confidence_masked_account_and_clipped_text_are_visible_without_fabricated_values() {
        var result = PaymentFieldExtractor.Extract([new("Nominal: Rp 100,00", .4m, new(0, .2m, .6m, .1m)), Line("Rekening tujuan: ****1234")]);
        Assert.Equal("100.00", Field(result, "amount").Value);
        Assert.Contains("low_confidence", Field(result, "amount").Flags);
        Assert.Contains("touches_image_edge", result.QualityFlags);
        Assert.Equal("****1234", Field(result, "account").Value);
        Assert.Contains("masked", Field(result, "account").Flags);
    }

    [Fact]
    public void Duplicate_even_matching_amount_labels_require_review_instead_of_arbitrary_selection() {
        var result = PaymentFieldExtractor.Extract([Line("Nominal: Rp 100,00"), Line("Nominal: Rp 100,00")]);
        Assert.Null(Field(result, "amount").Value); Assert.Contains("ambiguous", Field(result, "amount").Flags);
    }

    [Fact]
    public void Empty_image_has_unknown_fields_not_zero_or_verified_payment() {
        var result = PaymentFieldExtractor.Extract([]);
        Assert.Equal(8, result.Fields.Count); Assert.All(result.Fields, field => Assert.Null(field.Value));
        Assert.Contains("no_text", result.QualityFlags); Assert.Contains("amount_unknown", result.QualityFlags);
    }

    [Fact]
    public void Oversized_or_invalid_engine_text_and_coordinates_are_rejected() {
        Assert.Throws<DomainValidationException>(() => new RecognizedLine(new string('a', 513), .5m, new(.1m, .1m, .5m, .1m)));
        Assert.Throws<DomainValidationException>(() => Line("a\0b"));
        Assert.Throws<DomainValidationException>(() => new RecognizedLine("a", 1.01m, new(.1m, .1m, .5m, .1m)));
        Assert.Throws<DomainValidationException>(() => new NormalizedBox(.9m, .1m, .2m, .1m));
        Assert.Throws<DomainValidationException>(() => PaymentFieldExtractor.Extract(Enumerable.Range(0, 129).Select(_ => Line("text")).ToArray()));
    }

    private static RecognizedLine Line(string text) => new(text, .98m, new(.1m, .1m, .8m, .1m));
    private static FieldCandidate Field(PaymentExtraction value, string name) => Assert.Single(value.Fields, field => field.Field == name);
}
