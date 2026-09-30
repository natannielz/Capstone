using System.Globalization;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using UnitToko.Modules.Documents;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record ImageDecodeResult(string Status, string? Mime = null, int? Width = null, int? Height = null,
    int? Frames = null, int? Pages = null, long? Length = null, string? Sha256 = null);
internal interface IImageDecoder
{
    Task<ImageDecodeResult> DecodeAsync(Stream original, EvidenceLimits limits, CancellationToken ct);
}
internal sealed class UnavailableImageDecoder : IImageDecoder
{
    public Task<ImageDecodeResult> DecodeAsync(Stream original, EvidenceLimits limits, CancellationToken ct) => Task.FromResult(new ImageDecodeResult("decoder_unavailable"));
}
public sealed record ImageDecoderOptions(string DotnetExecutable, string AssemblyPath, string ExecutableSha256, string AssemblySha256,
    string DepsSha256, string RuntimeConfigSha256, string ManagedLibrarySha256, string CoreLibrarySha256, string NativeLibrarySha256, string PolicySha256, long MaximumMemoryBytes, TimeSpan Timeout);

/// <summary>Actual full-pixel decoding in a Windows job with creation-time limits. Not a host/network acceptance certificate.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsImageDecoder(ImageDecoderOptions options) : IImageDecoder
{
    public async Task<ImageDecodeResult> DecodeAsync(Stream original, EvidenceLimits limits, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return new("decoder_unavailable");
        var pins = new List<FileStream>();
        try {
            if (limits.MaximumBytes > 10 * 1024 * 1024 || limits.MaximumWidth > 32768 || limits.MaximumHeight > 32768 || limits.MaximumPixels > 25_000_000 ||
                options.MaximumMemoryBytes is < 16 * 1024 * 1024 or > 1024L * 1024 * 1024 || options.Timeout < TimeSpan.FromMilliseconds(100) || options.Timeout > TimeSpan.FromMinutes(1) ||
                Path.GetFileName(options.AssemblyPath) != "UnitToko.ImageDecoder.dll" || Path.GetFileName(options.DotnetExecutable) != "dotnet.exe") return new("decoder_unavailable");
            // Read locks and explicit hashes bind this execution's helper/config. Runtime and OS patch acceptance are separate deployment inputs.
            Pin(options.DotnetExecutable, options.ExecutableSha256); Pin(options.AssemblyPath, options.AssemblySha256);
            Pin(Path.ChangeExtension(options.AssemblyPath, ".deps.json"), options.DepsSha256);
            Pin(Path.ChangeExtension(options.AssemblyPath, ".runtimeconfig.json"), options.RuntimeConfigSha256);
            Pin(Path.Combine(Path.GetDirectoryName(options.AssemblyPath)!, "Magick.NET-Q8-AnyCPU.dll"), options.ManagedLibrarySha256);
            Pin(Path.Combine(Path.GetDirectoryName(options.AssemblyPath)!, "Magick.NET.Core.dll"), options.CoreLibrarySha256);
            Pin(Path.Combine(Path.GetDirectoryName(options.AssemblyPath)!, "runtimes", "win-x64", "native", "Magick.Native-Q8-x64.dll"), options.NativeLibrarySha256);
            Pin(Path.Combine(Path.GetDirectoryName(options.AssemblyPath)!, "policy.xml"), options.PolicySha256);
            var args = new[] { options.AssemblyPath, limits.MaximumBytes.ToString(CultureInfo.InvariantCulture), limits.MaximumWidth.ToString(CultureInfo.InvariantCulture),
                limits.MaximumHeight.ToString(CultureInfo.InvariantCulture), limits.MaximumPixels.ToString(CultureInfo.InvariantCulture) };
            var result = await WindowsBoundedProcess.RunAsync(options.DotnetExecutable, args, options.MaximumMemoryBytes, options.Timeout, original, limits.MaximumBytes, ct);
            if (result.Status != "exited") return new(result.Status);
            if (result.ExitCode == 2 && result.Output == "{\"status\":\"invalid_media\"}") return new("invalid_media");
            if (result.ExitCode == 3 && result.Output == "{\"status\":\"decoder_unavailable\"}") return new("decoder_unavailable");
            if (result.ExitCode != 0 || result.Error.Length != 0) return new("decoder_error");
            using var json = JsonDocument.Parse(result.Output, new() { MaxDepth = 4 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "frames", "height", "length", "mime", "pages", "sha256", "status", "width" }) == false ||
                root.GetProperty("status").GetString() != "decoded") return new("decoder_error");
            var mime = root.GetProperty("mime").GetString(); var width = root.GetProperty("width").GetInt32(); var height = root.GetProperty("height").GetInt32();
            var frames = root.GetProperty("frames").GetInt32(); var pages = root.GetProperty("pages").GetInt32(); var length = root.GetProperty("length").GetInt64(); var hash = root.GetProperty("sha256").GetString();
            if (mime is not ("image/png" or "image/jpeg") || !limits.AcceptsImage(length, width, height, frames, pages) || length != result.InputLength || hash != result.InputSha256) return new("decoder_error");
            return new("decoded", mime, width, height, frames, pages, length, hash);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new("decoder_unavailable"); }
        finally { foreach (var file in pins) await file.DisposeAsync(); }

        void Pin(string path, string hash) {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new InvalidDataException();
            for (FileSystemInfo? item = new FileInfo(path); item is not null; item = item is FileInfo leaf ? leaf.Directory : ((DirectoryInfo)item).Parent)
                if (!item.Exists || item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(file);
            if (Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != hash) throw new InvalidDataException();
        }
    }
}
