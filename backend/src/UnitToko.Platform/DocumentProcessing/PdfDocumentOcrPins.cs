using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace UnitToko.Platform.DocumentProcessing;

/// <summary>Retain both configured runtimes across all pages; bounded traversal/hash work observes the captured deadline.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PdfDocumentOcrPins : IAsyncDisposable
{
    private readonly List<FileStream> files = [];
    internal static async Task<PdfDocumentOcrPins> AcquireAsync(PdfRasterOptions raster, LocalOcrOptions ocr, Action checkpoint, CancellationToken ct)
    {
        var retained = new PdfDocumentOcrPins();
        try
        {
            checkpoint();
            if (ocr.Files.Count is < 5 or > 256 || ocr.Files.Select(pin => pin.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ocr.Files.Count ||
                ocr.PythonExecutable != raster.PythonExecutable || ocr.PillowRoot != raster.PillowRoot ||
                !WindowsPdfRaster.RuntimeFiles(raster, checkpoint).SetEquals(raster.Files.Select(pin => pin.Path))) throw new InvalidDataException();
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ocr.PythonExecutable, ocr.HelperPath,
                Path.Combine(Path.GetDirectoryName(ocr.PythonExecutable)!, "python312.dll"), Path.Combine(Path.GetDirectoryName(ocr.PythonExecutable)!, "python3.dll"),
                Path.Combine(Path.GetDirectoryName(ocr.PythonExecutable)!, "vcruntime140.dll"), Path.Combine(Path.GetDirectoryName(ocr.PythonExecutable)!, "vcruntime140_1.dll"),
                Path.Combine(ocr.ModelRoot, "eng.traineddata"), Path.Combine(ocr.ModelRoot, "ind.traineddata") };
            var entries = 0;
            foreach (var root in new[] { Path.Combine(ocr.PackageRoot, "tesserocr"), Path.Combine(ocr.PillowRoot, "PIL") })
            {
                Local(root); var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    checkpoint(); if (++entries > 1024) throw new InvalidDataException(); Local(entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                    else if (Path.GetExtension(entry) is ".py" or ".pyc" or ".pyd" or ".dll") expected.Add(entry);
                    if (expected.Count > 256) throw new InvalidDataException();
                }
            }
            if (!expected.SetEquals(ocr.Files.Select(pin => pin.Path))) throw new InvalidDataException();
            var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in raster.Files.Concat(ocr.Files.Select(pin => new PdfRasterFilePin(pin.Path, pin.Sha256))))
            {
                checkpoint();
                if (!PdfRasterProtocol.Hash(pin.Sha256) || pins.TryGetValue(pin.Path, out var previous) && previous != pin.Sha256) throw new InvalidDataException();
                pins[pin.Path] = pin.Sha256;
                if (pins.Count > 8448) throw new InvalidDataException();
            }
            long bytes = 0;
            foreach (var (path, hash) in pins)
            {
                checkpoint(); Local(path);
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                retained.files.Add(file); bytes += file.Length;
                if (file.Length > 64 * 1024 * 1024 || bytes > 512L * 1024 * 1024 ||
                    Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant() != hash) throw new InvalidDataException();
                checkpoint(); Local(path);
            }
            return retained;
        }
        catch { await retained.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { foreach (var file in files) await file.DisposeAsync(); }
    private static void Local(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetFullPath(path) != path) throw new InvalidDataException();
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        for (; item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (!item.Exists || item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
}
