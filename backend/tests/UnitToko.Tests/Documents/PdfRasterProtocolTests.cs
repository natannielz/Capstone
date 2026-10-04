using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;

namespace UnitToko.Tests.Documents;

public sealed class PdfRasterProtocolTests
{
    internal static readonly PdfRasterLimits Limits = new(10, 4_000_000, 25_000_000, 16 * 1024 * 1024);
    internal static readonly PdfRasterProvenance Provenance = new(PdfRasterProtocol.Pipeline, PdfRasterProtocol.Wrapper,
        PdfRasterProtocol.Pdfium, PdfRasterProtocol.WheelSha256, "12.3.0", new string('a', 64));

    [Fact]
    public void Complete_ordered_transcript_has_exact_page_bytes_and_no_writable_image_array()
    {
        var rows = Transcript(); var result = PdfRasterProtocol.Parse(Join(rows), Expected(), Provenance, Limits, default);
        var page = Assert.Single(result); Assert.Equal(2, page.Width); Assert.Equal(2, page.Height);
        Assert.Equal(0, page.PageIndex); Assert.Equal(new[] { "human_review_required" }, page.ReviewReasons);
        using var bytes = page.OpenPng(); Assert.False(bytes.CanWrite);
        using var copy = new MemoryStream(); bytes.CopyTo(copy); Assert.Equal(PdfRasterProtocol.Digest(copy.ToArray()), page.Sha256);
    }

    [Fact]
    public void Truncation_duplicates_wrong_order_source_and_extra_fields_never_return_partial_pages()
    {
        var original = Join(Transcript());
        foreach (var output in new[] { "", original.TrimEnd('\n'), string.Join('\n', original.Split('\n').Take(2)) + "\n",
            original + "{}\n", original.Replace("\"protocol\":1", "\"protocol\":1,\"protocol\":1", StringComparison.Ordinal),
            original.Replace("\"event\":\"page\"", "\"event\":\"failed\"", StringComparison.Ordinal),
            original.Replace("\"sourceSha256\":\"" + Expected().Content.Sha256, "\"sourceSha256\":\"" + new string('b', 64), StringComparison.Ordinal) })
            Assert.ThrowsAny<Exception>(() => PdfRasterProtocol.Parse(output, Expected(), Provenance, Limits, default));
        foreach (var mutation in new Action<JsonObject[]>[] {
            rows => rows[0]["unknown"] = 1,
            rows => rows[0]["runtime"]!["wrapper"] = "5.14.0",
            rows => rows[0]["pageCount"] = 2,
            rows => rows[0]["source"]!["length"] = 99,
            rows => rows[1]["page"]!["pageIndex"] = 1,
            rows => rows[1]["page"]!["canvasSize"]![0] = 1,
            rows => rows[1]["page"]!["pixelCornersInPdfCanvas"]![0]![0] = 1,
            rows => rows[1]["page"]!["reviewReasons"] = new JsonArray("accepted"),
            rows => rows[1]["page"]!["width"] = 4097,
            rows => rows[1]["page"]!["length"] = 1,
            rows => rows[1]["pngBase64"] = " " + rows[1]["pngBase64"]!.GetValue<string>(),
            rows => rows[2]["totalPixels"] = 5,
            rows => rows[2]["pagesSha256"] = new string('0', 64) }) {
            var rows = Transcript(); mutation(rows);
            Assert.ThrowsAny<Exception>(() => PdfRasterProtocol.Parse(Join(rows), Expected(), Provenance, Limits, default));
        }
        var ordered = Transcript();
        Assert.ThrowsAny<Exception>(() => PdfRasterProtocol.Parse(Join([ordered[0], ordered[2], ordered[1]]), Expected(), Provenance, Limits, default));
    }

