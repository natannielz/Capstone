using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnitToko.Modules.Documents;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record OcrFilePin(string Path, string Sha256);
public sealed record LocalOcrOptions(string PythonExecutable, string HelperPath, string PackageRoot, string PillowRoot,
    string ModelRoot, string ModelVersion, IReadOnlyList<OcrFilePin> Files, EvidenceLimits Limits, long MaximumMemoryBytes, TimeSpan Timeout);
internal sealed record LocalOcrMeasurement(TimeSpan Elapsed, long? PeakJobMemoryBytes);

/// <summary>Offline Tesseract candidate. Not production selected: benchmark and host/egress acceptance are separate release gates.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLocalOcrEngine : ILocalOcrEngine
{
    internal const string Engine = "tesseract-5.5.3/tesserocr-2.11.0";
    internal const string Preprocessing = "original-rgb-psm6-ind+eng-v1/" + PaymentFieldExtractor.Version;
    private readonly LocalOcrOptions options;
    private readonly ExtractionPurpose purpose;
    public ExtractionProvenance Provenance { get; }
    internal LocalOcrMeasurement? LastMeasurement { get; private set; }
    internal WindowsLocalOcrEngine(LocalOcrOptions options, ExtractionPurpose purpose = ExtractionPurpose.Payment) {
        ArgumentNullException.ThrowIfNull(options);
        if (purpose is not (ExtractionPurpose.Payment or ExtractionPurpose.SupplierInvoice)) throw new ArgumentException("Unsupported OCR purpose", nameof(purpose));
        this.purpose = purpose;
        // Freeze the configuration; a mutable collection supplied by composition cannot change an in-flight model.
        this.options = options with { Files = Array.AsReadOnly(options.Files.ToArray()) };
        var models = this.options.Files.Where(f => Path.GetDirectoryName(f.Path) == options.ModelRoot).OrderBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal).ToArray();
        if (models.Length != 2 || !models.Select(f => Path.GetFileName(f.Path)).SequenceEqual(new[] { "eng.traineddata", "ind.traineddata" })) throw new ArgumentException("OCR language pins are incomplete");
        Provenance = new(Engine, options.ModelVersion, Digest(models.Select(f => Path.GetFileName(f.Path) + ":" + f.Sha256)),
            Digest(this.options.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => f.Path + ":" + f.Sha256)),
            purpose == ExtractionPurpose.Payment ? Preprocessing : "original-rgb-psm6-ind+eng-v1/" + SupplierDocumentFieldExtractor.Version);
    }
    public async Task<ExtractionResult> ExtractAsync(string jobId, EvidenceRef evidence, ExtractionPurpose purpose, Stream validatedBytes, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        LastMeasurement = null;
        if (purpose != this.purpose) return Failed("ocr_unsupported_purpose");
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return Failed("ocr_unavailable");
        var pins = new List<FileStream>(); var elapsed = Stopwatch.StartNew();
        try {
            ValidateConfiguration();
            foreach (var pin in options.Files) {
                cancellationToken.ThrowIfCancellationRequested();
                LocalPath(pin.Path);
                if (pin.Sha256.Length != 64 || pin.Sha256.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new InvalidDataException();
                var file = new FileStream(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(file);
                if (file.Length > 64 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != pin.Sha256) return Failed("ocr_unavailable");
                if (elapsed.Elapsed >= options.Timeout) return Failed("ocr_timeout");
            }
            var args = new[] { "-I", "-S", "-B", options.HelperPath, options.PackageRoot, options.PillowRoot, options.ModelRoot,
                options.Limits.MaximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), options.Limits.MaximumWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                options.Limits.MaximumHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), options.Limits.MaximumPixels.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var result = await WindowsBoundedProcess.RunAsync(options.PythonExecutable, args, options.MaximumMemoryBytes, options.Timeout - elapsed.Elapsed,
                validatedBytes, options.Limits.MaximumBytes, cancellationToken, 131072);
            LastMeasurement = new(elapsed.Elapsed, result.PeakJobMemoryBytes);
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= options.Timeout) return Failed("ocr_timeout");
            if (result.Status == "decode_timeout") return Failed("ocr_timeout");
            if (result.Status == "invalid_media") return Failed("ocr_invalid_media");
            if (result.Status != "exited") return Failed("ocr_unavailable");
            if (result.ExitCode == 2 && result.Output == "{\"status\":\"invalid_media\"}") return Failed("ocr_invalid_media");
            if (result.ExitCode != 0) return Failed("ocr_unavailable");
            if (result.InputSha256 != evidence.Sha256) return Failed("ocr_integrity_mismatch");
            return Parse(result);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Failed("ocr_unavailable"); }
        finally { foreach (var pin in pins) await pin.DisposeAsync(); }

        ExtractionResult Failed(string code) => new(jobId, evidence, purpose, Provenance, "failed", code, []);
        ExtractionResult Parse(BoundedProcessResult result) {
            try {
                using var json = JsonDocument.Parse(result.Output, new() { MaxDepth = 6 }); var root = json.RootElement;
                Shape(root, "engine", "height", "length", "lines", "sha256", "status", "width");
                if (root.GetProperty("status").GetString() != "succeeded" || root.GetProperty("engine").GetString() != Engine ||
                    root.GetProperty("length").GetInt64() != result.InputLength || root.GetProperty("sha256").GetString() != evidence.Sha256 ||
                    !options.Limits.AcceptsImage(result.InputLength, root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(), 1, 1)) return Failed("ocr_invalid_output");
                var values = root.GetProperty("lines"); if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 128) return Failed("ocr_invalid_output");
                var lines = new List<RecognizedLine>();
                foreach (var line in values.EnumerateArray()) {
                    Shape(line, "box", "confidence", "text"); var box = line.GetProperty("box");
                    if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) return Failed("ocr_invalid_output");
                    lines.Add(new(line.GetProperty("text").GetString()!, line.GetProperty("confidence").GetDecimal(),
                        new(box[0].GetDecimal(), box[1].GetDecimal(), box[2].GetDecimal(), box[3].GetDecimal())));
                }
                return new(jobId, evidence, purpose, Provenance, "succeeded", null, lines);
            } catch { return Failed("ocr_invalid_output"); }
        }
    }
    private void ValidateConfiguration() {
        if (options.Limits.MaximumBytes > 10 * 1024 * 1024 || options.Limits.MaximumWidth > 32768 || options.Limits.MaximumHeight > 32768 || options.Limits.MaximumPixels > 25_000_000 ||
            options.MaximumMemoryBytes is < 16 * 1024 * 1024 or > 1024L * 1024 * 1024 || options.Timeout < TimeSpan.FromMilliseconds(100) || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.Files.Count is < 5 or > 256 || options.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Files.Count ||
            Path.GetFileName(options.PythonExecutable) != "python.exe" || Path.GetFileName(options.HelperPath) != "local_ocr.py") throw new InvalidDataException();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { options.PythonExecutable, options.HelperPath,
            Path.Combine(Path.GetDirectoryName(options.PythonExecutable)!, "python312.dll"), Path.Combine(Path.GetDirectoryName(options.PythonExecutable)!, "python3.dll"),
            Path.Combine(Path.GetDirectoryName(options.PythonExecutable)!, "vcruntime140.dll"), Path.Combine(Path.GetDirectoryName(options.PythonExecutable)!, "vcruntime140_1.dll") };
        // Only these local application directories are added to isolated Python's import path. Pin all executable library code,
        // including optional image plugins, and reject missing/extra code. Standard library/OS acceptance remains a host gate.
        foreach (var root in new[] { Path.Combine(options.PackageRoot, "tesserocr"), Path.Combine(options.PillowRoot, "PIL") }) {
            LocalPath(root);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p) is ".py" or ".pyc" or ".pyd" or ".dll")) expected.Add(path);
        }
        expected.Add(Path.Combine(options.ModelRoot, "eng.traineddata")); expected.Add(Path.Combine(options.ModelRoot, "ind.traineddata"));
        if (!expected.SetEquals(options.Files.Select(f => f.Path))) throw new InvalidDataException();
    }
    private static void LocalPath(string path) {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetFullPath(path) != path) throw new InvalidDataException();
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        for (; item is not null; item = item is FileInfo leaf ? leaf.Directory : ((DirectoryInfo)item).Parent)
            if (!item.Exists || item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
    private static void Shape(JsonElement value, params string[] names) {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(names)) throw new InvalidDataException();
    }
    private static string Digest(IEnumerable<string> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
}
