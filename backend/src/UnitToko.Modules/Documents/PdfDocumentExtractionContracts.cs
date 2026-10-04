using System.Collections.ObjectModel;
using System.Globalization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

/// <summary>The original PDF owns every observation. Nothing here is a bank, payment or supplier decision.</summary>
public static class PdfDocumentExtractionContract
{
    public const string PaymentJobKind = "payment.document-ocr.requested";
    public const string SupplierJobKind = "supplierdocument.document-ocr.requested";
    public const string InputMime = "application/pdf";
    public const int MaximumPages = 10;
    public const int MaximumLinesPerPage = 128;
    public const int MaximumTextPerPage = 16384;
    public const int MaximumTextPerDocument = 65536;
    public const int MaximumStoredOutputBytes = 2 * 1024 * 1024;
    public const long MaximumOriginalBytes = 10L * 1024 * 1024;
}

public sealed record PdfDocumentRasterProvenance
{
    public string Pipeline { get; }
    public string Wrapper { get; }
    public string Pdfium { get; }
    public string WheelSha256 { get; }
    public string Pillow { get; }
    public string RuntimeSha256 { get; }
    public PdfDocumentRasterProvenance(string pipeline, string wrapper, string pdfium, string wheelSha256, string pillow, string runtimeSha256)
    {
        PdfDocumentExtractionRules.Text(pipeline, 128); PdfDocumentExtractionRules.Text(wrapper, 128);
        PdfDocumentExtractionRules.Text(pdfium, 128); PdfDocumentExtractionRules.Text(pillow, 32);
        ExtractionBounds.Hash(wheelSha256); ExtractionBounds.Hash(runtimeSha256);
        Pipeline = pipeline; Wrapper = wrapper; Pdfium = pdfium; WheelSha256 = wheelSha256; Pillow = pillow; RuntimeSha256 = runtimeSha256;
    }
}

public sealed record PdfDocumentPoint
{
    public double X { get; }
    public double Y { get; }
    public PdfDocumentPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 14400 || Math.Abs(y) > 14400) throw PdfDocumentExtractionRules.Invalid();
        X = x; Y = y;
    }
}

