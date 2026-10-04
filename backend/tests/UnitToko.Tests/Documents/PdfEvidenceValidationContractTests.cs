using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Documents;

public sealed class PdfEvidenceValidationContractTests
{
    [Fact]
    public void Canonical_hash_preserves_page_order_and_every_original_and_provenance_fact()
    {
        var row = Passed(); var reordered = new BsonDocument(row.Elements.Reverse());
        reordered["rasterProvenance"] = new BsonDocument(row["rasterProvenance"].AsBsonDocument.Elements.Reverse());
        var page = row["pages"][0].AsBsonDocument;
        reordered["pages"] = new BsonArray { new BsonDocument(page.Elements.Reverse()) };
        Assert.Equal(PdfEvidenceValidationRecords.Hash(row), PdfEvidenceValidationRecords.Hash(reordered));
        foreach (var field in new[] { "runtimeSha256", "wheelSha256" }) {
            var changed = row.DeepClone().AsBsonDocument; changed["rasterProvenance"][field] = new string('f', 64);
            Assert.NotEqual(PdfEvidenceValidationRecords.Hash(row), PdfEvidenceValidationRecords.Hash(changed));
        }
        var swapped = row.DeepClone().AsBsonDocument;
        swapped["pages"][0]["pixelCornersInPdfCanvas"] = new BsonArray(page["pixelCornersInPdfCanvas"].AsBsonArray.Reverse());
        Assert.NotEqual(PdfEvidenceValidationRecords.Hash(row), PdfEvidenceValidationRecords.Hash(swapped));
        Assert.False(PdfEvidenceValidationRecords.CompletePages(swapped));
    }

    [Fact]
    public void Complete_pages_require_original_PDF_mime_contiguous_indexes_geometry_and_complete_review_metadata()
    {
        var passed = Passed(); Assert.True(PdfEvidenceValidationRecords.CompletePages(passed));
        foreach (var mutate in new Action<BsonDocument>[] {
            row => row["mime"] = "image/png", row => row["pageCount"] = 2,
            row => row["pages"][0]["pageIndex"] = 1, row => row["pages"][0]["width"] = 101,
            row => row["pages"][0]["length"] = 13L * 1024 * 1024,
            row => row["pages"][0]["canvasSize"] = new BsonArray { double.NaN, 72d },
            row => row["pages"][0]["reviewReasons"] = new BsonArray(),
            row => row["rasterProvenance"]["wheelSha256"] = new string('f', 64),
            row => row["pages"] = new BsonArray() }) {
            var changed = passed.DeepClone().AsBsonDocument; mutate(changed);
            Assert.False(PdfEvidenceValidationRecords.CompletePages(changed));
        }
        var blank = passed.DeepClone().AsBsonDocument;
        blank["pages"][0]["reviewReasons"] = new BsonArray { "human_review_required", "no_visible_marks" };
        Assert.True(PdfEvidenceValidationRecords.CompletePages(blank));
        var rejected = new BsonDocument { { "status", "rejected" }, { "pageCount", BsonNull.Value },
            { "rasterProvenance", BsonNull.Value }, { "pages", new BsonArray() } };
        Assert.True(PdfEvidenceValidationRecords.CompletePages(rejected));
        rejected["pages"] = passed["pages"];
        Assert.False(PdfEvidenceValidationRecords.CompletePages(rejected));
    }

    [Theory]
    [InlineData("pdf_raster_malformed_pdf", "invalid_media")]
    [InlineData("pdf_raster_encrypted_pdf", "invalid_media")]
    [InlineData("pdf_raster_unsupported_javascript", "invalid_media")]
    [InlineData("pdf_raster_unsupported_attachments", "invalid_media")]
    [InlineData("pdf_raster_page_limit", "invalid_media")]
    [InlineData("pdf_raster_source_mismatch", "integrity_mismatch")]
    [InlineData("pdf_raster_unavailable", null)]
    [InlineData("pdf_raster_runtime_pin", null)]
    [InlineData("pdf_raster_deadline", null)]
    [InlineData("pdf_raster_invalid_output", null)]
    [InlineData("pdf_raster_render_failed", null)]
    public void Runtime_failure_is_never_a_content_rejection_or_a_partial_pass(string code, string? expected) =>
        Assert.Equal(expected, PdfEvidenceValidationProcessor.RasterFailure(code));

