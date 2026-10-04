using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record PdfRasterFilePin(string Path, string Sha256);
internal sealed record PdfRasterLimits(int MaximumPages, int MaximumPagePixels, int MaximumTotalPixels, int MaximumOutputBytes)
{
    internal void RequireValid()
    {
        if (MaximumPages is < 1 or > 10 || MaximumPagePixels is < 1 or > 4_000_000 ||
            MaximumTotalPixels < MaximumPagePixels || MaximumTotalPixels > 25_000_000 || MaximumOutputBytes is < 4096 or > 16 * 1024 * 1024)
            throw new InvalidDataException();
    }
}
internal sealed record PdfRasterOptions(string PythonExecutable, string HelperPath, string PackageRoot, string PillowRoot,
    string WheelPath, string PillowVersion, IReadOnlyList<PdfRasterFilePin> Files, PdfRasterLimits Limits,
    long MaximumMemoryBytes, TimeSpan Timeout);
internal sealed record PdfRasterProvenance(string Pipeline, string Wrapper, string Pdfium, string WheelSha256, string Pillow, string RuntimeSha256);
internal sealed record PdfRasterPoint(double X, double Y);

/// <summary>Complete validated page bytes only; the original PDF remains the evidence owner.</summary>
internal sealed class PdfRasterPage
{
    private readonly byte[] png;
    internal int PageIndex { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int RotationDegrees { get; }
    internal string Sha256 { get; }
    internal long Length => png.LongLength;
    internal IReadOnlyList<double> CanvasSize { get; }
    internal IReadOnlyList<double> VisibleBox { get; }
    internal IReadOnlyList<PdfRasterPoint> PixelCornersInPdfCanvas { get; }
    internal IReadOnlyList<string> ReviewReasons { get; }
    internal PdfRasterPage(int index, int width, int height, int rotation, string hash, byte[] bytes,
        double[] canvas, double[] box, PdfRasterPoint[] corners, string[] reasons)
    {
        PageIndex = index; Width = width; Height = height; RotationDegrees = rotation; Sha256 = hash; png = bytes;
        CanvasSize = Array.AsReadOnly(canvas); VisibleBox = Array.AsReadOnly(box);
        PixelCornersInPdfCanvas = Array.AsReadOnly(corners); ReviewReasons = Array.AsReadOnly(reasons);
    }
    internal Stream OpenPng() => new MemoryStream(png, writable: false);
}
internal sealed record PdfRasterResult(string Status, string? FailureCode, ExpectedPublication Original,
    PdfRasterProvenance Provenance, IReadOnlyList<PdfRasterPage> Pages);