public sealed record PdfDocumentRasterPage
{
    public int PageIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public int RotationDegrees { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public IReadOnlyList<double> CanvasSize { get; }
    public IReadOnlyList<double> VisibleBox { get; }
    public IReadOnlyList<PdfDocumentPoint> PixelCornersInPdfCanvas { get; }
    public IReadOnlyList<string> ReviewReasons { get; }
    public PdfDocumentRasterPage(int pageIndex, int width, int height, int rotationDegrees, long length, string sha256,
        IEnumerable<double> canvasSize, IEnumerable<double> visibleBox, IEnumerable<PdfDocumentPoint> pixelCornersInPdfCanvas, IEnumerable<string> reviewReasons)
    {
        if (pageIndex is < 0 or >= PdfDocumentExtractionContract.MaximumPages || width is < 1 or > 4096 || height is < 1 or > 4096 ||
            (long)width * height > 4_000_000 || rotationDegrees is not (0 or 90 or 180 or 270) || length is < 57 or > 10 * 1024 * 1024) throw PdfDocumentExtractionRules.Invalid();
        ExtractionBounds.Hash(sha256); PageIndex = pageIndex; Width = width; Height = height; RotationDegrees = rotationDegrees; Length = length; Sha256 = sha256;
        CanvasSize = PdfDocumentExtractionRules.Copy(canvasSize, 2, 2); VisibleBox = PdfDocumentExtractionRules.Copy(visibleBox, 4, 4);
        PixelCornersInPdfCanvas = PdfDocumentExtractionRules.Copy(pixelCornersInPdfCanvas, 4, 4); ReviewReasons = ExtractionBounds.Flags(reviewReasons);
        if (CanvasSize.Concat(VisibleBox).Any(v => !double.IsFinite(v) || Math.Abs(v) > 14400) || CanvasSize.Any(v => v <= 0) ||
            VisibleBox[2] <= VisibleBox[0] || VisibleBox[3] <= VisibleBox[1] || PixelCornersInPdfCanvas.Any(v => v is null) ||
            ReviewReasons.Count is < 1 or > 2 || ReviewReasons[0] != "human_review_required" || ReviewReasons.Count == 2 && ReviewReasons[1] != "no_visible_marks" ||
            Math.Ceiling(CanvasSize[0] * (25d / 9)) != Width || Math.Ceiling(CanvasSize[1] * (25d / 9)) != Height ||
            !PdfDocumentExtractionRules.Near(CanvasSize[0], rotationDegrees % 180 == 0 ? VisibleBox[2] - VisibleBox[0] : VisibleBox[3] - VisibleBox[1]) ||
            !PdfDocumentExtractionRules.Near(CanvasSize[1], rotationDegrees % 180 == 0 ? VisibleBox[3] - VisibleBox[1] : VisibleBox[2] - VisibleBox[0])) throw PdfDocumentExtractionRules.Invalid();
        PdfDocumentPoint[] expected = [new(VisibleBox[0], VisibleBox[3]), new(VisibleBox[2], VisibleBox[3]), new(VisibleBox[2], VisibleBox[1]), new(VisibleBox[0], VisibleBox[1])];
        for (var index = 0; index < 4; index++)
            if (!PdfDocumentExtractionRules.Same(PixelCornersInPdfCanvas[index], expected[(index + 4 - rotationDegrees / 90) % 4])) throw PdfDocumentExtractionRules.Invalid();
    }
}

public sealed record PdfDocumentPreprocessing
{
    public int OrientationDegrees { get; }
    public decimal DeskewDegrees { get; }
    public bool ContrastApplied { get; }
    public int WorkingWidth { get; }
    public int WorkingHeight { get; }
    public decimal OrientationScore { get; }
    public decimal OrientationMargin { get; }
    public IReadOnlyList<string> ReviewReasons { get; }
    public PdfDocumentPreprocessing(int orientationDegrees, decimal deskewDegrees, bool contrastApplied, int workingWidth, int workingHeight,
        decimal orientationScore, decimal orientationMargin, IEnumerable<string> reviewReasons)
    {
        if (orientationDegrees is not (0 or 90 or 180 or 270) || deskewDegrees is < -10 or > 10 || deskewDegrees * 4 != decimal.Truncate(deskewDegrees * 4) ||
            workingWidth is < 0 or > 4800 || workingHeight is < 0 or > 4800 || (long)workingWidth * workingHeight > 5_500_000 ||
            orientationScore is < 0 or > 1 || orientationMargin < 0 || orientationMargin > orientationScore) throw PdfDocumentExtractionRules.Invalid();
        ReviewReasons = ExtractionBounds.Flags(reviewReasons);
        if (ReviewReasons.Count > 1 || ReviewReasons.Any(v => v is not ("no_text" or "orientation_unresolved" or "skew_out_of_range" or "low_text_confidence"))) throw PdfDocumentExtractionRules.Invalid();
        OrientationDegrees = orientationDegrees; DeskewDegrees = deskewDegrees; ContrastApplied = contrastApplied; WorkingWidth = workingWidth;
        WorkingHeight = workingHeight; OrientationScore = orientationScore; OrientationMargin = orientationMargin;
    }
    internal void RequireLines(int count)
    {
        var fallback = ReviewReasons.Any(v => v is "orientation_unresolved" or "skew_out_of_range") || WorkingWidth == 0 || WorkingHeight == 0;
        if (fallback ? count != 0 || WorkingWidth != 0 || WorkingHeight != 0 || OrientationDegrees != 0 || DeskewDegrees != 0 || ReviewReasons.Count != 1 :
            OrientationScore < .65m || OrientationMargin < .10m || (count == 0) != ReviewReasons.Contains("no_text")) throw PdfDocumentExtractionRules.Invalid();
    }
}

public sealed record PdfDocumentRecognizedLine
{
    public string Text { get; }
    public decimal Confidence { get; }
    public NormalizedBox ImageBox { get; }
    public IReadOnlyList<PdfDocumentPoint> PdfCanvasCorners { get; }
    public PdfDocumentRecognizedLine(string text, decimal confidence, NormalizedBox imageBox, IEnumerable<PdfDocumentPoint> pdfCanvasCorners)
    {
        PdfDocumentExtractionRules.Text(text, 512); ExtractionBounds.Confidence(confidence);
        Text = text; Confidence = confidence; ImageBox = imageBox ?? throw PdfDocumentExtractionRules.Invalid();
        PdfCanvasCorners = PdfDocumentExtractionRules.Copy(pdfCanvasCorners, 4, 4);
        if (PdfCanvasCorners.Any(v => v is null)) throw PdfDocumentExtractionRules.Invalid();
    }
}

public sealed record PdfDocumentFieldCandidate
{
    public string Field { get; }
    public string? Value { get; }
    public decimal? Confidence { get; }
    public NormalizedBox? ImageBox { get; }
    public IReadOnlyList<PdfDocumentPoint>? PdfCanvasCorners { get; }
    public IReadOnlyList<string> Flags { get; }
    public PdfDocumentFieldCandidate(string field, string? value, decimal? confidence, NormalizedBox? imageBox,
        IEnumerable<PdfDocumentPoint>? pdfCanvasCorners, IEnumerable<string> flags)
    {
        if (!PaymentFieldExtractor.Fields.Contains(field, StringComparer.Ordinal) && !SupplierDocumentFieldExtractor.Fields.Contains(field, StringComparer.Ordinal)) throw PdfDocumentExtractionRules.Invalid();
        if (value is not null) PdfDocumentExtractionRules.Text(value, 256); if (confidence is { } c) ExtractionBounds.Confidence(c);
        if ((imageBox is null) != (pdfCanvasCorners is null)) throw PdfDocumentExtractionRules.Invalid();
        Field = field; Value = value; Confidence = confidence; ImageBox = imageBox;
        PdfCanvasCorners = pdfCanvasCorners is null ? null : PdfDocumentExtractionRules.Copy(pdfCanvasCorners, 4, 4);
        if (PdfCanvasCorners?.Any(v => v is null) == true) throw PdfDocumentExtractionRules.Invalid();
        Flags = ExtractionBounds.Flags(flags);
    }
}

public sealed record PdfDocumentPageExtraction
{
    public PdfDocumentRasterPage Raster { get; }
    public ExtractionProvenance OcrProvenance { get; }
    public PdfDocumentPreprocessing Preprocessing { get; }
    public IReadOnlyList<PdfDocumentRecognizedLine> Lines { get; }
    public IReadOnlyList<PdfDocumentFieldCandidate> Fields { get; }
    public IReadOnlyList<string> ReviewReasons { get; }
    public PdfDocumentPageExtraction(PdfDocumentRasterPage raster, ExtractionProvenance ocrProvenance, PdfDocumentPreprocessing preprocessing,
        IEnumerable<PdfDocumentRecognizedLine> lines, IEnumerable<PdfDocumentFieldCandidate> fields, IEnumerable<string> reviewReasons)
    {
        Raster = raster ?? throw PdfDocumentExtractionRules.Invalid(); OcrProvenance = ocrProvenance ?? throw PdfDocumentExtractionRules.Invalid();
        Preprocessing = preprocessing ?? throw PdfDocumentExtractionRules.Invalid(); Lines = PdfDocumentExtractionRules.Copy(lines, 0, 128);
        Fields = PdfDocumentExtractionRules.Copy(fields, 8, 8); ReviewReasons = ExtractionBounds.Flags(reviewReasons);
        if (Lines.Any(v => v is null) || Fields.Any(v => v is null) || Lines.Sum(v => v.Text.Length) > PdfDocumentExtractionContract.MaximumTextPerPage) throw PdfDocumentExtractionRules.Invalid();
        Preprocessing.RequireLines(Lines.Count);
        foreach (var line in Lines) PdfDocumentExtractionRules.RequireCorners(Raster, line.ImageBox, line.PdfCanvasCorners);
        foreach (var field in Fields) if (field.ImageBox is { } box) PdfDocumentExtractionRules.RequireCorners(Raster, box, field.PdfCanvasCorners!);
    }
}

public sealed record PdfDocumentFieldObservation(int PageIndex, PdfDocumentFieldCandidate Candidate);
public sealed record PdfDocumentFieldReview(string Field, IReadOnlyList<PdfDocumentFieldObservation> Observations, IReadOnlyList<string> ReviewReasons);

public sealed record PdfDocumentExtraction
{
    public EvidenceRef Evidence { get; }
    public ExtractionPurpose Purpose { get; }
    public PdfDocumentRasterProvenance RasterProvenance { get; }
    public ExtractionProvenance OcrProvenance { get; }
    public IReadOnlyList<PdfDocumentPageExtraction> Pages { get; }
    public IReadOnlyList<PdfDocumentFieldReview> Fields { get; }
    public bool RequiresHumanReview => true;
    public PdfDocumentExtraction(EvidenceRef evidence, ExtractionPurpose purpose, PdfDocumentRasterProvenance rasterProvenance,
        ExtractionProvenance ocrProvenance, IEnumerable<PdfDocumentPageExtraction> pages)
    {
        Evidence = evidence ?? throw PdfDocumentExtractionRules.Invalid(); Purpose = purpose;
        RasterProvenance = rasterProvenance ?? throw PdfDocumentExtractionRules.Invalid(); OcrProvenance = ocrProvenance ?? throw PdfDocumentExtractionRules.Invalid();
        Pages = PdfDocumentExtractionRules.Copy(pages, 1, PdfDocumentExtractionContract.MaximumPages);
        var names = PdfDocumentExtractionRules.Names(purpose);
        if (Pages.Any(v => v is null) || Pages.Sum(v => v.Lines.Sum(line => line.Text.Length)) > PdfDocumentExtractionContract.MaximumTextPerDocument ||
            Pages.Sum(v => (long)v.Raster.Width * v.Raster.Height) > 25_000_000 || Pages.Sum(v => v.Raster.Length) > 12 * 1024 * 1024) throw PdfDocumentExtractionRules.Invalid();
        for (var index = 0; index < Pages.Count; index++) {
            var page = Pages[index];
            if (page.Raster.PageIndex != index || page.OcrProvenance != OcrProvenance || !page.Fields.Select(v => v.Field).SequenceEqual(names, StringComparer.Ordinal)) throw PdfDocumentExtractionRules.Invalid();
            PdfDocumentExtractionRules.RequireParsed(page, purpose);
        }
        Fields = Array.AsReadOnly(names.Select(name => {
            var observations = Pages.Select(page => new PdfDocumentFieldObservation(page.Raster.PageIndex, page.Fields.Single(field => field.Field == name))).ToArray();
            var reasons = new List<string> { "human_review_required" };
            if (observations.Any(v => v.Candidate.Value is null)) reasons.Add("unknown_on_page");
            if (observations.Any(v => v.Candidate.Flags.Contains("ambiguous"))) reasons.Add("ambiguous_on_page");
            var known = observations.Select(v => v.Candidate.Value).OfType<string>().ToArray();
            if (known.Distinct(StringComparer.Ordinal).Count() > 1) reasons.Add("contradictory_pages"); else if (known.Length > 1) reasons.Add("repeated_across_pages");
            return new PdfDocumentFieldReview(name, Array.AsReadOnly(observations), reasons.AsReadOnly());
        }).ToArray());
    }
}

/// <summary>Persist this exact reference beside a human correction; a page or candidate alone is insufficient.</summary>
public sealed record PdfDocumentCandidateReference
{
    public long ResultId { get; }
    public string ResultHash { get; }
    public EvidenceRef Evidence { get; }
    public int PageIndex { get; }
    public string Field { get; }
    public PdfDocumentCandidateReference(long resultId, string resultHash, EvidenceRef evidence, int pageIndex, string field)
    {
        if (resultId <= 0 || resultId == long.MaxValue || pageIndex is < 0 or >= PdfDocumentExtractionContract.MaximumPages ||
            !PaymentFieldExtractor.Fields.Contains(field, StringComparer.Ordinal) && !SupplierDocumentFieldExtractor.Fields.Contains(field, StringComparer.Ordinal)) throw PdfDocumentExtractionRules.Invalid();
        ExtractionBounds.Hash(resultHash); ResultId = resultId; ResultHash = resultHash; Evidence = evidence ?? throw PdfDocumentExtractionRules.Invalid(); PageIndex = pageIndex; Field = field;
    }
}

public sealed record PdfDocumentExtractionView(long SourceId, long SourceRevision, long SubmissionId, string SourceKind, EvidenceRef Evidence,
    string State, long? ResultId, string? ResultHash, DateTimeOffset? ExtractedAt, PdfDocumentExtraction? Extraction, string? FailureCode);

internal static class PdfDocumentExtractionRules
{
    internal static DomainValidationException Invalid() => new("pdf_document_extraction_invalid", "Pemrosesan PDF tidak lengkap atau melampaui batas; dokumen asli tetap harus ditinjau petugas.");
    internal static void Text(string value, int maximum) {
        ExtractionBounds.Text(value, maximum);
        if (!value.IsNormalized(System.Text.NormalizationForm.FormC) || value.Any(char.IsSurrogate)) {
            // Valid non-BMP Unicode is allowed; malformed UTF-16 and noncanonical text are not.
            if (!value.IsNormalized(System.Text.NormalizationForm.FormC) || !WellFormed(value)) throw Invalid();
        }
    }
    private static bool WellFormed(string text) {
        for (var index = 0; index < text.Length; index++) {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[++index])) return false;
        }
        return true;
    }
    internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values, int minimum, int maximum) {
        if (values is null) throw Invalid(); var copied = values.Take(maximum + 1).ToArray();
        if (copied.Length < minimum || copied.Length > maximum) throw Invalid(); return Array.AsReadOnly(copied);
    }
    internal static IReadOnlyList<string> Names(ExtractionPurpose purpose) => purpose switch {
        ExtractionPurpose.Payment => PaymentFieldExtractor.Fields, ExtractionPurpose.SupplierInvoice => SupplierDocumentFieldExtractor.Fields, _ => throw Invalid() };
    internal static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= .001;
    internal static bool Same(PdfDocumentPoint a, PdfDocumentPoint b) => Near(a.X, b.X) && Near(a.Y, b.Y);
    internal static void RequireCorners(PdfDocumentRasterPage page, NormalizedBox box, IReadOnlyList<PdfDocumentPoint> corners) {
        if (corners.Count != 4) throw Invalid(); var source = page.PixelCornersInPdfCanvas;
        PdfDocumentPoint At(decimal x, decimal y) => new(source[0].X + (double)x * (source[1].X - source[0].X) + (double)y * (source[3].X - source[0].X),
            source[0].Y + (double)x * (source[1].Y - source[0].Y) + (double)y * (source[3].Y - source[0].Y));
        PdfDocumentPoint[] expected = [At(box.X, box.Y), At(box.X + box.Width, box.Y), At(box.X + box.Width, box.Y + box.Height), At(box.X, box.Y + box.Height)];
        if (corners.Where((point, index) => !Same(point, expected[index])).Any()) throw Invalid();
    }
    internal static void RequireParsed(PdfDocumentPageExtraction page, ExtractionPurpose purpose) {
        var lines = page.Lines.Select(v => new RecognizedLine(v.Text, v.Confidence, v.ImageBox)).ToArray();
        IReadOnlyList<string> quality;
        if (purpose == ExtractionPurpose.Payment) {
            var parsed = PaymentFieldExtractor.Extract(lines); quality = parsed.QualityFlags;
            for (var index = 0; index < parsed.Fields.Count; index++) {
                var actual = page.Fields[index]; var expected = parsed.Fields[index]; RequireCandidate(actual, expected.Value, expected.Confidence, expected.BoundingBox, expected.Flags);
            }
        } else {
            var parsed = SupplierDocumentFieldExtractor.Extract(lines); quality = parsed.QualityFlags;
            for (var index = 0; index < parsed.Fields.Count; index++) {
                var actual = page.Fields[index]; var expected = parsed.Fields[index]; RequireCandidate(actual, expected.Value, expected.Confidence, expected.BoundingBox, expected.Flags);
            }
        }
        if (!page.ReviewReasons.SequenceEqual(page.Raster.ReviewReasons.Concat(page.Preprocessing.ReviewReasons).Concat(quality).Distinct(StringComparer.Ordinal), StringComparer.Ordinal)) throw Invalid();
    }
    private static void RequireCandidate(PdfDocumentFieldCandidate actual, string? value, decimal? confidence, IReadOnlyList<decimal>? box, IReadOnlyList<string> flags) {
        if (actual.Value != value || actual.Confidence != confidence || (actual.ImageBox is null) != (box is null) ||
            box is not null && !actual.ImageBox!.Values.SequenceEqual(box) || !actual.Flags.SequenceEqual(flags, StringComparer.Ordinal)) throw Invalid();
    }
}
