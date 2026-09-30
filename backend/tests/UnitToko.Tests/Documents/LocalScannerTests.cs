using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Tests.Documents;

// Protocol simulator only; these cases do not establish real ClamAV/signature/host acceptance.
public sealed class LocalScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    [Fact]
    public async Task Default_and_unavailable_scanner_never_report_clean_or_consume_content() {
        using var source = new MemoryStream([1, 2, 3]);
        Assert.Equal("scanner_unavailable", (await new UnavailableContentScanner().ScanAsync(source, default)).Status);
        Assert.Equal(0, source.Position);
        await using var server = new Server("unexpected-version");
        Assert.Equal("scanner_unavailable", (await Scanner(server).ScanAsync(source, default)).Status); Assert.Equal(0, source.Position);
    }
    [Theory]
    [InlineData("stream: OK", "clean")]
    [InlineData("stream: Test-Signature FOUND", "infected")]
    [InlineData("stream: Heuristics.Limits.Exceeded FOUND", "scanner_unavailable")]
    [InlineData("stream: scan failed ERROR", "scanner_unavailable")]
    [InlineData("stream: OK\nstream: Other FOUND", "scanner_unavailable")]
    [InlineData("OK", "scanner_unavailable")]
    public async Task Exact_protocol_verdict_is_bound_to_exact_stream_and_definition_identity(string response, string expected) {
        await using var server = new Server(Version(), response); var bytes = "synthetic-payment-image-fixture"u8.ToArray(); using var source = new MemoryStream(bytes);
        var result = await Scanner(server).ScanAsync(source, default); Assert.Equal(expected, result.Status);
        if (expected is "clean" or "infected") {
            Assert.Equal(bytes, server.Bytes); Assert.Equal(bytes.Length, result.Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
            Assert.Equal("1.5.1", result.EngineVersion); Assert.Equal(30000, result.SignatureVersion); Assert.Equal(Now.AddHours(-1), result.SignaturesAt);
        } else Assert.Null(result.Sha256);
    }
    [Theory]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("engine")]
    [InlineData("definitions")]
    [InlineData("overlong")]
    public async Task Untrusted_or_stale_version_reply_prevents_sending_private_data(string kind) {
        var version = kind switch { "old" => Version(Now.AddDays(-4)), "future" => Version(Now.AddHours(1)), "engine" => Version().Replace("1.5.1", "1.5.0"),
            "definitions" => Version().Replace("30000", "20000"), _ => new string('x', 600) };
        await using var server = new Server(version); using var source = new MemoryStream([1, 2, 3]);
        Assert.Equal("scanner_unavailable", (await Scanner(server).ScanAsync(source, default)).Status); Assert.Equal(0, source.Position); Assert.Null(server.Bytes);
    }
    [Fact]
    public async Task Definition_change_during_stream_invalidates_the_clean_response() {
        await using var server = new Server(Version(), "stream: OK", Version().Replace("30000", "30001")); using var source = new MemoryStream([1, 2, 3]);
        Assert.Equal("scanner_unavailable", (await Scanner(server).ScanAsync(source, default)).Status);
    }
    [Fact]
    public async Task Input_and_deadline_limits_remain_non_clean() {
        await using var server = new Server(Version()); using var source = new MemoryStream(new byte[1025]);
        Assert.Equal("invalid_media", (await Scanner(server).ScanAsync(source, default)).Status);
        await using var stalled = new Server(Version(), stall: true); using var second = new MemoryStream([1, 2, 3]);
        Assert.Equal("scanner_unavailable", (await Scanner(stalled, TimeSpan.FromMilliseconds(200)).ScanAsync(second, default)).Status); Assert.Equal(0, second.Position);
    }
    private static LocalClamdScanner Scanner(Server server, TimeSpan? duration = null) => new(new(server.Port, "1.5.1", 30000, TimeSpan.FromDays(2), 1024, duration ?? TimeSpan.FromSeconds(3)), new FixedClock(Now));
    private static string Version(DateTimeOffset? date = null) => "ClamAV 1.5.1/30000/" + (date ?? Now.AddHours(-1)).UtcDateTime.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
    internal sealed class Server : IAsyncDisposable {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource stop = new(); private readonly Task run;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public byte[]? Bytes { get; private set; }
        public Server(string version, string response = "stream: OK", string? versionAfter = null, bool stall = false) {
            listener.Start(); run = Run();
            async Task Run() {
                try {
                    var versions = 0;
                    while (!stop.IsCancellationRequested) {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token); await using var stream = client.GetStream();
                        if (stall) { await Task.Delay(Timeout.Infinite, stop.Token); continue; }
                        var command = new List<byte>(); var single = new byte[1];
                        while (await stream.ReadAsync(single, stop.Token) > 0 && single[0] != 0) { command.Add(single[0]); if (command.Count > 20) throw new InvalidDataException(); }
                        if (Encoding.ASCII.GetString(command.ToArray()) == "zVERSION") { await stream.WriteAsync(Encoding.ASCII.GetBytes((versions++ == 0 ? version : versionAfter ?? version) + "\0"), stop.Token); continue; }
                        Assert.Equal("zINSTREAM", Encoding.ASCII.GetString(command.ToArray())); using var content = new MemoryStream(); var size = new byte[4];
                        while (true) { await stream.ReadExactlyAsync(size, stop.Token); var length = BinaryPrimitives.ReadInt32BigEndian(size); if (length == 0) break; Assert.InRange(length, 1, 65536); var chunk = new byte[length]; await stream.ReadExactlyAsync(chunk, stop.Token); content.Write(chunk); }
                        Bytes = content.ToArray(); await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\0"), stop.Token);
                    }
                } catch (Exception error) when (stop.IsCancellationRequested || error is IOException or SocketException or OperationCanceledException) { }
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await run; stop.Dispose(); }
    }
}
