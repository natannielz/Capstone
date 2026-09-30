using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record ContentScanResult(string Status, string? EngineVersion = null, long? SignatureVersion = null,
    DateTimeOffset? SignaturesAt = null, long? Length = null, string? Sha256 = null);
internal interface IContentScanner { Task<ContentScanResult> ScanAsync(Stream original, CancellationToken ct); }
internal sealed class UnavailableContentScanner : IContentScanner
{
    public Task<ContentScanResult> ScanAsync(Stream original, CancellationToken ct) => Task.FromResult(new ContentScanResult("scanner_unavailable"));
}
internal sealed record LocalClamdOptions(int Port, string EngineVersion, long MinimumSignatureVersion, TimeSpan MaximumSignatureAge, long MaximumBytes, TimeSpan Timeout);

// Loopback only, without DNS, cloud upload, file-path commands or quarantine/delete commands.
// The listener identity, definitions and service confinement still require real host acceptance.
internal sealed class LocalClamdScanner(LocalClamdOptions options, IClock clock) : IContentScanner
{
    public async Task<ContentScanResult> ScanAsync(Stream original, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (options.Port is <= 0 or > 65535 || options.EngineVersion.Length is < 3 or > 40 || options.EngineVersion.Any(c => c is not (>= '0' and <= '9') and not '.') ||
            options.MinimumSignatureVersion <= 0 || options.MaximumSignatureAge <= TimeSpan.Zero || options.MaximumSignatureAge > TimeSpan.FromDays(7) ||
            options.MaximumBytes is <= 0 or > 10 * 1024 * 1024 || options.Timeout < TimeSpan.FromMilliseconds(100) || options.Timeout > TimeSpan.FromMinutes(1)) return new("scanner_unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(options.Timeout);
        try {
            var before = await VersionAsync(timeout.Token);
            using var client = await ConnectAsync(timeout.Token); await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
            var buffer = new byte[65536]; var size = new byte[4]; long total = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true) {
                var read = await original.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, options.MaximumBytes - total + 1)), timeout.Token);
                if (read == 0) break; total += read;
                if (total > options.MaximumBytes) return new("invalid_media");
                digest.AppendData(buffer, 0, read); BinaryPrimitives.WriteInt32BigEndian(size, read);
                await stream.WriteAsync(size, timeout.Token); await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            if (total == 0) return new("invalid_media");
            await stream.WriteAsync(new byte[4], timeout.Token); await stream.FlushAsync(timeout.Token);
            var response = await ReadReplyAsync(stream, timeout.Token);
            var status = response == "stream: OK" ? "clean" : response.StartsWith("stream: Heuristics.Limits.Exceeded", StringComparison.Ordinal) ? "scanner_unavailable" : response.StartsWith("stream: ", StringComparison.Ordinal) && response.EndsWith(" FOUND", StringComparison.Ordinal) &&
                response.Length > "stream:  FOUND".Length ? "infected" : "scanner_unavailable";
            if (status == "scanner_unavailable") return new(status);
            var after = await VersionAsync(timeout.Token);
            if (before != after) return new("scanner_unavailable");
            timeout.Token.ThrowIfCancellationRequested();
            return new(status, before.Engine, before.Version, before.At, total, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new("scanner_unavailable"); }
    }
    private async Task<TcpClient> ConnectAsync(CancellationToken ct) {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try { await client.ConnectAsync(IPAddress.Loopback, options.Port, ct); return client; } catch { client.Dispose(); throw; }
    }
    private sealed record Definition(string Engine, long Version, DateTimeOffset At);
    private async Task<Definition> VersionAsync(CancellationToken ct) {
        using var client = await ConnectAsync(ct); await using var stream = client.GetStream(); await stream.WriteAsync("zVERSION\0"u8.ToArray(), ct);
        var response = await ReadReplyAsync(stream, ct); var parts = response.Split('/');
        if (parts.Length != 3 || parts[0] != "ClamAV " + options.EngineVersion || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < options.MinimumSignatureVersion || !DateTimeOffset.TryParseExact(parts[2], ["ddd MMM d HH:mm:ss yyyy", "ddd MMM  d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) || at > clock.UtcNow || clock.UtcNow - at > options.MaximumSignatureAge) throw new InvalidDataException();
        return new(options.EngineVersion, version, at);
    }
    private static async Task<string> ReadReplyAsync(Stream stream, CancellationToken ct) {
        var buffer = new byte[513]; var count = 0;
        while (count < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct); if (read == 0) throw new InvalidDataException();
            var end = Array.IndexOf(buffer, (byte)0, count, read);
            if (end >= 0) {
                if (end != count + read - 1 || end > 512 || buffer.AsSpan(0, end).ContainsAnyExceptInRange((byte)32, (byte)126)) throw new InvalidDataException();
                return Encoding.ASCII.GetString(buffer, 0, end);
            }
            count += read;
        }
        throw new InvalidDataException();
    }
}
