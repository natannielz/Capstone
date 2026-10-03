using System.Runtime.Versioning;
using System.Security.Cryptography;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class SupplierDocumentLocalOcrTests
{
    [LocalOcrFact]
    public async Task Actual_supplier_engine_recognizes_original_headers_without_payment_fields_or_automatic_matches() {
        var path = Path.Combine(Environment.GetEnvironmentVariable("UNITTOKO_SUPPLIER_OCR_FIXTURES") ?? throw new InvalidOperationException("Explicit synthetic supplier OCR fixture directory is required"), "supplier-invoice.png");
        var bytes = await File.ReadAllBytesAsync(path); var reference = Ref(bytes);
        var engine = new WindowsLocalOcrEngine(LocalOcrTests.Options(), ExtractionPurpose.SupplierInvoice);
        using var input = new MemoryStream(bytes); var result = await engine.ExtractAsync("actual-supplier", reference, ExtractionPurpose.SupplierInvoice, input, default);
        Assert.Equal("succeeded", result.Status); Assert.Null(result.FailureCode); Assert.Equal(reference, result.Evidence); Assert.Empty(result.Fields);
        var suggestions = SupplierDocumentFieldExtractor.Extract(result.Lines);
        Assert.Equal("SYNTH-INVOICE-001", Assert.Single(suggestions.Fields, field => field.Field == "document_reference").Value);
        Assert.Equal("36000.00", Assert.Single(suggestions.Fields, field => field.Field == "total_amount").Value);
        Assert.Equal("IDR", Assert.Single(suggestions.Fields, field => field.Field == "currency").Value);
        Assert.Equal("2026-10-03", Assert.Single(suggestions.Fields, field => field.Field == "document_date").Value);
        Assert.Equal("PEMASOK CONTOH", Assert.Single(suggestions.Fields, field => field.Field == "supplier_name").Value);
        Assert.Equal("SYNTH-PO-001", Assert.Single(suggestions.Fields, field => field.Field == "purchase_order_reference").Value);
        Assert.Equal("SYNTH-SJ-001", Assert.Single(suggestions.Fields, field => field.Field == "delivery_reference").Value);
        Assert.Equal("PROC-PENCIL-12", Assert.Single(suggestions.Fields, field => field.Field == "source_sku").Value);
        Assert.Contains("human_line_matching_required", suggestions.QualityFlags); Assert.Equal(engine.Provenance, result.Provenance);
        Assert.EndsWith(SupplierDocumentFieldExtractor.Version, result.Provenance.PreprocessingVersion);
    }
    [LocalOcrFact]
    public async Task Supplier_purpose_is_explicit_and_pins_original_hash_media_and_cancellation_still_fail_closed() {
        var options = LocalOcrTests.Options(); var bytes = ImageDecoderTests.Png(32, 32); var reference = Ref(bytes);
        var engine = new WindowsLocalOcrEngine(options, ExtractionPurpose.SupplierInvoice);
        using var wrongPurpose = new MemoryStream(bytes);
        Assert.Equal("ocr_unsupported_purpose", (await engine.ExtractAsync("wrong-purpose", reference, ExtractionPurpose.Payment, wrongPurpose, default)).FailureCode); Assert.Equal(0, wrongPurpose.Position);
        using var empty = new MemoryStream(bytes); var result = await engine.ExtractAsync("empty", reference, ExtractionPurpose.SupplierInvoice, empty, default);
        Assert.Equal("succeeded", result.Status); Assert.Contains("no_text", SupplierDocumentFieldExtractor.Extract(result.Lines).QualityFlags);
        var mismatched = options with { Files = options.Files.Select(pin => pin.Path == options.HelperPath ? pin with { Sha256 = new string('0', 64) } : pin).ToArray() };
        using var pinned = new MemoryStream(bytes);
        Assert.Equal("ocr_unavailable", (await new WindowsLocalOcrEngine(mismatched, ExtractionPurpose.SupplierInvoice).ExtractAsync("bad-pin", reference, ExtractionPurpose.SupplierInvoice, pinned, default)).FailureCode); Assert.Equal(0, pinned.Position);
        using var changed = new MemoryStream(bytes);
        Assert.Equal("ocr_integrity_mismatch", (await engine.ExtractAsync("bad-hash", new(1, 1, new string('f', 64)), ExtractionPurpose.SupplierInvoice, changed, default)).FailureCode);
        using var invalid = new MemoryStream("not an image"u8.ToArray());
        Assert.Equal("ocr_invalid_media", (await engine.ExtractAsync("invalid", Ref(invalid.ToArray()), ExtractionPurpose.SupplierInvoice, invalid, default)).FailureCode);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var canceled = new MemoryStream(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExtractAsync("cancelled", reference, ExtractionPurpose.SupplierInvoice, canceled, cancel.Token));
    }
    private static EvidenceRef Ref(byte[] bytes) => new(1, 1, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
}
