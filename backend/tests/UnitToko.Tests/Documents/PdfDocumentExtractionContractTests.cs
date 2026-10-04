using System.Security.Cryptography;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Tests.Documents;

public sealed class PdfDocumentExtractionContractTests
{
    [Fact]
    public void All_pages_are_immutable_and_conflicting_payment_candidates_remain_separate_human_review_observations()
    {
        var source = new[] { Page(0, "Nominal: Rp 100.000"), Page(1, "Nominal: Rp 200.000") };
        var result = new PdfDocumentExtraction(Reference, ExtractionPurpose.Payment, RasterProvenance, OcrProvenance, source);
        source[0] = Page(0, "Nominal: Rp 999.000");
        var amount = Assert.Single(result.Fields, field => field.Field == "amount");
        Assert.Equal(new[] { 0, 1 }, amount.Observations.Select(row => row.PageIndex));
        Assert.Equal(new[] { "100000.00", "200000.00" }, amount.Observations.Select(row => row.Candidate.Value));
        Assert.Equal(new[] { "human_review_required", "contradictory_pages" }, amount.ReviewReasons);
        Assert.True(result.RequiresHumanReview); Assert.Equal(2, result.Pages.Count);
        Assert.DoesNotContain(result.GetType().GetProperties(), property => property.Name is "Amount" or "Verified" or "Approved");
        var reference = new PdfDocumentCandidateReference(20, Hash('f'), Reference, 1, "amount");
        Assert.Equal(1, PdfDocumentExtractionRecords.CandidateReference(reference)["pageIndex"].AsInt32);
        Assert.Throws<DomainValidationException>(() => new PdfDocumentCandidateReference(20, Hash('f'), Reference, 10, "amount"));
    }