    [Fact]
    public void Persisted_geometry_uses_the_verified_native_protocol_tolerance_without_accepting_changed_corner_mapping()
    {
        var row = Passed();
        row["pages"][0]["pixelCornersInPdfCanvas"][0]["x"] = .0005;
        Assert.True(PdfEvidenceValidationRecords.CompletePages(row));
        row["pages"][0]["pixelCornersInPdfCanvas"][0]["x"] = .002;
        Assert.False(PdfEvidenceValidationRecords.CompletePages(row));
    }

    [Fact]
    public void Scanner_facts_and_receipt_owner_cannot_be_inferred_from_a_clean_label()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(PdfEvidenceValidationProcessor.ScannerFacts(new("clean"), now));
        Assert.False(PdfEvidenceValidationProcessor.ScannerFacts(new("clean", "engine\n", 1, now), now));
        Assert.False(PdfEvidenceValidationProcessor.ScannerFacts(new("clean", "engine", 1, now.AddTicks(1)), now));
        Assert.False(PdfEvidenceValidationProcessor.ScannerFacts(new("clean", "engine", 0, now), now));
        Assert.True(PdfEvidenceValidationProcessor.ScannerFacts(new("clean", "synthetic-only", 1, now), now));
        var row = new BsonDocument { { "_id", 1L }, { "resultHash", new string('a', 64) }, { "auditId", 2L }, { "pipelineId", new string('b', 64) } };
        var receipt = PdfEvidenceValidationRecords.Receipt(row);
        Assert.True(receipt["evidencePdfValidation"].AsBoolean);
        Assert.False(receipt.Contains("evidenceVideoValidation"));
        Assert.NotEqual(EvidenceValidationRecords.Receipt(row), receipt);
        Assert.NotEqual("evidence.validation.requested", PdfEvidenceValidationContract.JobKind);
        Assert.NotEqual(VideoValidationRecords.Kind, PdfEvidenceValidationContract.JobKind);
    }

    [Fact]
    public void Outbox_extension_keeps_reviewed_predecessor_and_stronger_terms_and_refuses_an_unknown_shape()
    {
        var predecessor = PdfEvidenceValidationSchemas.PreviousOutboxValidator();
        var stronger = new BsonDocument("$and", new BsonArray { predecessor, new BsonDocument("attempts", new BsonDocument("$lte", 3)) });
        var extended = PdfEvidenceValidationSchemas.ExtendOutbox(stronger);
        Assert.True(PdfEvidenceValidationSchemas.SupportsOutbox(extended, predecessor));
        Assert.True(InventorySchemas.Contains(extended, new BsonDocument("attempts", new BsonDocument("$lte", 3))));
        Assert.Equal(extended, PdfEvidenceValidationSchemas.ExtendOutbox(extended));
        Assert.Throws<InvalidOperationException>(() => PdfEvidenceValidationSchemas.ExtendOutbox(new BsonDocument("schemaVersion", 1)));
        Assert.Equal(3, PdfEvidenceValidationSchemas.Collections.Length);
        Assert.All(PdfEvidenceValidationSchemas.Collections, table => Assert.DoesNotContain(table, EvidenceValidationSchemas.Collections));
    }

    private static BsonDocument Passed()
    {
        var page = new PdfRasterPage(0, 100, 200, 0, new string('b', 64), new byte[57], [36, 72], [0, 0, 36, 72],
            [new(0, 72), new(36, 72), new(36, 0), new(0, 0)], ["human_review_required"]);
        return new() { { "status", "passed" }, { "mime", PdfEvidenceValidationContract.Mime }, { "pageCount", 1 },
            { "rasterProvenance", PdfEvidenceValidationRecords.Provenance(new(PdfRasterProtocol.Pipeline, PdfRasterProtocol.Wrapper,
                PdfRasterProtocol.Pdfium, PdfRasterProtocol.WheelSha256, "12.3.0", new string('c', 64))) },
            { "pages", new BsonArray { PdfEvidenceValidationRecords.Page(page) } } };
    }
}
