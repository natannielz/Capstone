using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

/// <summary>Assigned private PDF capability to complete, bounded page rasters. No admission, OCR, persistence or business acceptance.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPdfRaster
{
    private readonly PdfRasterOptions options;
    private readonly string inventorySha256;
    internal PdfRasterProvenance Provenance { get; }
    // Isolated startup already owns pinned Python/stdlib. Helper may add only the verified application roots.
    private const string Bootstrap = """
import sys,os,time,importlib.machinery
expected_inventory=sys.argv.pop(1)
sys.argv.pop(0)
helper,packages,pillow,wheel=sys.argv[0:4]
python=os.path.dirname(sys.executable)
stdlib=os.path.join(python,'Lib')
normalize=lambda path:os.path.normcase(os.path.abspath(path))
allowed=set()
class SourceOnly(importlib.machinery.SourceFileLoader):
 def get_code(self,name):return compile(self.get_data(self.path),self.path,'exec')
class Guard:
 @staticmethod
 def find_spec(name,path=None,target=None):
  spec=importlib.machinery.PathFinder.find_spec(name,path,target)
  if spec is None:return None
  if spec.origin is None or normalize(spec.origin) not in allowed:raise ModuleNotFoundError('unavailable')
  if isinstance(spec.loader,importlib.machinery.SourceFileLoader):spec.loader=SourceOnly(name,spec.origin)
  elif not isinstance(spec.loader,importlib.machinery.ExtensionFileLoader):raise ModuleNotFoundError('unavailable')
  return spec
sys.meta_path=[importlib.machinery.BuiltinImporter,importlib.machinery.FrozenImporter,Guard]
import _sha2
if _sha2.__spec__.origin!='built-in':raise RuntimeError('unavailable')
deadline=int(sys.argv[-1])/1000
remaining=deadline-time.time()
if not 0<remaining<=60:raise RuntimeError('unavailable')
end=time.monotonic()+remaining
def checkpoint():
 if time.time()>=deadline or time.monotonic()>=end:raise RuntimeError('unavailable')
files={normalize(helper),normalize(wheel)}
entries=0
def entry(path):
 global entries
 checkpoint()
 entries+=1
 if entries>12288:raise RuntimeError('unavailable')
 stat=os.stat(path,follow_symlinks=False)
 if stat.st_file_attributes&1024:raise RuntimeError('unavailable')
 if os.path.isdir(path):return True
 files.add(normalize(path))
 if len(files)>8192:raise RuntimeError('unavailable')
 return False
with os.scandir(python) as rows:
 for row in rows:
  if not row.is_dir(follow_symlinks=False):entry(row.path)
def tree(root,exclude=False):
 pending=[root]
 while pending:
  directory=pending.pop()
  with os.scandir(directory) as rows:
   for row in rows:
    if entry(row.path) and not (exclude and directory==root and row.name=='site-packages'):pending.append(row.path)
tree(os.path.join(python,'DLLs'))
tree(stdlib,True)
tree(packages)
tree(os.path.join(pillow,'PIL'))
if os.path.isdir(os.path.join(pillow,'defusedxml')):tree(os.path.join(pillow,'defusedxml'))
total=0
inventory=_sha2.sha256()
for path in sorted(files):
 checkpoint()
 digest=_sha2.sha256()
 with open(path,'rb') as source:
  length=os.fstat(source.fileno()).st_size
  total+=length
  if length>67108864 or total>536870912:raise RuntimeError('unavailable')
  while True:
   checkpoint()
   block=source.read(65536)
   if not block:break
   digest.update(block)
 inventory.update((path+':'+digest.hexdigest()+'\n').encode('utf-8'))
if inventory.hexdigest()!=expected_inventory:raise RuntimeError('unavailable')
allowed=files
with open(helper,'rb') as source:code=source.read(1048577)
if len(code)>1048576:raise RuntimeError('unavailable')
exec(compile(code,helper,'exec'),{'__name__':'__main__','__file__':helper})
""";

    internal WindowsPdfRaster(PdfRasterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Files is null || options.Files.Count is < 20 or > 8192 || options.Files.Any(pin => pin.Path.Length > 1024 || !PdfRasterProtocol.Hash(pin.Sha256)))
            throw new ArgumentException("PDF runtime pins require a bounded complete release inventory.");
        this.options = options with { Files = Array.AsReadOnly(options.Files.ToArray()) };
        using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pin in this.options.Files.OrderBy(pin => pin.Path.ToLowerInvariant(), StringComparer.Ordinal))
            inventory.AppendData(Encoding.UTF8.GetBytes(pin.Path.ToLowerInvariant() + ":" + pin.Sha256 + "\n"));
        inventorySha256 = Convert.ToHexString(inventory.GetHashAndReset()).ToLowerInvariant();
        Provenance = new(PdfRasterProtocol.Pipeline, PdfRasterProtocol.Wrapper, PdfRasterProtocol.Pdfium,
            PdfRasterProtocol.WheelSha256, options.PillowVersion, PdfRasterProtocol.Digest(Encoding.UTF8.GetBytes(inventorySha256 + "\n" + Bootstrap)));
    }

    internal async Task<PdfRasterResult> RasterizeAsync(VerifiedNativeOriginalHandle original, ExpectedPublication expected,
        DateTimeOffset capturedDeadline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return Failed("pdf_raster_unavailable");
        if (original is null || original.Publication != expected || expected.Content.Length is < 1 or > 10 * 1024 * 1024)
            return Failed("pdf_raster_source_mismatch");
        var elapsed = Stopwatch.StartNew(); var window = capturedDeadline - DateTimeOffset.UtcNow;
        if (window <= TimeSpan.Zero) return Failed("pdf_raster_deadline");
        var budget = window < options.Timeout ? window : options.Timeout;
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(1)) return Failed("pdf_raster_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(budget);
        var pins = new List<FileStream>(); var verifiedRuntime = false;
        try
        {
            Validate(() => { _ = Remaining(); });
            // Retain the verified original across pinning, child execution AND parent protocol/PNG validation.
            using var retained = original.AcquireInput(expected, 10 * 1024 * 1024); retained.RequireUnchanged();
            long total = 0;
            foreach (var pin in options.Files)
            {
                Remaining(); LocalPath(pin.Path);
                // Locks are retained, but thousands of per-file 64 KiB buffers are not.
                var file = new FileStream(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                pins.Add(file); total += file.Length;
                if (file.Length > 64 * 1024 * 1024 || total > 512L * 1024 * 1024 || Convert.ToHexString(await SHA256.HashDataAsync(file, deadline.Token)).ToLowerInvariant() != pin.Sha256) throw new InvalidDataException();
                LocalPath(pin.Path);
            }
            // Repeat exact inventory with all files locked; addition/removal cannot silently change import coverage.
            Validate(() => { _ = Remaining(); }); verifiedRuntime = true; retained.RequireUnchanged();
            var remaining = Remaining(); var absolute = DateTimeOffset.UtcNow + remaining;
            if (absolute > capturedDeadline) absolute = capturedDeadline;
            string N(long value) => value.ToString(CultureInfo.InvariantCulture);
            var startup = "exec(bytes.fromhex('" + Convert.ToHexString(Encoding.UTF8.GetBytes(Bootstrap)) + "'))";
            string[] args = ["-I", "-S", "-B", "-c", startup, inventorySha256, options.HelperPath, options.PackageRoot, options.PillowRoot, options.WheelPath,
                expected.Content.Sha256, N(expected.Content.Length), N(options.Limits.MaximumPages), N(options.Limits.MaximumPagePixels),
                N(options.Limits.MaximumTotalPixels), N(options.Limits.MaximumOutputBytes), N(absolute.ToUnixTimeMilliseconds())];
            var child = await WindowsBoundedProcess.RunAsync(options.PythonExecutable, args, options.MaximumMemoryBytes,
                remaining, original, expected, 10 * 1024 * 1024, deadline.Token, options.Limits.MaximumOutputBytes);
            Remaining(); retained.RequireUnchanged();
            if (child.Status == "decode_timeout") return Failed("pdf_raster_deadline");
            if (child.Status != "exited") return Failed(child.Status == "invalid_media" ? "pdf_raster_source_mismatch" : "pdf_raster_process_failed");
            if (child.InputLength != expected.Content.Length || child.InputSha256 != expected.Content.Sha256) return Failed("pdf_raster_source_mismatch");
            if (child.Error.Length != 0) return Failed("pdf_raster_process_failed");
            if (child.ExitCode != 0)
            {
                var code = PdfRasterProtocol.Failure(child.Output, child.ExitCode);
                return Failed(code is null ? "pdf_raster_invalid_output" : "pdf_raster_" + code);
            }
            var pages = PdfRasterProtocol.Parse(child.Output, expected, Provenance, options.Limits, deadline.Token);
            Remaining(); retained.RequireUnchanged();
            return new("rasterized", null, expected, Provenance, pages);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed("pdf_raster_deadline"); }
        catch (TimeoutException) { return Failed("pdf_raster_deadline"); }
        catch (ObjectDisposedException) { return Failed("pdf_raster_source_mismatch"); }
        catch (StorageUnavailableException) { return Failed("pdf_raster_source_mismatch"); }
        catch { return Failed(verifiedRuntime ? "pdf_raster_invalid_output" : "pdf_raster_unavailable"); }
        finally { foreach (var pin in pins) await pin.DisposeAsync(); }

        PdfRasterResult Failed(string code) => new("failed", code, expected, Provenance, Array.Empty<PdfRasterPage>());
        TimeSpan Remaining()
        {
            deadline.Token.ThrowIfCancellationRequested(); var remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero || DateTimeOffset.UtcNow >= capturedDeadline) throw new TimeoutException();
            return remaining;
        }
    }

    private void Validate(Action checkpoint)
    {
        options.Limits.RequireValid();
        if (options.MaximumMemoryBytes is < 32 * 1024 * 1024 or > 512L * 1024 * 1024 || options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.Files.Count is < 20 or > 8192 || options.Files.Any(pin => !PdfRasterProtocol.Hash(pin.Sha256)) ||
            options.Files.Select(pin => pin.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Files.Count ||
            Path.GetFileName(options.PythonExecutable) != "python.exe" || Path.GetFileName(options.HelperPath) != "pdf_preprocess.py" ||
            options.PillowVersion.Length is < 3 or > 32 || options.PillowVersion.Any(c => c is not (>= '0' and <= '9') && c != '.')) throw new InvalidDataException();
        var expected = RuntimeFiles(options, checkpoint);
        if (!expected.SetEquals(options.Files.Select(pin => pin.Path)) || options.Files.Single(pin => pin.Path == options.WheelPath).Sha256 != PdfRasterProtocol.WheelSha256) throw new InvalidDataException();
    }

    // Shared with fixture composition; callers must supply reviewed hashes, never auto-approve this discovered inventory.
    internal static HashSet<string> RuntimeFiles(PdfRasterOptions options, Action? checkpoint = null)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { options.HelperPath, options.WheelPath };
        var entries = 0; var python = Path.GetDirectoryName(options.PythonExecutable)!; checkpoint?.Invoke(); LocalPath(python);
        foreach (var path in Directory.EnumerateFiles(python)) { checkpoint?.Invoke(); if (++entries > 12288) throw new InvalidDataException(); LocalPath(path); files.Add(path); }
        if (!files.Contains(options.PythonExecutable) || !files.Contains(Path.Combine(python, "python312.dll")) ||
            !files.Contains(Path.Combine(python, "python3.dll")) || Directory.EnumerateFiles(python, "*._pth").Any() || File.Exists(Path.Combine(python, "pyvenv.cfg"))) throw new InvalidDataException();
        AddTree(Path.Combine(python, "DLLs")); AddTree(Path.Combine(python, "Lib"), excludeSitePackages: true);
        AddTree(options.PackageRoot); AddTree(Path.Combine(options.PillowRoot, "PIL"));
        if (Directory.Exists(Path.Combine(options.PillowRoot, "defusedxml"))) AddTree(Path.Combine(options.PillowRoot, "defusedxml"));
        foreach (var path in files) { checkpoint?.Invoke(); LocalPath(path); }
        return files;

        void AddTree(string root, bool excludeSitePackages = false)
        {
            checkpoint?.Invoke(); LocalPath(root); var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    checkpoint?.Invoke(); if (++entries > 12288) throw new InvalidDataException(); LocalPath(entry);
                    if (Directory.Exists(entry)) { if (!excludeSitePackages || directory != root || Path.GetFileName(entry) != "site-packages") pending.Push(entry); }
                    else files.Add(entry);
                    if (files.Count > 8192) throw new InvalidDataException();
                }
            }
        }
    }
    private static void LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetFullPath(path) != path) throw new InvalidDataException();
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        for (; item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (!item.Exists || item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
}
