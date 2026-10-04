using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class WindowsPdfRasterTests
{
    [WindowsStorageFact]
    public async Task Actual_pinned_parser_reads_two_rotated_pages_from_verified_private_original_and_preserves_ownership()
    {
        var bytes = Pdf(); using var root = new StorageRoot(); var expected = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, expected, bytes); var options = Options();
        var result = await new WindowsPdfRaster(options).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddSeconds(60), default);
        Assert.True(result.Status == "rasterized", result.FailureCode); Assert.Null(result.FailureCode); Assert.Equal(expected, result.Original);
        Assert.Equal(PdfRasterProtocol.Pipeline, result.Provenance.Pipeline); Assert.Equal(PdfRasterProtocol.WheelSha256, result.Provenance.WheelSha256);
        Assert.Equal(2, result.Pages.Count); Assert.Equal(new[] { 0, 1 }, result.Pages.Select(page => page.PageIndex));
        Assert.Equal(new[] { 0, 90 }, result.Pages.Select(page => page.RotationDegrees));
        foreach (var page in result.Pages) {
            Assert.Equal(new[] { "human_review_required" }, page.ReviewReasons); using var image = page.OpenPng();
            Assert.False(image.CanWrite); using var copy = new MemoryStream(); await image.CopyToAsync(copy);
            Assert.Equal(PdfRasterProtocol.Digest(copy.ToArray()), page.Sha256); Assert.False(PdfRasterPng.Require(copy.ToArray(), page.Width, page.Height, default));
        }
        Assert.Throws<IOException>(() => File.Open(root.Original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        await original.DisposeAsync(); UnchangedUnlocked(root, bytes); PinsUnlocked(options);
    }

    [WindowsStorageFact]
    public async Task Malformed_source_wrong_publication_missing_and_changed_file_pins_return_no_pages()
    {
        var options = Options(); var bytes = Pdf(); using var root = new StorageRoot(); var expected = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, expected, bytes);
        Rejected(await new WindowsPdfRaster(options).RasterizeAsync(original, new(expected.Attempt, 2, expected.Content), DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_raster_source_mismatch");
        foreach (var path in new[] { options.HelperPath, options.WheelPath, options.PythonExecutable, Path.Combine(options.PackageRoot, "pypdfium2_raw", "pdfium.dll") }) {
            Assert.Contains(options.Files, pin => pin.Path == path);
            var bad = options with { Files = options.Files.Select(pin => pin.Path == path ? pin with { Sha256 = new string('0', 64) } : pin).ToArray() };
            Rejected(await new WindowsPdfRaster(bad).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_raster_unavailable");
        }
        var missing = options with { Files = options.Files.Where(pin => pin.Path != options.HelperPath).ToArray() };
        Rejected(await new WindowsPdfRaster(missing).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_raster_unavailable");
        await original.DisposeAsync(); UnchangedUnlocked(root, bytes); PinsUnlocked(options);
        foreach (var damaged in new[] { "not PDF"u8.ToArray(), Pdf()[..100] }) {
            using var badRoot = new StorageRoot(); var badExpected = PdfRasterProtocolTests.Expected(damaged);
            await using var badOriginal = await Publish(badRoot, badExpected, damaged);
            Rejected(await new WindowsPdfRaster(options).RasterizeAsync(badOriginal, badExpected, DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_raster_malformed_pdf");
        }
    }

    [WindowsStorageFact]
    public async Task Captured_deadline_and_cancellation_cover_pin_work_and_never_publish_pages()
    {
        var options = Options(); var bytes = Pdf(); using var root = new StorageRoot(); var expected = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, expected, bytes);
        Rejected(await new WindowsPdfRaster(options).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddMilliseconds(-1), default), "pdf_raster_deadline");
        Rejected(await new WindowsPdfRaster(options with { Timeout = TimeSpan.FromTicks(1) }).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddSeconds(60), default), "pdf_raster_deadline");
        using var cancellation = new CancellationTokenSource();
        var running = new WindowsPdfRaster(options).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddSeconds(60), cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        PinsUnlocked(options); Assert.Throws<IOException>(() => File.Delete(root.Original));
        await original.DisposeAsync(); UnchangedUnlocked(root, bytes);
    }

    [WindowsStorageFact]
    public async Task Confined_child_flood_truncation_and_stall_are_fail_closed_and_release_runtime_locks()
    {
        var options = Options(); var bytes = Pdf(); using var root = new StorageRoot(); var expected = PdfRasterProtocolTests.Expected(bytes);
        await using var original = await Publish(root, expected, bytes);
        var helper = Path.Combine(root.Path, "pdf_preprocess.py");
        foreach (var (body, code, seconds) in new[] {
            ("import sys\nsys.stdin.buffer.read()\nsys.stdout.write('x'*16777217)\n", "pdf_raster_process_failed", 30),
            ("import sys\nsys.stdin.buffer.read()\nsys.stdout.write('{')\n", "pdf_raster_invalid_output", 30),
            ("import sys,time\nsys.stdin.buffer.read()\ntime.sleep(30)\n", "pdf_raster_deadline", 3) }) {
            await File.WriteAllTextAsync(helper, body, new UTF8Encoding(false));
            var changed = options with { HelperPath = helper, Timeout = TimeSpan.FromSeconds(seconds), Files = options.Files.Where(pin => pin.Path != options.HelperPath)
                .Append(new PdfRasterFilePin(helper, PdfRasterProtocol.Digest(await File.ReadAllBytesAsync(helper)))).ToArray() };
            var watch = Stopwatch.StartNew();
            Rejected(await new WindowsPdfRaster(changed).RasterizeAsync(original, expected, DateTimeOffset.UtcNow.AddSeconds(seconds), default), code);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds + 8));
            using var released = File.Open(helper, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(released.Length > 0);
        }
        PinsUnlocked(options); await original.DisposeAsync(); UnchangedUnlocked(root, bytes);
    }

    internal static PdfRasterOptions Options()
    {
        // Test-only measured hashes. Runtime composition must supply independently reviewed release pins.
        var python = Required("UNITTOKO_OCR_PYTHON", file: true); var pillow = Required("UNITTOKO_OCR_PILLOW", file: false);
        var root = Root(); var packages = Path.Combine(root, ".tools", "local-ocr", "pdfium-5.13.0", "packages");
        var provisional = new PdfRasterOptions(python, Path.Combine(root, "backend", "document-processing", "pdf_preprocess.py"), packages, pillow,
            Path.Combine(root, ".tools", "local-ocr", "pdfium-5.13.0", "pypdfium2-5.13.0-py3-none-win_amd64.whl"), "12.3.0", [],
            PdfRasterProtocolTests.Limits, 256L * 1024 * 1024, TimeSpan.FromSeconds(60));
        return provisional with { Files = WindowsPdfRaster.RuntimeFiles(provisional).Select(path => new PdfRasterFilePin(path, PdfRasterProtocol.Digest(File.ReadAllBytes(path)))).ToArray() };
    }
    private static string Required(string variable, bool file) {
        var path = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException("Set the existing pinned PDF/Python fixture paths");
        Assert.True(Path.IsPathFullyQualified(path) && (file ? File.Exists(path) : Directory.Exists(path))); return Path.GetFullPath(path);
    }
    private static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Authoritative checkout required");
    }
    private static async Task<VerifiedNativeOriginalHandle> Publish(StorageRoot root, ExpectedPublication expected, byte[] bytes) {
        var store = root.Open(); var budget = new StorageIoBudget(10 * 1024 * 1024, TimeSpan.FromSeconds(10), 65536); using var input = new MemoryStream(bytes);
        Assert.IsType<StageOutcome.Sealed>(await store.StageAsync(expected.Attempt, input, budget, default));
        Assert.Equal(PublishState.Published, (await store.PublishAsync(expected, budget, default)).State);
        return Assert.IsType<NativeOpenOutcome.Opened>(await store.OpenVerifiedForNativeValidationAsync(expected, budget, default)).Handle;
    }
    private static void Rejected(PdfRasterResult result, string code) { Assert.Equal("failed", result.Status); Assert.Equal(code, result.FailureCode); Assert.Empty(result.Pages); }
    private static void PinsUnlocked(PdfRasterOptions options) {
        foreach (var path in new[] { options.PythonExecutable, options.HelperPath, options.WheelPath }) { using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); Assert.True(file.Length > 0); }
    }
    private static void UnchangedUnlocked(StorageRoot root, byte[] expected) { using var file = File.Open(root.Original, FileMode.Open, FileAccess.ReadWrite, FileShare.None); using var copy = new MemoryStream(); file.CopyTo(copy); Assert.Equal(expected, copy.ToArray()); }
    private static byte[] Pdf() {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 96] /Resources << >> /Contents 4 0 R >>", Stream("q 0 g 8 10 18 22 re f Q\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 96] /Rotate 90 /Resources << >> /Contents 6 0 R >>", Stream("q 0 g 9 10 18 22 re f Q\n")];
        var text = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++) { offsets.Add(text.Length); text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = text.Length; text.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
        static string Stream(string contents) => "<< /Length " + contents.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + contents + "\nendstream";
    }
}
