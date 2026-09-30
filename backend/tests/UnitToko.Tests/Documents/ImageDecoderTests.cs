using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Tests.Documents;

public sealed class ImageDecoderAvailabilityTests
{
    [Fact]
    public async Task Unconfigured_decoder_does_not_consume_private_bytes() {
        using var source = new MemoryStream([1, 2, 3]);
        var result = await new UnavailableImageDecoder().DecodeAsync(source, new(1024, 100, 100, 10000), CancellationToken.None);
        Assert.Equal("decoder_unavailable", result.Status); Assert.Equal(0, source.Position);
    }
}

[SupportedOSPlatform("windows")]
public sealed class ImageDecoderTests
{
    [WindowsStorageFact]
    public async Task Actual_Windows_decoder_reads_all_pixels_and_returns_exact_original_identity() {
        var bytes = Png(13, 7); using var stream = new MemoryStream(bytes);
        var result = await Decoder().DecodeAsync(stream, Limits(), default);
        Assert.Equal("decoded", result.Status); Assert.Equal("image/png", result.Mime);
        Assert.Equal(13, result.Width); Assert.Equal(7, result.Height); Assert.Equal(1, result.Frames); Assert.Equal(1, result.Pages);
        Assert.Equal(bytes.Length, result.Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
    }
    [WindowsStorageFact]
    public async Task Jpeg_decodes_but_truncated_scan_even_with_end_marker_is_rejected() {
        // Generated 13x7 RGB gradient; no user image or bank information.
        var bytes = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAHAA0DASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDzH4f/ALI1r+6/dQ9u4r6I8Mfsj2w08fuou3cUUV52UZlieX4j57w64yzr2K/fdD//2Q==");
        using var original = new MemoryStream(bytes); var decoded = await Decoder().DecodeAsync(original, Limits(), default);
        Assert.Equal("decoded", decoded.Status); Assert.Equal("image/jpeg", decoded.Mime); Assert.Equal(13, decoded.Width); Assert.Equal(7, decoded.Height);
        foreach (var changed in new[] { bytes[..^1], bytes[..^20].Concat(new byte[] { 255, 217 }).ToArray(), bytes.Concat(new byte[] { 0 }).ToArray() }) {
            using var source = new MemoryStream(changed); Assert.Equal("invalid_media", (await Decoder().DecodeAsync(source, Limits(), default)).Status);
        }
    }
    [WindowsStorageFact]
    public async Task Bad_CRC_truncation_extra_data_animation_and_bad_compressed_pixels_are_not_decoded() {
        var valid = Png(13, 7); var badCrc = valid.ToArray(); badCrc[^5] ^= 1;
        foreach (var bytes in new[] { valid[..^1], valid.Concat(new byte[] { 0 }).ToArray(), badCrc, Png(13, 7, corruptPixels: true), Png(13, 7, animation: true), "not an image"u8.ToArray() }) {
            using var stream = new MemoryStream(bytes); var result = await Decoder().DecodeAsync(stream, Limits(), default);
            Assert.Equal("invalid_media", result.Status); Assert.Null(result.Mime); Assert.Null(result.Sha256);
        }
    }
    [WindowsStorageFact]
    public async Task Width_pixel_and_byte_limits_reject_actual_image() {
        var bytes = Png(13, 7);
        foreach (var limits in new[] { new EvidenceLimits(10000, 12, 100, 10000), new EvidenceLimits(10000, 100, 100, 90), new EvidenceLimits(bytes.Length - 1, 100, 100, 10000) }) {
            using var stream = new MemoryStream(bytes); Assert.Equal("invalid_media", (await Decoder().DecodeAsync(stream, limits, default)).Status);
        }
    }
    [WindowsStorageFact]
    public async Task Binary_hash_mismatch_is_unavailable_before_reading_source() {
        var options = Options() with { AssemblySha256 = new string('0', 64) };
        using var source = new MemoryStream(Png(13, 7));
        Assert.Equal("decoder_unavailable", (await new WindowsImageDecoder(options).DecodeAsync(source, Limits(), default)).Status);
        Assert.Equal(0, source.Position);
    }
    [WindowsStorageFact]
    public async Task Actual_job_memory_limit_prevents_unbounded_decoder_process() {
        var bytes = Png(3000, 3000); var limits = new EvidenceLimits(1_000_000, 4000, 4000, 16_000_000);
        using var normal = new MemoryStream(bytes); Assert.Equal("decoded", (await Decoder().DecodeAsync(normal, limits, default)).Status);
        using var source = new MemoryStream(bytes);
        var result = await new WindowsImageDecoder(Options() with { MaximumMemoryBytes = 16 * 1024 * 1024 }).DecodeAsync(source, limits, default);
        Assert.NotEqual("decoded", result.Status); Assert.Null(result.Mime);
    }
    [WindowsStorageFact]
    public async Task Deadline_terminates_real_child_even_when_input_source_never_finishes() {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var source = new StalledSource();
        var result = await new WindowsImageDecoder(Options() with { Timeout = TimeSpan.FromMilliseconds(300) }).DecodeAsync(source, Limits(), default);
        Assert.Equal("decode_timeout", result.Status); Assert.True(started.Elapsed < TimeSpan.FromSeconds(8));
    }
    private static WindowsImageDecoder Decoder() => new(Options());
    private static EvidenceLimits Limits() => new(10000, 100, 100, 10000);
    internal static ImageDecoderOptions Options() {
        var runtime = Environment.GetEnvironmentVariable("UNITTOKO_DECODER_DOTNET") ?? throw new InvalidOperationException("Set the approved local .NET runtime for the actual isolated decoder tests");
        var dll = Path.Combine(AppContext.BaseDirectory, "ImageDecoder", "UnitToko.ImageDecoder.dll");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return new(runtime, dll, Hash(runtime), Hash(dll), Hash(Path.ChangeExtension(dll, ".deps.json")), Hash(Path.ChangeExtension(dll, ".runtimeconfig.json")),
            Hash(Path.Combine(Path.GetDirectoryName(dll)!, "Magick.NET-Q8-AnyCPU.dll")), Hash(Path.Combine(Path.GetDirectoryName(dll)!, "Magick.NET.Core.dll")),
            Hash(Path.Combine(Path.GetDirectoryName(dll)!, "runtimes", "win-x64", "native", "Magick.Native-Q8-x64.dll")), Hash(Path.Combine(Path.GetDirectoryName(dll)!, "policy.xml")), 256 * 1024 * 1024, TimeSpan.FromSeconds(10));
    }
    internal static byte[] Png(int width, int height, bool corruptPixels = false, bool animation = false) {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        if (animation) Chunk("acTL", new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 });
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true)) zlib.Write(new byte[checked((width * 3 + 1) * height)]);
        Chunk("IDAT", corruptPixels ? new byte[] { 1, 2, 3 } : compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
        void Chunk(string type, byte[] bytes) {
            var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length); output.Write(size);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(bytes).ToArray(); output.Write(body);
            var crc = uint.MaxValue; foreach (var value in body) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
            BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); output.Write(size);
        }
    }
    private sealed class StalledSource : Stream {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
