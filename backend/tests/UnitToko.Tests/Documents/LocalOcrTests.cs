using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class LocalOcrTests
{
    [LocalOcrFact]
    public async Task Actual_local_candidate_recognizes_synthetic_payment_and_preserves_exact_original_provenance() {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Environment.GetEnvironmentVariable("UNITTOKO_OCR_FIXTURES")!, "payment.png"));
        var options = Options(); var engine = new WindowsLocalOcrEngine(options); var watch = Stopwatch.StartNew();
        using var input = new MemoryStream(bytes); var result = await engine.ExtractAsync("synthetic-payment", Ref(bytes), ExtractionPurpose.Payment, input, default);
        Assert.Equal("succeeded", result.Status); Assert.Null(result.FailureCode); Assert.Equal(Ref(bytes), result.Evidence);
        Assert.Equal("1234567.89", Field(result, "amount").Value); Assert.Equal("IDR", Field(result, "currency").Value);
        Assert.Equal("2026-09-30", Field(result, "transfer_date").Value); Assert.Equal("DEMO-20260930-1", Field(result, "reference").Value);
        Assert.Equal(engine.Provenance, result.Provenance); Assert.NotEmpty(result.Lines);
        Assert.All(result.Lines, line => Assert.InRange(line.Confidence, 0m, 1m));
        Assert.True(watch.Elapsed < options.Timeout + TimeSpan.FromSeconds(4));
    }
    [LocalOcrFact]
    public async Task Empty_actual_image_returns_unknowns_and_no_money() {
        var bytes = ImageDecoderTests.Png(100, 100); using var input = new MemoryStream(bytes);
        var result = await new WindowsLocalOcrEngine(Options()).ExtractAsync("synthetic-empty", Ref(bytes), ExtractionPurpose.Payment, input, default);
        Assert.Equal("succeeded", result.Status); Assert.All(result.Fields, field => Assert.Null(field.Value)); Assert.Contains("no_text", result.QualityFlags);
    }
    [LocalOcrFact]
    public async Task Pin_mismatch_and_missing_pin_refuse_before_reading_private_stream() {
        var options = Options();
        foreach (var path in new[] { options.HelperPath, options.PythonExecutable, Path.Combine(options.ModelRoot, "ind.traineddata") }) {
            var changed = options with { Files = options.Files.Select(p => p.Path == path ? p with { Sha256 = new string('0', 64) } : p).ToArray() };
            using var input = new MemoryStream([1, 2, 3]); var result = await new WindowsLocalOcrEngine(changed).ExtractAsync("bad-pin", Ref([1, 2, 3]), ExtractionPurpose.Payment, input, default);
            Assert.Equal("ocr_unavailable", result.FailureCode); Assert.Equal(0, input.Position); Assert.Empty(result.Lines);
        }
        using var source = new MemoryStream([1, 2, 3]);
        var missing = options with { Files = options.Files.Where(p => p.Path != options.HelperPath).ToArray() };
        Assert.Equal("ocr_unavailable", (await new WindowsLocalOcrEngine(missing).ExtractAsync("missing-pin", Ref([1, 2, 3]), ExtractionPurpose.Payment, source, default)).FailureCode);
        Assert.Equal(0, source.Position);
    }
    [LocalOcrFact]
    public async Task Different_original_invalid_media_and_resource_limits_cannot_return_success() {
        var bytes = ImageDecoderTests.Png(100, 100); var options = Options(); var engine = new WindowsLocalOcrEngine(options);
        using var source = new MemoryStream(bytes);
        Assert.Equal("ocr_integrity_mismatch", (await engine.ExtractAsync("wrong-original", new(1, 1, new string('0', 64)), ExtractionPurpose.Payment, source, default)).FailureCode);
        using var invalid = new MemoryStream("not an image"u8.ToArray());
        Assert.Equal("ocr_invalid_media", (await engine.ExtractAsync("bad-media", Ref(invalid.ToArray()), ExtractionPurpose.Payment, invalid, default)).FailureCode);
        using var large = new MemoryStream(bytes);
        Assert.Equal("ocr_invalid_media", (await new WindowsLocalOcrEngine(options with { Limits = new(100000, 99, 100, 10000) }).ExtractAsync("dimensions", Ref(bytes), ExtractionPurpose.Payment, large, default)).FailureCode);
        using var lowMemory = new MemoryStream(bytes);
        Assert.Equal("failed", (await new WindowsLocalOcrEngine(options with { MaximumMemoryBytes = 16 * 1024 * 1024 }).ExtractAsync("memory", Ref(bytes), ExtractionPurpose.Payment, lowMemory, default)).Status);
    }
    [LocalOcrFact]
    public async Task Unsupported_purpose_does_not_read_and_cancellation_does_not_succeed() {
        var engine = new WindowsLocalOcrEngine(Options()); using var input = new MemoryStream([1, 2, 3]);
        Assert.Equal("ocr_unsupported_purpose", (await engine.ExtractAsync("supplier", Ref([1, 2, 3]), ExtractionPurpose.SupplierInvoice, input, default)).FailureCode);
        Assert.Equal(0, input.Position); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExtractAsync("canceled", Ref([1, 2, 3]), ExtractionPurpose.Payment, input, cancellation.Token));
    }
    [LocalOcrFact]
    public async Task Stalled_input_is_killed_at_the_actual_process_deadline() {
        var watch = Stopwatch.StartNew(); using var input = new StalledInput();
        var result = await new WindowsLocalOcrEngine(Options() with { Timeout = TimeSpan.FromSeconds(2) }).ExtractAsync("stalled", new(1, 1, new string('0', 64)), ExtractionPurpose.Payment, input, default);
        Assert.Equal("ocr_timeout", result.FailureCode); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8)); Assert.Empty(result.Lines);
    }
    [LocalOcrFact]
    public async Task Duplicate_json_keys_wrong_engine_and_excess_output_never_escape_the_private_protocol() {
        var options = Options(); var directory = Path.Combine(Environment.GetEnvironmentVariable("UNITTOKO_OCR_FIXTURES")!, "protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var helper = Path.Combine(directory, "local_ocr.py"); var bytes = new byte[] { 1, 2, 3 };
        var valid = "{\"engine\":\"" + WindowsLocalOcrEngine.Engine + "\",\"height\":1,\"width\":1,\"length\":3,\"lines\":[],\"sha256\":\"" + Hash(bytes) + "\",\"status\":\"succeeded\"}";
        try {
            foreach (var output in new[] { valid.Replace("\"lines\":[]", "\"lines\":[],\"lines\":[]", StringComparison.Ordinal), valid.Replace(WindowsLocalOcrEngine.Engine, "unapproved-engine", StringComparison.Ordinal), new string('a', 131073) }) {
                await File.WriteAllTextAsync(helper, "import sys,base64\nsys.stdin.buffer.read()\nsys.stdout.buffer.write(base64.b64decode('" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(output)) + "'))\n");
                var pins = options.Files.Where(p => p.Path != options.HelperPath).Append(new OcrFilePin(helper, Hash(await File.ReadAllBytesAsync(helper)))).ToArray();
                using var source = new MemoryStream(bytes);
                var result = await new WindowsLocalOcrEngine(options with { HelperPath = helper, Files = pins, Timeout = TimeSpan.FromSeconds(3) })
                    .ExtractAsync("protocol", Ref(bytes), ExtractionPurpose.Payment, source, default);
                Assert.Equal("failed", result.Status); Assert.Empty(result.Lines); Assert.Empty(result.Fields);
                if (output.Length < 131073) Assert.Equal("ocr_invalid_output", result.FailureCode);
            }
        } finally { File.Delete(helper); Directory.Delete(directory, recursive: false); }
    }
    internal static LocalOcrOptions Options() {
        // Synthetic local test pin generation only. Production pins must come from a reviewed release manifest.
        var python = Environment.GetEnvironmentVariable("UNITTOKO_OCR_PYTHON")!;
        var helper = Environment.GetEnvironmentVariable("UNITTOKO_OCR_HELPER")!;
        var packages = Environment.GetEnvironmentVariable("UNITTOKO_OCR_PACKAGES")!;
        var pillow = Environment.GetEnvironmentVariable("UNITTOKO_OCR_PILLOW")!;
        var models = Environment.GetEnvironmentVariable("UNITTOKO_OCR_MODELS")!;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { python, helper, Path.Combine(Path.GetDirectoryName(python)!, "python312.dll"),
            Path.Combine(Path.GetDirectoryName(python)!, "python3.dll"), Path.Combine(Path.GetDirectoryName(python)!, "vcruntime140.dll"), Path.Combine(Path.GetDirectoryName(python)!, "vcruntime140_1.dll"),
            Path.Combine(models, "eng.traineddata"), Path.Combine(models, "ind.traineddata") };
        foreach (var root in new[] { Path.Combine(packages, "tesserocr"), Path.Combine(pillow, "PIL") })
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p) is ".py" or ".pyc" or ".pyd" or ".dll")) paths.Add(path);
        return new(python, helper, packages, pillow, models, "tessdata-fast-87416418657359cb625c412a48b6e1d6d41c29bd-ind+eng",
            paths.Select(p => new OcrFilePin(p, Hash(File.ReadAllBytes(p)))).ToArray(), new(10 * 1024 * 1024, 32768, 32768, 25_000_000), 512 * 1024 * 1024, TimeSpan.FromSeconds(20));
    }
    private static FieldCandidate Field(ExtractionResult result, string field) => Assert.Single(result.Fields, f => f.Field == field);
    private static EvidenceRef Ref(byte[] bytes) => new(1, 1, Hash(bytes));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class StalledInput : Stream {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class LocalOcrFactAttribute : FactAttribute
{
    public LocalOcrFactAttribute() {
        if (!OperatingSystem.IsWindows() || new[] { "PYTHON", "HELPER", "PACKAGES", "PILLOW", "MODELS", "FIXTURES" }.Any(n => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITTOKO_OCR_" + n))))
            Skip = "Requires explicitly configured Windows local OCR candidate and synthetic fixtures; skipped is not engine acceptance";
    }
}
