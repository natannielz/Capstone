using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class PdfDocumentOcrTests
{
    [WindowsStorageTheory]
    [InlineData(ExtractionPurpose.Payment)]
    [InlineData(ExtractionPurpose.SupplierInvoice)]
    public async Task Actual_two_page_pdf_uses_the_same_pinned_engine_and_retains_original_rotated_coordinates_and_all_candidates(ExtractionPurpose purpose)
    {
        var bytes = Pdf((Lines(purpose, alternate: false), 0), (Lines(purpose, alternate: true), 90));
        using var root = new StorageRoot(); var publication = PdfRasterProtocolTests.Expected(bytes); var evidence = Reference(bytes);
        await using var original = await Publish(root, publication, bytes);
        var processor = new PdfDocumentOcr(WindowsPdfRasterTests.Options(), LocalOcrTests.Options(), purpose);
        var result = await processor.ExtractAsync("synthetic-multipage", evidence, original, publication, DateTimeOffset.UtcNow.AddSeconds(60), default);
        Assert.True(result.Status == "succeeded", result.FailureCode); Assert.Null(result.FailureCode); Assert.True(result.RequiresHumanReview);
        Assert.Equal(evidence, result.OriginalEvidence); Assert.Equal(publication, result.Publication); Assert.Equal(purpose, result.Purpose);
        Assert.Equal(WindowsLocalOcrEngine.Engine, result.OcrProvenance.EngineVersion); Assert.Equal(processor.OcrProvenance, result.OcrProvenance);
        Assert.Equal(2, result.Pages.Count); Assert.Equal(new[] { 0, 90 }, result.Pages.Select(page => page.Raster.RotationDegrees));
        foreach (var page in result.Pages)
        {
            Assert.NotEmpty(page.Lines); Assert.Contains("human_review_required", page.ReviewReasons);
            Assert.Equal(result.OcrProvenance, page.OcrProvenance);
            using var png = page.Raster.OpenPng(); Assert.False(png.CanWrite); using var copy = new MemoryStream(); await png.CopyToAsync(copy);
            Assert.Equal(PdfRasterProtocol.Digest(copy.ToArray()), page.Raster.Sha256); Assert.NotEqual(evidence.Sha256, page.Raster.Sha256);
            foreach (var line in page.Lines) {
                Assert.Equal(PdfDocumentOcrCoordinates.Map(page.Raster, line.ImageBox), line.PdfCanvasCorners);
                Assert.All(line.PdfCanvasCorners, corner => { Assert.InRange(corner.X, 0, 576); Assert.InRange(corner.Y, 0, 360); });
            }
        }
        var amountName = purpose == ExtractionPurpose.Payment ? "amount" : "total_amount";
        var amount = Assert.Single(result.Fields, field => field.Field == amountName);
        Assert.Equal(new[] { "1234567.89", "2345678.90" }, amount.Observations.Select(value => value.Candidate.Value));
        Assert.Contains("contradictory_pages", amount.ReviewReasons); Assert.Equal(new[] { 0, 1 }, amount.Observations.Select(value => value.PageIndex));
        var currency = Assert.Single(result.Fields, field => field.Field == "currency");
        Assert.All(currency.Observations, value => Assert.Equal("IDR", value.Candidate.Value)); Assert.Contains("repeated_across_pages", currency.ReviewReasons);
        Assert.Throws<IOException>(() => File.Delete(root.Original)); await original.DisposeAsync(); Assert.Equal(bytes, await File.ReadAllBytesAsync(root.Original));
    }

    [WindowsStorageFact]
    public async Task Actual_blank_and_ambiguous_pages_remain_manual_review_unknowns_without_a_cross_page_winner()
    {
        var duplicate = Lines(ExtractionPurpose.Payment, alternate: false).Append("Nominal : Rp 2.345.678,90").ToArray();
        var bytes = Pdf((Array.Empty<string>(), 0), (duplicate, 0)); using var root = new StorageRoot(); var publication = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, publication, bytes);
        var result = await new PdfDocumentOcr(WindowsPdfRasterTests.Options(), LocalOcrTests.Options(), ExtractionPurpose.Payment)
            .ExtractAsync("synthetic-unknowns", Reference(bytes), original, publication, DateTimeOffset.UtcNow.AddSeconds(60), default);
        Assert.True(result.Status == "succeeded", result.FailureCode); Assert.Equal(2, result.Pages.Count);
        Assert.Empty(result.Pages[0].Lines); Assert.All(result.Pages[0].Fields, field => Assert.Null(field.Value));
        Assert.Contains("no_visible_marks", result.Pages[0].ReviewReasons); Assert.Contains("no_text", result.Pages[0].ReviewReasons);
        Assert.NotEmpty(result.Pages[1].Lines);
        var amount = Assert.Single(result.Fields, field => field.Field == "amount");
        Assert.All(amount.Observations, observation => Assert.Null(observation.Candidate.Value));
        Assert.Contains("ambiguous", amount.Observations[1].Candidate.Flags);
        Assert.Contains("unknown_on_page", amount.ReviewReasons); Assert.Contains("ambiguous_on_page", amount.ReviewReasons);
        Assert.True(result.RequiresHumanReview);
    }

    [WindowsStorageFact]
    public async Task A_real_first_page_cannot_escape_when_later_page_exceeds_existing_image_limits()
    {
        var bytes = Pdf((Lines(ExtractionPurpose.Payment, alternate: false), 0), (Lines(ExtractionPurpose.Payment, alternate: true), 90));
        using var root = new StorageRoot(); var publication = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, publication, bytes);
        var options = LocalOcrTests.Options();
        var processor = new PdfDocumentOcr(WindowsPdfRasterTests.Options(), options with { Limits = new(options.Limits.MaximumBytes, 32768, 1200, options.Limits.MaximumPixels) }, ExtractionPurpose.Payment);
        var result = await processor.ExtractAsync("synthetic-partial-denial", Reference(bytes), original, publication, DateTimeOffset.UtcNow.AddSeconds(60), default);
        Failed(result, "pdf_document_page_limit"); Assert.Equal(1, result.FailurePageIndex);
    }

    [WindowsStorageFact]
    public async Task Captured_deadline_cancellation_original_hash_and_real_runtime_pins_fail_closed_without_partial_output()
    {
        var bytes = Pdf((Lines(ExtractionPurpose.Payment, alternate: false), 0)); using var root = new StorageRoot(); var publication = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, publication, bytes);
        var raster = WindowsPdfRasterTests.Options(); var options = LocalOcrTests.Options(); var processor = new PdfDocumentOcr(raster, options, ExtractionPurpose.Payment);
        Failed(await processor.ExtractAsync("expired", Reference(bytes), original, publication, DateTimeOffset.UtcNow.AddTicks(-1), default), "pdf_document_deadline");
        Failed(await processor.ExtractAsync("too-late", Reference(bytes), original, publication, DateTimeOffset.UtcNow.AddMilliseconds(1), default), "pdf_document_deadline");
        Failed(await processor.ExtractAsync("hash", new(71, 3, new string('0', 64)), original, publication, DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_document_source_mismatch");
        var altered = options with { Files = options.Files.Select(pin => pin.Path == options.HelperPath ? pin with { Sha256 = new string('0', 64) } : pin).ToArray() };
        Failed(await new PdfDocumentOcr(raster, altered, ExtractionPurpose.Payment).ExtractAsync("pins", Reference(bytes), original, publication, DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_document_unavailable");
        using var cancellation = new CancellationTokenSource();
        var running = processor.ExtractAsync("cancel", Reference(bytes), original, publication, DateTimeOffset.UtcNow.AddSeconds(60), cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Throws<ArgumentException>(() => new PdfDocumentOcr(raster, options, ExtractionPurpose.DeliveryNote));
        Assert.Throws<IOException>(() => File.Delete(root.Original));
        await original.DisposeAsync(); Assert.Equal(bytes, await File.ReadAllBytesAsync(root.Original));
    }

    [Fact]
    public void Mapping_preserves_the_rotated_rectangle_and_rejects_unrelated_or_nonfinite_provenance()
    {
        var png = ImageDecoderTests.Png(100, 200);
        PdfRasterPage Page(PdfRasterPoint[] corners) => new(0, 100, 200, 90, PdfRasterProtocol.Digest(png), png, [36, 72], [0, 0, 72, 36], corners, ["human_review_required"]);
        var page = Page([new(0, 0), new(0, 36), new(72, 36), new(72, 0)]);
        var corners = PdfDocumentOcrCoordinates.Map(page, new(.25m, .25m, .5m, .5m));
        Assert.Equal(new[] { new PdfRasterPoint(18, 9), new(18, 27), new(54, 27), new(54, 9) }, corners);
        Assert.Throws<InvalidDataException>(() => PdfDocumentOcrCoordinates.Map(Page([new(0, 0), new(0, 0), new(0, 0), new(0, 0)]), new(0, 0, 1, 1)));
        Assert.Throws<InvalidDataException>(() => PdfDocumentOcrCoordinates.Map(Page([new(double.NaN, 0), new(0, 36), new(72, 36), new(72, 0)]), new(0, 0, 1, 1)));
    }

    private static void Failed(PdfDocumentOcrResult result, string code) { Assert.Equal("failed", result.Status); Assert.Equal(code, result.FailureCode); Assert.Empty(result.Pages); Assert.Empty(result.Fields); }
    private static EvidenceRef Reference(byte[] bytes) => new(71, 3, PdfRasterProtocol.Digest(bytes));
    private static async Task<VerifiedNativeOriginalHandle> Publish(StorageRoot root, ExpectedPublication publication, byte[] bytes)
    {
        var store = root.Open(); var budget = new StorageIoBudget(10 * 1024 * 1024, TimeSpan.FromSeconds(10), 65536); using var source = new MemoryStream(bytes);
        Assert.IsType<StageOutcome.Sealed>(await store.StageAsync(publication.Attempt, source, budget, default));
        Assert.Equal(PublishState.Published, (await store.PublishAsync(publication, budget, default)).State);
        return Assert.IsType<NativeOpenOutcome.Opened>(await store.OpenVerifiedForNativeValidationAsync(publication, budget, default)).Handle;
    }
    private static string[] Lines(ExtractionPurpose purpose, bool alternate)
    {
        var amount = alternate ? "2.345.678,90" : "1.234.567,89";
        return purpose == ExtractionPurpose.Payment
            ? ["BUKTI TRANSFER SINTETIS", "Nominal : Rp " + amount, "Tanggal : 30/09/2026", "Waktu : 14:25:36",
                "Nama penerima : KOPERASI DEMO", "Rekening tujuan : 1234567890", "Bank tujuan : BANK DEMO", "Referensi : DEMO-20260930-1"]
            : ["Nomor invoice : SYNTH-INVOICE-001", "Tanggal invoice : 03/10/2026", "Nama pemasok : PEMASOK CONTOH", "Total tagihan : Rp " + amount,
                "Nomor PO : SYNTH-PO-001", "Nomor surat jalan : SYNTH-SJ-001", "Kode barang pemasok : PROC-PENCIL-12"];
    }
    private static byte[] Pdf(params (string[] Lines, int Rotation)[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var pageId = objects.Count + 1; kids.Add(pageId);
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 576 360] /Rotate {page.Rotation} /Resources << /Font << /F1 3 0 R >> >> /Contents {pageId + 1} 0 R >>"));
            var contents = new StringBuilder("BT /F1 16 Tf 0 g 24 330 Td 32 TL\n");
            foreach (var line in page.Lines) contents.Append('(').Append(line.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)).Append(") Tj T*\n");
            contents.Append("ET\n"); objects.Add(FormattableString.Invariant($"<< /Length {contents.Length} >>\nstream\n{contents}\nendstream"));
        }
        objects[1] = FormattableString.Invariant($"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', kids.Select(id => id.ToString(CultureInfo.InvariantCulture) + " 0 R"))}] >>");
        var document = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++) { offsets.Add(document.Length); document.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = document.Length; document.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) document.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        document.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(document.ToString());
    }
}
