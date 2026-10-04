using UnitToko.Modules.Documents;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record PdfDocumentOcrLine(string Text, decimal Confidence, NormalizedBox ImageBox,
    IReadOnlyList<PdfRasterPoint> PdfCanvasCorners);
internal sealed record PdfDocumentOcrField(string Field, string? Value, decimal? Confidence, NormalizedBox? ImageBox,
    IReadOnlyList<PdfRasterPoint>? PdfCanvasCorners, IReadOnlyList<string> Flags);
internal sealed record PdfDocumentOcrPage(PdfRasterPage Raster, ExtractionProvenance OcrProvenance,
    LocalOcrPreprocessing Preprocessing, IReadOnlyList<PdfDocumentOcrLine> Lines,
    IReadOnlyList<PdfDocumentOcrField> Fields, IReadOnlyList<string> ReviewReasons);
internal sealed record PdfDocumentOcrObservation(int PageIndex, PdfDocumentOcrField Candidate);

/// <summary>Every page's suggestion, including unknowns. There is deliberately no document-wide winning value.</summary>
internal sealed record PdfDocumentOcrFieldReview(string Field, IReadOnlyList<PdfDocumentOcrObservation> Observations,
    IReadOnlyList<string> ReviewReasons);

/// <summary>Read-only computation, never evidence admission or business acceptance. Failed results contain no page output.</summary>
internal sealed record PdfDocumentOcrResult(string JobId, EvidenceRef OriginalEvidence, ExpectedPublication Publication,
    ExtractionPurpose Purpose, PdfRasterProvenance RasterProvenance, ExtractionProvenance OcrProvenance,
    string Status, string? FailureCode, int? FailurePageIndex, IReadOnlyList<PdfDocumentOcrPage> Pages,
    IReadOnlyList<PdfDocumentOcrFieldReview> Fields)
{
    internal bool RequiresHumanReview => true;
}

internal static class PdfDocumentOcrCoordinates
{
    // OCR boxes already map back to the unchanged raster, including its original rotation/skew.
    internal static IReadOnlyList<PdfRasterPoint> Map(PdfRasterPage page, NormalizedBox box)
    {
        if (page.Width is < 1 or > 4096 || page.Height is < 1 or > 4096 || page.RotationDegrees is not (0 or 90 or 180 or 270) || page.PixelCornersInPdfCanvas.Count != 4 ||
            page.CanvasSize.Count != 2 || page.CanvasSize.Any(v => !double.IsFinite(v) || v <= 0) ||
            Math.Ceiling(page.CanvasSize[0] * (25d / 9)) != page.Width || Math.Ceiling(page.CanvasSize[1] * (25d / 9)) != page.Height ||
            page.VisibleBox.Count != 4 || page.VisibleBox.Any(v => !double.IsFinite(v) || Math.Abs(v) > 14400) ||
            page.VisibleBox[2] <= page.VisibleBox[0] || page.VisibleBox[3] <= page.VisibleBox[1]) throw new InvalidDataException();
        var corners = page.PixelCornersInPdfCanvas;
        var width = page.VisibleBox[2] - page.VisibleBox[0]; var height = page.VisibleBox[3] - page.VisibleBox[1];
        if (Math.Abs(page.CanvasSize[0] - (page.RotationDegrees % 180 == 0 ? width : height)) > .001 ||
            Math.Abs(page.CanvasSize[1] - (page.RotationDegrees % 180 == 0 ? height : width)) > .001) throw new InvalidDataException();
        PdfRasterPoint[] unrotated = [new(page.VisibleBox[0], page.VisibleBox[3]), new(page.VisibleBox[2], page.VisibleBox[3]),
            new(page.VisibleBox[2], page.VisibleBox[1]), new(page.VisibleBox[0], page.VisibleBox[1])];
        for (var index = 0; index < 4; index++) {
            var expected = unrotated[(index + 4 - page.RotationDegrees / 90) % 4];
            if (Math.Abs(corners[index].X - expected.X) > .001 || Math.Abs(corners[index].Y - expected.Y) > .001) throw new InvalidDataException();
        }
        foreach (var corner in corners) Require(corner);
        // An affine page rectangle must close. Do not map a malformed or unrelated quadrilateral.
        if (Math.Abs(corners[0].X + corners[2].X - corners[1].X - corners[3].X) > .001 ||
            Math.Abs(corners[0].Y + corners[2].Y - corners[1].Y - corners[3].Y) > .001) throw new InvalidDataException();
        var result = new[] { At(box.X, box.Y), At(box.X + box.Width, box.Y),
            At(box.X + box.Width, box.Y + box.Height), At(box.X, box.Y + box.Height) };
        return Array.AsReadOnly(result);

        PdfRasterPoint At(decimal x, decimal y)
        {
            var point = new PdfRasterPoint(corners[0].X + (double)x * (corners[1].X - corners[0].X) + (double)y * (corners[3].X - corners[0].X),
                corners[0].Y + (double)x * (corners[1].Y - corners[0].Y) + (double)y * (corners[3].Y - corners[0].Y));
            Require(point); return point;
        }
        void Require(PdfRasterPoint point)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < page.VisibleBox[0] - .001 || point.X > page.VisibleBox[2] + .001 ||
                point.Y < page.VisibleBox[1] - .001 || point.Y > page.VisibleBox[3] + .001) throw new InvalidDataException();
        }
    }
}