    [Fact]
    public void Missing_duplicate_pages_forged_candidates_mapped_coordinates_and_noncanonical_text_are_refused()
    {
        Assert.Throws<DomainValidationException>(() => new PdfDocumentExtraction(Reference, ExtractionPurpose.Payment, RasterProvenance, OcrProvenance, [Page(1, "Nominal: Rp 100.000")]));
        Assert.Throws<DomainValidationException>(() => new PdfDocumentExtraction(Reference, ExtractionPurpose.Payment, RasterProvenance, OcrProvenance, [Page(0, "Nominal: Rp 100.000"), Page(0, "Nominal: Rp 100.000")]));
        var page = Page(0, "Nominal: Rp 100.000");
        var fields = page.Fields.Select(field => field.Field == "amount" ? new PdfDocumentFieldCandidate(field.Field, "200000.00", field.Confidence, field.ImageBox, field.PdfCanvasCorners, field.Flags) : field).ToArray();
        var forged = new PdfDocumentPageExtraction(page.Raster, page.OcrProvenance, page.Preprocessing, page.Lines, fields, page.ReviewReasons);
        Assert.Throws<DomainValidationException>(() => new PdfDocumentExtraction(Reference, ExtractionPurpose.Payment, RasterProvenance, OcrProvenance, [forged]));
        Assert.Throws<DomainValidationException>(() => new PdfDocumentPageExtraction(page.Raster, page.OcrProvenance, page.Preprocessing,
            [new("Nominal: Rp 100.000", .95m, Box, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)])], page.Fields, page.ReviewReasons));
        Assert.Throws<DomainValidationException>(() => new PdfDocumentRecognizedLine("Cafe\u0301", .9m, Box, Corners));
        Assert.Throws<DomainValidationException>(() => new PdfDocumentPoint(double.NaN, 1));
        Assert.Throws<DomainValidationException>(() => new PdfDocumentPreprocessing(0, .1m, true, 200, 100, .95m, .8m, []));
    }

    [Fact]
    public void Supplier_totals_on_repeated_pages_remain_advisory_and_unknown_fields_retain_every_page()
    {
        PdfDocumentPageExtraction SupplierPage(int index) {
            var image = Page(index, "Nominal: Rp 100.000");
            var lines = new[] { new RecognizedLine("Total invoice: Rp 100.000", .95m, Box) };
            var parsed = SupplierDocumentFieldExtractor.Extract(lines);
            var provenance = new ExtractionProvenance(LocalOcrProtocol.Engine, "eng+ind-test", Hash('b'), Hash('d'),
                LocalOcrProtocol.ImagePreprocessing + "/" + SupplierDocumentFieldExtractor.Version);
            return new(image.Raster, provenance, image.Preprocessing, [new(lines[0].Text, .95m, Box, Corners)],
                parsed.Fields.Select(field => new PdfDocumentFieldCandidate(field.Field, field.Value, field.Confidence,
                    field.BoundingBox is null ? null : Box, field.BoundingBox is null ? null : Corners, field.Flags)),
                image.Raster.ReviewReasons.Concat(parsed.QualityFlags).Distinct(StringComparer.Ordinal));
        }
        var first = SupplierPage(0); var result = new PdfDocumentExtraction(Reference, ExtractionPurpose.SupplierInvoice, RasterProvenance, first.OcrProvenance, [first, SupplierPage(1)]);
        var total = Assert.Single(result.Fields, field => field.Field == "total_amount");
        Assert.Equal(new[] { "100000.00", "100000.00" }, total.Observations.Select(row => row.Candidate.Value));
        Assert.Contains("repeated_across_pages", total.ReviewReasons); Assert.DoesNotContain("contradictory_pages", total.ReviewReasons);
        var supplier = Assert.Single(result.Fields, field => field.Field == "supplier_name");
        Assert.Equal(2, supplier.Observations.Count); Assert.All(supplier.Observations, row => Assert.Null(row.Candidate.Value));
        Assert.Contains("unknown_on_page", supplier.ReviewReasons); Assert.True(result.RequiresHumanReview);
    }

    [Fact]
    public void Whole_original_serialization_rechecks_page_hashes_and_rejects_partial_failed_results()
    {
        var success = EngineResult("Nominal: Rp 100.000");
        var output = PdfDocumentExtractionRecords.Output(success);
        Assert.Single(output["pages"].AsBsonArray); Assert.Equal(8, output["fields"].AsBsonArray.Count);
        var badHashPage = success.Pages[0].Raster;
        var forged = new PdfRasterPage(0, badHashPage.Width, badHashPage.Height, 0, Hash('a'), Bytes,
            [72d, 36d], [0d, 0d, 72d, 36d], [new(0, 36), new(72, 36), new(72, 0), new(0, 0)], ["human_review_required"]);
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentExtractionRecords.Output(success with { Pages = [success.Pages[0] with { Raster = forged }] }));
        var failure = success with { Status = "failed", FailureCode = "pdf_document_deadline", FailurePageIndex = 0 };
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentExtractionRecords.Output(failure));
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentExtractionRecords.Failure(failure));
        Assert.Equal(("pdf_document_deadline", (int?)0), PdfDocumentExtractionRecords.Failure(failure with { Pages = [], Fields = [] }));
    }

    [Fact]
    public void Persisted_output_reconstruction_keeps_original_reference_and_detects_deleted_observations_or_changed_page_provenance()
    {
        var engine = EngineResult("Nominal: Rp 100.000"); var row = Stored(engine);
        var result = PdfDocumentExtractionRecords.Extract(row);
        Assert.Equal(Reference, result.Evidence); Assert.True(result.RequiresHumanReview);
        var omitted = row.DeepClone().AsBsonDocument; omitted["output"]["fields"][0]["observations"] = new BsonArray();
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentExtractionRecords.Extract(omitted));
        var provenance = row.DeepClone().AsBsonDocument; provenance["output"]["pages"][0]["ocrProvenance"]["modelSha256"] = Hash('c');
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentExtractionRecords.Extract(provenance));
        var moved = row.DeepClone().AsBsonDocument; moved["original"]["generation"] = Hash('c');
        Assert.Throws<PersistenceConflictException>(() => PdfDocumentExtractionRecords.Extract(moved));
    }

    private static readonly byte[] Bytes = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
    private static string PageHash => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private static string Hash(char value) => new(value, 64);
    private static EvidenceRef Reference => new(1, 1, Hash('a'));
    private static readonly NormalizedBox Box = new(.1m, .1m, .8m, .1m);
    private static readonly PdfDocumentPoint[] Corners = [new(7.2, 32.4), new(64.8, 32.4), new(64.8, 28.8), new(7.2, 28.8)];
    private static ExtractionProvenance OcrProvenance => new(LocalOcrProtocol.Engine, "eng+ind-test", Hash('b'), Hash('d'), LocalOcrProtocol.PaymentPreprocessing);
    private static PdfDocumentRasterProvenance RasterProvenance => new(PdfRasterProtocol.Pipeline, PdfRasterProtocol.Wrapper, PdfRasterProtocol.Pdfium,
        PdfRasterProtocol.WheelSha256, "12.0.0", Hash('e'));
    private static PdfDocumentPageExtraction Page(int index, string text)
    {
        var parsed = PaymentFieldExtractor.Extract([new(text, .95m, Box)]);
        var raster = new PdfDocumentRasterPage(index, 200, 100, 0, 64, PageHash, [72d, 36d], [0d, 0d, 72d, 36d],
            [new(0, 36), new(72, 36), new(72, 0), new(0, 0)], ["human_review_required"]);
        return new(raster, OcrProvenance, new(0, 0, true, 200, 100, .95m, .8m, []), [new(text, .95m, Box, Corners)],
            parsed.Fields.Select(field => new PdfDocumentFieldCandidate(field.Field, field.Value, field.Confidence,
                field.BoundingBox is null ? null : Box, field.BoundingBox is null ? null : Corners, field.Flags)),
            raster.ReviewReasons.Concat(parsed.QualityFlags).Distinct(StringComparer.Ordinal));
    }
    private static PdfDocumentOcrResult EngineResult(string text)
    {
        var page = Page(0, text); var raster = new PdfRasterPage(0, 200, 100, 0, PageHash, Bytes, [72d, 36d], [0d, 0d, 72d, 36d],
            [new(0, 36), new(72, 36), new(72, 0), new(0, 0)], ["human_review_required"]);
        PdfDocumentOcrField Candidate(PdfDocumentFieldCandidate value) => new(value.Field, value.Value, value.Confidence, value.ImageBox,
            value.PdfCanvasCorners?.Select(point => new PdfRasterPoint(point.X, point.Y)).ToArray(), value.Flags);
        var nativePage = new PdfDocumentOcrPage(raster, OcrProvenance, new(0, 0, true, 200, 100, .95m, .8m, []),
            page.Lines.Select(line => new PdfDocumentOcrLine(line.Text, line.Confidence, line.ImageBox, line.PdfCanvasCorners.Select(point => new PdfRasterPoint(point.X, point.Y)).ToArray())).ToArray(),
            page.Fields.Select(Candidate).ToArray(), page.ReviewReasons);
        var result = new PdfDocumentExtraction(Reference, ExtractionPurpose.Payment, RasterProvenance, OcrProvenance, [page]);
        return new(ObjectId.GenerateNewId().ToString(), Reference, Publication, ExtractionPurpose.Payment,
            new(RasterProvenance.Pipeline, RasterProvenance.Wrapper, RasterProvenance.Pdfium, RasterProvenance.WheelSha256, RasterProvenance.Pillow, RasterProvenance.RuntimeSha256),
            OcrProvenance, "succeeded", null, null, [nativePage], result.Fields.Select(field => new PdfDocumentOcrFieldReview(field.Field,
                field.Observations.Select(observation => new PdfDocumentOcrObservation(observation.PageIndex, Candidate(observation.Candidate))).ToArray(), field.ReviewReasons)).ToArray());
    }
    private static ExpectedPublication Publication => new(new(Hash('e'), 2, 3, 1, Hash('f'), Hash('b')), 1, new(64, Hash('a')));
    private static BsonDocument Stored(PdfDocumentOcrResult value)
    {
        var binding = new BsonDocument { { "reference", new BsonDocument { { "id", 1L }, { "version", 1L }, { "sha256", Hash('a') } } },
            { "reservationId", 2L }, { "storeId", Hash('e') }, { "generation", Hash('b') }, { "length", 64L } };
        return new() { { "sourceKind", "Payment" }, { "purpose", "Payment" }, { "inputMime", "application/pdf" },
            { "pipelineId", PdfDocumentExtractionRecords.PipelineId("Payment", value.RasterProvenance, value.OcrProvenance) }, { "binding", binding },
            { "original", PdfDocumentExtractionRecords.Original(Publication) }, { "rasterProvenance", PdfDocumentExtractionRecords.RasterProvenance(value.RasterProvenance) },
            { "ocrProvenance", PdfDocumentExtractionRecords.OcrProvenance(value.OcrProvenance) }, { "requiresHumanReview", true }, { "pageCount", 1 }, { "output", PdfDocumentExtractionRecords.Output(value) } };
    }
}
