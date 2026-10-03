using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;

namespace UnitToko.Tests.Documents;

public sealed class SupplierDocumentExtractionTests
{
    [Fact]
    public void Explicit_supplier_labels_preserve_exact_references_and_values_but_never_make_line_claims() {
        var result = SupplierDocumentFieldExtractor.Extract([Line("Nomor invoice: INV-00007"), Line("Tanggal invoice: 03/10/2026"),
            Line("Nama pemasok: PEMASOK SINTETIS"), Line("Total tagihan: Rp 1.234.567,89"), Line("Nomor PO: PO-0002"),
            Line("Nomor surat jalan: SJ-00003"), Line("Kode barang pemasok: 00004-a")]);
        Assert.Equal("INV-00007", Field(result, "document_reference").Value); Assert.Equal("2026-10-03", Field(result, "document_date").Value);
        Assert.Equal("PEMASOK SINTETIS", Field(result, "supplier_name").Value); Assert.Equal("1234567.89", Field(result, "total_amount").Value);
        Assert.Equal("IDR", Field(result, "currency").Value); Assert.Equal("PO-0002", Field(result, "purchase_order_reference").Value);
        Assert.Equal("SJ-00003", Field(result, "delivery_reference").Value); Assert.Equal("00004-a", Field(result, "source_sku").Value);
        Assert.All(result.Fields, field => { Assert.Equal(.98m, field.Confidence); Assert.Equal(new decimal[] { .1m, .1m, .8m, .1m }, field.BoundingBox); });
        Assert.Contains("human_line_matching_required", result.QualityFlags);
    }
    [Fact]
    public void Quotations_payment_labels_duplicate_source_codes_and_missing_facts_remain_unresolved() {
        var result = SupplierDocumentFieldExtractor.Extract([Line("Quotation number: Q-1"), Line("Quotation date: 03/10/2026"),
            Line("Total penawaran: Rp 100,00"), Line("Nominal: Rp 100,00"), Line("Kode barang pemasok: 001"), Line("Kode barang pemasok: 001")]);
        Assert.All(result.Fields, field => Assert.Null(field.Value)); Assert.Contains("ambiguous", Field(result, "source_sku").Flags);
        Assert.Contains("ambiguous_fields", result.QualityFlags); Assert.Contains("amount_unknown", result.QualityFlags);
        var empty = SupplierDocumentFieldExtractor.Extract([]); Assert.Equal(8, empty.Fields.Count); Assert.Contains("no_text", empty.QualityFlags);
        Assert.All(empty.Fields, field => { Assert.Null(field.Confidence); Assert.Null(field.BoundingBox); });
    }
    [Theory]
    [InlineData("1O0.000", null)]
    [InlineData("1,234", null)]
    [InlineData("1.23.456", null)]
    [InlineData("0", "0.00")]
    [InlineData("1.234,50", "1234.50")]
    public void Amount_suggestions_do_not_repair_digits_or_guess_separators(string input, string? expected) {
        var result = SupplierDocumentFieldExtractor.Extract([Line("Total sph: IDR " + input)]);
        Assert.Equal(expected, Field(result, "total_amount").Value);
    }
    [Fact]
    public void Supplier_success_contains_complete_raw_lines_and_no_payment_fields_and_overflow_never_truncates() {
        var provenance = new ExtractionProvenance("synthetic", "synthetic", new string('a', 64), new string('b', 64), SupplierDocumentFieldExtractor.Version);
        var reference = new EvidenceRef(1, 1, new string('c', 64)); var lines = new[] { Line("Nomor invoice: 001"), Line("Unstructured item row 2") };
        var result = new ExtractionResult("synthetic", reference, ExtractionPurpose.SupplierInvoice, provenance, "succeeded", null, lines);
        Assert.Equal(lines, result.Lines); Assert.Empty(result.Fields);
        Assert.Throws<DomainValidationException>(() => new ExtractionResult("oversized", reference, ExtractionPurpose.SupplierInvoice, provenance, "succeeded", null, Enumerable.Repeat(Line("a"), 129)));
        Assert.Throws<DomainValidationException>(() => SupplierDocumentFieldExtractor.Extract(Enumerable.Repeat(Line(new string('a', 512)), 33)));
    }
    private static RecognizedLine Line(string text) => new(text, .98m, new(.1m, .1m, .8m, .1m));
    private static SupplierDocumentFieldCandidate Field(SupplierDocumentExtraction result, string name) => Assert.Single(result.Fields, field => field.Field == name);
}
