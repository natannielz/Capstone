using System.Buffers.Binary;

namespace UnitToko.ImageDecoder;

// Reject truncated/trailing containers and animation before the actual full-pixel decoder.
// A successful envelope check alone never reports a decoded image.
internal static class ImageEnvelope
{
    internal static string Media(ReadOnlySpan<byte> bytes) {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { Png(bytes); return "image/png"; }
        if (bytes.StartsWith(new byte[] { 255, 216 })) { Jpeg(bytes); return "image/jpeg"; }
        throw new InvalidDataException();
    }
    private static void Png(ReadOnlySpan<byte> bytes) {
        var offset = 8; var header = false; var image = false; var endedData = false;
        while (offset <= bytes.Length - 12) {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12) throw new InvalidDataException();
            var count = (int)length; var type = bytes.Slice(offset + 4, 4); var body = bytes.Slice(offset + 8, count);
            if (type.ToArray().Any(b => b is not (>= 65 and <= 90) and not (>= 97 and <= 122)) || (type[2] & 32) != 0) throw new InvalidDataException();
            if (Crc(bytes.Slice(offset + 4, count + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 8 + count)..])) throw new InvalidDataException();
            if (!header && !type.SequenceEqual("IHDR"u8)) throw new InvalidDataException();
            if (type.SequenceEqual("IHDR"u8)) { if (header || count != 13) throw new InvalidDataException(); header = true; }
            else if (type.SequenceEqual("IDAT"u8)) { if (endedData) throw new InvalidDataException(); image = true; }
            else {
                if (image) endedData = true;
                if (type.SequenceEqual("IEND"u8)) { if (!image || count != 0 || offset + 12 != bytes.Length) throw new InvalidDataException(); return; }
                if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) throw new InvalidDataException();
                if ((type[0] & 32) == 0 && !type.SequenceEqual("PLTE"u8)) throw new InvalidDataException();
            }
            offset += count + 12;
        }
        throw new InvalidDataException();
    }
    private static uint Crc(ReadOnlySpan<byte> bytes) {
        var crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        return ~crc;
    }
    private static void Jpeg(ReadOnlySpan<byte> bytes) {
        var offset = 2; var scan = false; var frame = false; var entropy = false;
        while (offset < bytes.Length) {
            if (entropy) { while (offset < bytes.Length && bytes[offset] != 255) offset++; }
            if (offset >= bytes.Length || bytes[offset++] != 255) throw new InvalidDataException();
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length) throw new InvalidDataException();
            var marker = bytes[offset++];
            if (entropy && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            entropy = false;
            if (marker == 0xd9) { if (!scan || !frame || offset != bytes.Length) throw new InvalidDataException(); return; }
            if (marker is 0 or 0xd8 or 0x01 or >= 0xd0 and <= 0xd7 || offset + 2 > bytes.Length) throw new InvalidDataException();
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (size < 2 || size > bytes.Length - offset) throw new InvalidDataException();
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc)) { if (frame || marker is not (0xc0 or 0xc1 or 0xc2)) throw new InvalidDataException(); frame = true; }
            if (marker == 0xe2 && bytes.Slice(offset + 2, size - 2).StartsWith("MPF\0"u8)) throw new InvalidDataException();
            if (marker == 0xda) { if (!frame) throw new InvalidDataException(); scan = true; entropy = true; }
            offset += size;
        }
        throw new InvalidDataException();
    }
}
