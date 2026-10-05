using System.Security.Cryptography;
using MongoDB.Bson;
using UnitToko.Modules.Documents;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed partial class PdfDocumentExtractionRecords
{
    internal static (string Code, int? PageIndex) Failure(PdfDocumentOcrResult value)
    {
        if (value is null || value.Status != "failed" || value.Pages is null || value.Fields is null || value.Pages.Count != 0 || value.Fields.Count != 0 || value.FailureCode is null ||
            !UnitToko.Platform.Mongo.Migrations.PdfDocumentExtractionSchemas.SafeFailure()["enum"].AsBsonArray.Contains(value.FailureCode) ||
            value.FailurePageIndex is < 0 or >= PdfDocumentExtractionContract.MaximumPages) throw Broken();
        return (value.FailureCode, value.FailurePageIndex);
    }
    /// <summary>Only a complete verified whole-original success can enter the result owner; failures use a separate bounded record.</summary>
    internal static BsonDocument Output(PdfDocumentOcrResult value)
    {
        if (value is null || value.Status != "succeeded" || value.FailureCode is not null || value.FailurePageIndex is not null || value.OriginalEvidence is null ||
            value.Publication is null || value.Pages is null || value.Fields is null || value.Pages.Any(page => page is null) ||
            !ObjectId.TryParse(value.JobId, out var jobId) || value.JobId != jobId.ToString() || value.OriginalEvidence.Sha256 != value.Publication.Content.Sha256 ||
            value.Publication.Content.Length > PdfDocumentExtractionContract.MaximumOriginalBytes || value.Pages.Count is < 1 or > 10 ||
            value.Fields.Count != 8 || !value.RequiresHumanReview) throw Broken();
        var pages = new List<PdfDocumentPageExtraction>();
        foreach (var page in value.Pages) {
            if (page.Raster.PageIndex != pages.Count) throw Broken();
            using var png = page.Raster.OpenPng();
            if (png.Length != page.Raster.Length || Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() != page.Raster.Sha256) throw Broken();
            var raster = page.Raster;
            var metadata = new PdfDocumentRasterPage(raster.PageIndex, raster.Width, raster.Height, raster.RotationDegrees, raster.Length, raster.Sha256,
                raster.CanvasSize, raster.VisibleBox, raster.PixelCornersInPdfCanvas.Select(Point), raster.ReviewReasons);
            var preprocessing = page.Preprocessing;
            pages.Add(new(metadata, page.OcrProvenance, new(preprocessing.OrientationDegrees, preprocessing.DeskewDegrees, preprocessing.ContrastApplied,
                preprocessing.WorkingWidth, preprocessing.WorkingHeight, preprocessing.OrientationScore, preprocessing.OrientationMargin, preprocessing.ReviewReasons),
                page.Lines.Select(line => new PdfDocumentRecognizedLine(line.Text, line.Confidence, line.ImageBox, line.PdfCanvasCorners.Select(Point))),
                page.Fields.Select(Field), page.ReviewReasons));
        }
        var result = new PdfDocumentExtraction(value.OriginalEvidence, value.Purpose,
            new(value.RasterProvenance.Pipeline, value.RasterProvenance.Wrapper, value.RasterProvenance.Pdfium, value.RasterProvenance.WheelSha256,
                value.RasterProvenance.Pillow, value.RasterProvenance.RuntimeSha256), value.OcrProvenance, pages);
        var expected = Output(result);
        var actualReviews = new BsonArray(value.Fields.Select(review => new BsonDocument { { "field", review.Field },
            { "observations", new BsonArray(review.Observations.Select(observation => new BsonDocument { { "pageIndex", observation.PageIndex }, { "candidate", Field(Field(observation.Candidate)) } })) },
            { "reviewReasons", new BsonArray(review.ReviewReasons) } }));
        if (!expected["fields"].Equals(actualReviews)) throw Broken();
        return expected;
    }
    private static PdfDocumentFieldCandidate Field(PdfDocumentOcrField value) => new(value.Field, value.Value, value.Confidence, value.ImageBox,
        value.PdfCanvasCorners?.Select(Point), value.Flags);
    private static PdfDocumentPoint Point(PdfRasterPoint value) => new(value.X, value.Y);
    private static BsonDocument Point(PdfDocumentPoint value) => new() { { "x", value.X }, { "y", value.Y } };
    private static BsonArray Corners(IReadOnlyList<PdfDocumentPoint> value) => new(value.Select(Point));
    private static BsonArray Box(NormalizedBox value) => new(value.Values.Select(v => new BsonDecimal128(v)));
    private static BsonDocument Field(PdfDocumentFieldCandidate value) => new() { { "field", value.Field },
        { "value", value.Value is null ? BsonNull.Value : new BsonString(value.Value) }, { "confidence", value.Confidence is { } confidence ? new BsonDecimal128(confidence) : BsonNull.Value },
        { "imageBox", value.ImageBox is null ? BsonNull.Value : Box(value.ImageBox) }, { "pdfCanvasCorners", value.PdfCanvasCorners is null ? BsonNull.Value : Corners(value.PdfCanvasCorners) },
        { "flags", new BsonArray(value.Flags) } };
    private static BsonDocument Raster(PdfDocumentRasterPage value) => new() { { "pageIndex", value.PageIndex }, { "width", value.Width }, { "height", value.Height },
        { "length", value.Length }, { "sha256", value.Sha256 }, { "rotationDegrees", value.RotationDegrees }, { "canvasSize", new BsonArray(value.CanvasSize) },
        { "visibleBox", new BsonArray(value.VisibleBox) }, { "pixelCornersInPdfCanvas", Corners(value.PixelCornersInPdfCanvas) }, { "reviewReasons", new BsonArray(value.ReviewReasons) } };
    private static BsonDocument Preprocessing(PdfDocumentPreprocessing value) => new() { { "orientationDegrees", value.OrientationDegrees }, { "deskewDegrees", new Decimal128(value.DeskewDegrees) },
        { "contrastApplied", value.ContrastApplied }, { "workingWidth", value.WorkingWidth }, { "workingHeight", value.WorkingHeight }, { "orientationScore", new Decimal128(value.OrientationScore) },
        { "orientationMargin", new Decimal128(value.OrientationMargin) }, { "reviewReasons", new BsonArray(value.ReviewReasons) } };
    private static BsonDocument Output(PdfDocumentExtraction value)
    {
        var output = new BsonDocument { { "pages", new BsonArray(value.Pages.Select(page => new BsonDocument {
            { "raster", Raster(page.Raster) }, { "ocrProvenance", OcrProvenance(page.OcrProvenance) }, { "preprocessing", Preprocessing(page.Preprocessing) },
            { "lines", new BsonArray(page.Lines.Select(line => new BsonDocument { { "text", line.Text }, { "confidence", new Decimal128(line.Confidence) }, { "imageBox", Box(line.ImageBox) }, { "pdfCanvasCorners", Corners(line.PdfCanvasCorners) } })) },
            { "fields", new BsonArray(page.Fields.Select(Field)) }, { "reviewReasons", new BsonArray(page.ReviewReasons) } })) },
            { "fields", new BsonArray(value.Fields.Select(review => new BsonDocument { { "field", review.Field },
                { "observations", new BsonArray(review.Observations.Select(observation => new BsonDocument { { "pageIndex", observation.PageIndex }, { "candidate", Field(observation.Candidate) } })) },
                { "reviewReasons", new BsonArray(review.ReviewReasons) } })) } };
        if (output.ToBson().Length > PdfDocumentExtractionContract.MaximumStoredOutputBytes) throw Broken();
        return output;
    }
    internal static PdfDocumentExtraction Extract(BsonDocument row)
    {
        try {
            RequireOriginal(row); RequireProvenance(row);
            var purpose = PurposeForSource(row["sourceKind"].AsString);
            if (row["purpose"].AsString != purpose.ToString() || row["inputMime"] != PdfDocumentExtractionContract.InputMime || row["requiresHumanReview"] != true) throw Broken();
            var raster = row["rasterProvenance"].AsBsonDocument;
            var pages = row["output"]["pages"].AsBsonArray.Select(value => Page(value.AsBsonDocument)).ToArray();
            var result = new PdfDocumentExtraction(Reference(row["binding"]["reference"].AsBsonDocument), purpose,
                new(raster["pipeline"].AsString, raster["wrapper"].AsString, raster["pdfium"].AsString, raster["wheelSha256"].AsString, raster["pillow"].AsString, raster["runtimeSha256"].AsString),
                ExtractionRecords.Provenance(row["ocrProvenance"].AsBsonDocument), pages);
            if (row["pageCount"].AsInt32 != result.Pages.Count || !row["output"].Equals(Output(result))) throw Broken(); return result;
        } catch (Exception error) when (error is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException or
            InvalidOperationException) { throw Broken(); }
    }
    private static PdfDocumentPageExtraction Page(BsonDocument value)
    {
        var raster = value["raster"].AsBsonDocument; var preprocessing = value["preprocessing"].AsBsonDocument;
        return new(new(raster["pageIndex"].AsInt32, raster["width"].AsInt32, raster["height"].AsInt32, raster["rotationDegrees"].AsInt32, raster["length"].AsInt64, raster["sha256"].AsString,
            raster["canvasSize"].AsBsonArray.Select(v => v.AsDouble), raster["visibleBox"].AsBsonArray.Select(v => v.AsDouble), Points(raster["pixelCornersInPdfCanvas"]), Strings(raster["reviewReasons"])),
            ExtractionRecords.Provenance(value["ocrProvenance"].AsBsonDocument),
            new(preprocessing["orientationDegrees"].AsInt32, preprocessing["deskewDegrees"].AsDecimal, preprocessing["contrastApplied"].AsBoolean,
                preprocessing["workingWidth"].AsInt32, preprocessing["workingHeight"].AsInt32, preprocessing["orientationScore"].AsDecimal, preprocessing["orientationMargin"].AsDecimal, Strings(preprocessing["reviewReasons"])),
            value["lines"].AsBsonArray.Select(line => new PdfDocumentRecognizedLine(line["text"].AsString, line["confidence"].AsDecimal, Box(line["imageBox"]), Points(line["pdfCanvasCorners"]))),
            value["fields"].AsBsonArray.Select(field => Field(field.AsBsonDocument)), Strings(value["reviewReasons"]));
    }
    private static PdfDocumentFieldCandidate Field(BsonDocument value) => new(value["field"].AsString, value["value"].IsBsonNull ? null : value["value"].AsString,
        value["confidence"].IsBsonNull ? null : value["confidence"].AsDecimal, value["imageBox"].IsBsonNull ? null : Box(value["imageBox"]),
        value["pdfCanvasCorners"].IsBsonNull ? null : Points(value["pdfCanvasCorners"]), Strings(value["flags"]));
    private static NormalizedBox Box(BsonValue value) { var box = value.AsBsonArray; if (box.Count != 4) throw Broken(); return new(box[0].AsDecimal, box[1].AsDecimal, box[2].AsDecimal, box[3].AsDecimal); }
    private static IEnumerable<PdfDocumentPoint> Points(BsonValue value) => value.AsBsonArray.Select(point => new PdfDocumentPoint(point["x"].AsDouble, point["y"].AsDouble));
    private static IEnumerable<string> Strings(BsonValue value) => value.AsBsonArray.Select(v => v.AsString);
}