    [Fact]
    public void Valid_recomputed_hashes_do_not_hide_png_geometry_crc_data_or_blank_claim_inconsistency()
    {
        foreach (var broken in new[] { Png()[..^1], Png().Concat(new byte[] { 0 }).ToArray(), Png(width: 3), Png(filter: 5), Png(trailingZlib: true), Png(uniform: true) }) {
            var rows = Transcript(broken); // The alleged PNG and page/terminal hashes are all recomputed.
            Assert.ThrowsAny<Exception>(() => PdfRasterProtocol.Parse(Join(rows), Expected(), Provenance, Limits, default));
        }
        var crc = Png(); crc[29] ^= 1;
        Assert.ThrowsAny<Exception>(() => PdfRasterProtocol.Parse(Join(Transcript(crc)), Expected(), Provenance, Limits, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfRasterProtocol.Parse(Join(Transcript()), Expected(), Provenance, Limits, canceled.Token));
    }

    [Fact]
    public void Failure_contract_is_finite_and_never_a_success_transcript()
    {
        Assert.Equal("encrypted_pdf", PdfRasterProtocol.Failure("{\"event\":\"failed\",\"code\":\"encrypted_pdf\",\"pageIndex\":null}\n", 2));
        Assert.Null(PdfRasterProtocol.Failure("{\"event\":\"failed\",\"code\":\"secret document value\",\"pageIndex\":null}\n", 2));
        Assert.Null(PdfRasterProtocol.Failure("{\"event\":\"failed\",\"code\":\"encrypted_pdf\",\"pageIndex\":null}\n", 0));
    }

    internal static ExpectedPublication Expected(byte[]? content = null) {
        content ??= "synthetic original PDF"u8.ToArray();
        return new(new(new string('1', 64), 1, 2, 1, new string('2', 64), new string('3', 64)), 1, new(content.LongLength, PdfRasterProtocol.Digest(content)));
    }
    internal static JsonObject[] Transcript(byte[]? png = null) {
        png ??= Png(); var expected = Expected();
        var page = new JsonObject { ["pageIndex"] = 0, ["sourceSha256"] = expected.Content.Sha256,
            ["canvasSize"] = new JsonArray(.5, .5), ["visibleBox"] = new JsonArray(0, 0, .5, .5), ["rotationDegrees"] = 0,
            ["width"] = 2, ["height"] = 2, ["pixelCornersInPdfCanvas"] = new JsonArray(new JsonArray(0, .5), new JsonArray(.5, .5), new JsonArray(.5, 0), new JsonArray(0, 0)),
            ["mode"] = "L", ["mime"] = "image/png", ["length"] = png.Length, ["sha256"] = PdfRasterProtocol.Digest(png), ["reviewReasons"] = new JsonArray("human_review_required") };
        return [new() { ["event"] = "document", ["protocol"] = 1, ["pipeline"] = PdfRasterProtocol.Pipeline,
            ["source"] = new JsonObject { ["sha256"] = expected.Content.Sha256, ["length"] = expected.Content.Length, ["mime"] = "application/pdf" },
            ["runtime"] = new JsonObject { ["wrapper"] = Provenance.Wrapper, ["pdfium"] = Provenance.Pdfium, ["wheelSha256"] = Provenance.WheelSha256, ["pillow"] = Provenance.Pillow },
            ["limits"] = new JsonObject { ["maximumPages"] = Limits.MaximumPages, ["maximumPagePixels"] = Limits.MaximumPagePixels, ["maximumTotalPixels"] = Limits.MaximumTotalPixels, ["maximumOutputBytes"] = Limits.MaximumOutputBytes },
            ["pageCount"] = 1, ["scale"] = new JsonObject { ["numerator"] = 25, ["denominator"] = 9 }, ["requiresHumanReview"] = true },
            new() { ["event"] = "page", ["page"] = page, ["pngBase64"] = Convert.ToBase64String(png) },
            new() { ["event"] = "completed", ["sourceSha256"] = expected.Content.Sha256, ["pageCount"] = 1, ["totalPixels"] = 4,
                ["pagesSha256"] = PdfRasterProtocol.Digest(Encoding.ASCII.GetBytes(page.ToJsonString() + "\n")) }];
    }
    internal static string Join(JsonObject[] rows) => string.Join('\n', rows.Select(row => row.ToJsonString())) + "\n";
    private static byte[] Png(int width = 2, byte filter = 0, bool trailingZlib = false, bool uniform = false) {
        using var result = new MemoryStream(); result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2); header[8] = 8;
        Chunk("IHDR", header); using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) for (var y = 0; y < 2; y++) {
            zlib.WriteByte(filter); for (var x = 0; x < width; x++) zlib.WriteByte(uniform ? (byte)255 : (byte)(x == 0 ? 0 : 255));
        }
        if (trailingZlib) compressed.Write("hidden"u8); Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return result.ToArray();
        void Chunk(string name, byte[] payload) {
            var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, payload.Length); result.Write(length);
            var body = Encoding.ASCII.GetBytes(name).Concat(payload).ToArray(); result.Write(body); uint crc = uint.MaxValue;
            foreach (var value in body) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); result.Write(length);
        }
    }
}
