using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal static class PdfRasterProtocol
{
    internal const string Pipeline = "pdfium-static-gray-25over9-bounded-pages-v1";
    internal const string Wrapper = "5.13.0";
    internal const string Pdfium = "153.0.7999.0";
    internal const string WheelSha256 = "47dcca2a8d507b5fd24f94c3c9d48fb379430f097bc20f01beff6c963ffbcedb";
    internal static readonly string[] SafeFailures = ["configuration", "runtime_pin", "source_limit", "source_mismatch", "malformed_pdf", "encrypted_pdf",
        "unsupported_forms", "unsupported_attachments", "unsupported_javascript", "page_limit", "geometry_limit", "total_pixel_limit", "output_limit", "deadline", "render_failed", "unavailable"];

    internal static IReadOnlyList<PdfRasterPage> Parse(string output, ExpectedPublication original, PdfRasterProvenance provenance,
        PdfRasterLimits limits, CancellationToken ct)
    {
        limits.RequireValid();
        if (output.Length == 0 || output.Length > limits.MaximumOutputBytes || output[^1] != '\n' || output.Any(c => c > 127 || c == '\r' || c == '\0')) throw Invalid();
        // 16 MiB wire, at most 12 MiB retained PNGs and one 10 MiB current PNG.
        // Including UTF-16/base64, JSON metadata, compressed bytes and row buffers,
        // these hard ceilings reserve a conservative 256 MiB managed working envelope.
        // No all-lines copies or full re-encoded base64 duplicate are retained.
        var bytes = Encoding.ASCII.GetBytes(output); var pages = new List<PdfRasterPage>();
        var offset = 0; var count = 0; var complete = false; long pixels = 0, pngBytes = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (offset < bytes.Length)
        {
            ct.ThrowIfCancellationRequested(); if (complete) throw Invalid();
            var end = Array.IndexOf(bytes, (byte)'\n', offset); if (end <= offset) throw Invalid();
            using var frame = JsonDocument.Parse(bytes.AsMemory(offset, end - offset), new() { MaxDepth = 8 });
            var row = frame.RootElement; var kind = row.GetProperty("event").GetString();
            if (offset == 0)
            {
                Shape(row, "event", "protocol", "pipeline", "source", "runtime", "limits", "pageCount", "scale", "requiresHumanReview");
                if (kind != "document" || row.GetProperty("protocol").GetInt32() != 1 || Text(row, "pipeline") != Pipeline || !row.GetProperty("requiresHumanReview").GetBoolean()) throw Invalid();
                var source = row.GetProperty("source"); Shape(source, "sha256", "length", "mime");
                if (Text(source, "sha256") != original.Content.Sha256 || source.GetProperty("length").GetInt64() != original.Content.Length || Text(source, "mime") != "application/pdf") throw Invalid();
                var runtime = row.GetProperty("runtime"); Shape(runtime, "wrapper", "pdfium", "wheelSha256", "pillow");
                if (Text(runtime, "wrapper") != provenance.Wrapper || Text(runtime, "pdfium") != provenance.Pdfium ||
                    Text(runtime, "wheelSha256") != provenance.WheelSha256 || Text(runtime, "pillow") != provenance.Pillow) throw Invalid();
                var declared = row.GetProperty("limits"); Shape(declared, "maximumPages", "maximumPagePixels", "maximumTotalPixels", "maximumOutputBytes");
                if (declared.GetProperty("maximumPages").GetInt32() != limits.MaximumPages || declared.GetProperty("maximumPagePixels").GetInt32() != limits.MaximumPagePixels ||
                    declared.GetProperty("maximumTotalPixels").GetInt32() != limits.MaximumTotalPixels || declared.GetProperty("maximumOutputBytes").GetInt32() != limits.MaximumOutputBytes) throw Invalid();
                var scale = row.GetProperty("scale"); Shape(scale, "numerator", "denominator");
                if (scale.GetProperty("numerator").GetInt32() != 25 || scale.GetProperty("denominator").GetInt32() != 9) throw Invalid();
                count = row.GetProperty("pageCount").GetInt32(); if (count < 1 || count > limits.MaximumPages) throw Invalid();
            }
            else if (kind == "page")
            {
                Shape(row, "event", "page", "pngBase64"); if (pages.Count >= count) throw Invalid();
                var facts = row.GetProperty("page");
                Shape(facts, "pageIndex", "sourceSha256", "canvasSize", "visibleBox", "rotationDegrees", "width", "height", "pixelCornersInPdfCanvas", "mode", "mime", "length", "sha256", "reviewReasons");
                if (facts.GetProperty("pageIndex").GetInt32() != pages.Count || Text(facts, "sourceSha256") != original.Content.Sha256 || Text(facts, "mode") != "L" || Text(facts, "mime") != "image/png") throw Invalid();
                var width = facts.GetProperty("width").GetInt32(); var height = facts.GetProperty("height").GetInt32(); var rotation = facts.GetProperty("rotationDegrees").GetInt32();
                if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > limits.MaximumPagePixels || rotation is not (0 or 90 or 180 or 270)) throw Invalid();
                pixels += (long)width * height; if (pixels > limits.MaximumTotalPixels) throw Invalid();
                var canvas = Numbers(facts.GetProperty("canvasSize"), 2); var box = Numbers(facts.GetProperty("visibleBox"), 4);
                if (canvas.Any(value => value <= 0) || box[2] <= box[0] || box[3] <= box[1] || Math.Ceiling(canvas[0] * (25d / 9)) != width || Math.Ceiling(canvas[1] * (25d / 9)) != height) throw Invalid();
                var cw = box[2] - box[0]; var ch = box[3] - box[1];
                if (!Near(canvas[0], rotation % 180 == 0 ? cw : ch) || !Near(canvas[1], rotation % 180 == 0 ? ch : cw)) throw Invalid();
                var cornerRows = facts.GetProperty("pixelCornersInPdfCanvas"); if (cornerRows.ValueKind != JsonValueKind.Array || cornerRows.GetArrayLength() != 4) throw Invalid();
                var corners = cornerRows.EnumerateArray().Select(value => Numbers(value, 2)).Select(value => new PdfRasterPoint(value[0], value[1])).ToArray();
                PdfRasterPoint[] unrotated = [new(box[0], box[3]), new(box[2], box[3]), new(box[2], box[1]), new(box[0], box[1])];
                for (var corner = 0; corner < 4; corner++) {
                    var expected = unrotated[(corner + 4 - rotation / 90) % 4];
                    if (!Near(corners[corner].X, expected.X) || !Near(corners[corner].Y, expected.Y)) throw Invalid();
                }
                var length = facts.GetProperty("length").GetInt32(); var hash = Text(facts, "sha256");
                if (length is < 57 or > 10 * 1024 * 1024 || !Hash(hash)) throw Invalid();
                pngBytes += length; if (pngBytes > limits.MaximumOutputBytes * 3L / 4) throw Invalid();
                var base64 = Text(row, "pngBase64");
                if (base64.Length != 4 * ((length + 2) / 3) || base64.Any(char.IsWhiteSpace)) throw Invalid();
                var png = Convert.FromBase64String(base64);
                var tail = length % 3 == 0 ? 3 : length % 3;
                if (png.Length != length || !base64.AsSpan(base64.Length - 4).SequenceEqual(Convert.ToBase64String(png.AsSpan(length - tail)).AsSpan()) || Digest(png) != hash) throw Invalid();
                var blank = PdfRasterPng.Require(png, width, height, ct);
                var reasons = facts.GetProperty("reviewReasons").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
                if (!reasons.SequenceEqual(blank ? new[] { "human_review_required", "no_visible_marks" } : new[] { "human_review_required" })) throw Invalid();
                digest.AppendData(Encoding.ASCII.GetBytes(facts.GetRawText())); digest.AppendData("\n"u8);
                pages.Add(new(pages.Count, width, height, rotation, hash, png, canvas, box, corners, reasons));
            }
            else if (kind == "completed")
            {
                Shape(row, "event", "sourceSha256", "pageCount", "totalPixels", "pagesSha256");
                if (pages.Count != count || Text(row, "sourceSha256") != original.Content.Sha256 || row.GetProperty("pageCount").GetInt32() != count ||
                    row.GetProperty("totalPixels").GetInt64() != pixels || Text(row, "pagesSha256") != Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()) throw Invalid();
                complete = true;
            }
            else throw Invalid();
            offset = end + 1;
        }
        ct.ThrowIfCancellationRequested(); if (!complete) throw Invalid();
        return pages.AsReadOnly();
    }
    internal static string? Failure(string output, int exitCode)
    {
        // Failure transcripts can contain earlier provisional frames; never parse them into usable pages.
        if (exitCode is not (2 or 3) || output.Length == 0 || output[^1] != '\n') return null;
        var previous = output.LastIndexOf('\n', output.Length - 2); var terminal = output[(previous + 1)..^1];
        using var json = JsonDocument.Parse(terminal, new() { MaxDepth = 2 }); var value = json.RootElement;
        Shape(value, "event", "code", "pageIndex"); var code = Text(value, "code"); var page = value.GetProperty("pageIndex");
        if (Text(value, "event") != "failed" || !SafeFailures.Contains(code, StringComparer.Ordinal) ||
            page.ValueKind != JsonValueKind.Null && (page.ValueKind != JsonValueKind.Number || page.GetInt32() is < 0 or > 9)) return null;
        return code;
    }
    internal static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static double[] Numbers(JsonElement value, int count) {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw Invalid();
        var values = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (values.Any(item => !double.IsFinite(item) || Math.Abs(item) > 14400)) throw Invalid(); return values;
    }
    private static bool Near(double left, double right) => Math.Abs(left - right) <= .001;
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString() ?? throw Invalid();
    private static void Shape(JsonElement value, params string[] names) {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("pdf_raster_invalid_output");
}
