using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitToko.Platform.DocumentProcessing;

/// <summary>Only the helper's complete 8-bit gray, noninterlaced PNG form is admitted.</summary>
internal static class PdfRasterPng
{
    internal static bool Require(byte[] png, int width, int height, CancellationToken ct)
    {
        if (png.Length is < 57 or > 10 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid();
        var offset = 8; var header = false; var ended = false; var data = false;
        using var compressed = new MemoryStream(png.Length);
        while (offset < png.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (png.Length - offset < 12) throw Invalid();
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > (uint)(png.Length - offset - 12)) throw Invalid();
            var size = checked((int)length); var type = png.AsSpan(offset + 4, 4); var payload = png.AsSpan(offset + 8, size);
            if (Crc(png.AsSpan(offset + 4, size + 4), ct) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + size, 4))) throw Invalid();
            if (type.SequenceEqual("IHDR"u8))
            {
                if (header || offset != 8 || size != 13 || BinaryPrimitives.ReadUInt32BigEndian(payload) != width ||
                    BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) != height || !payload[8..].SequenceEqual(new byte[] { 8, 0, 0, 0, 0 })) throw Invalid();
                header = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!header || ended || size == 0) throw Invalid();
                data = true; compressed.Write(payload);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (!header || !data || ended || size != 0 || offset + 12 != png.Length) throw Invalid();
                ended = true;
            }
            else throw Invalid(); // The pinned encoder emits no metadata or alternate image frames.
            offset += size + 12;
        }
        if (!ended || compressed.Length < 6) throw Invalid();
        compressed.Position = 0;
        // One-byte compressed reads prevent read-ahead from hiding a second zlib stream/trailing payload.
        using var exact = new ExactCompressedInput(compressed, ct);
        using var decoded = new ZLibStream(exact, CompressionMode.Decompress, leaveOpen: true);
        var row = new byte[checked(width + 1)]; var previous = new byte[width]; var current = new byte[width];
        var minimum = 255; var maximum = 0;
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested(); decoded.ReadExactly(row);
            if (row[0] > 4) throw Invalid();
            for (var x = 0; x < width; x++)
            {
                var left = x == 0 ? 0 : current[x - 1]; var above = previous[x]; var upperLeft = x == 0 ? 0 : previous[x - 1];
                var predictor = row[0] switch { 0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2, _ => Paeth(left, above, upperLeft) };
                current[x] = unchecked((byte)(row[x + 1] + predictor)); minimum = Math.Min(minimum, current[x]); maximum = Math.Max(maximum, current[x]);
            }
            (previous, current) = (current, previous);
        }
        if (decoded.ReadByte() != -1 || compressed.Position != compressed.Length) throw Invalid();
        return minimum == maximum;
    }
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    private static uint Crc(ReadOnlySpan<byte> value, CancellationToken ct)
    {
        uint crc = uint.MaxValue;
        for (var index = 0; index < value.Length; index++) { if (index % 4096 == 0) ct.ThrowIfCancellationRequested(); crc ^= value[index]; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        return ~crc;
    }
    private static InvalidDataException Invalid() => new("pdf_raster_invalid_output");
    private sealed class ExactCompressedInput(Stream source, CancellationToken ct) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { ct.ThrowIfCancellationRequested(); return source.Read(buffer, offset, Math.Min(count, 1)); }
        public override int Read(Span<byte> buffer) { ct.ThrowIfCancellationRequested(); return source.Read(buffer[..Math.Min(buffer.Length, 1)]); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
