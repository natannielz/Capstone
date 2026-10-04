using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

// Proof refers to the assigned original PDF; rendered pages never become independently admitted evidence.
internal static class PdfEvidenceValidationRecords
{
    internal const string Kind = PdfEvidenceValidationContract.JobKind;
    internal const string Policies = "evidencePdfValidationPolicies";
    internal const string Results = "evidencePdfValidationResults";
    internal const string Audits = "evidencePdfValidationAudits";

    internal static string Hash(BsonDocument row)
    {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("resultHash");
        return Convert.ToHexString(SHA256.HashData(Canonical(copy).AsBsonDocument.ToBson())).ToLowerInvariant();
    }
    private static BsonValue Canonical(BsonValue value) => value.IsBsonDocument
        ? new BsonDocument(value.AsBsonDocument.Elements.OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new BsonElement(e.Name, Canonical(e.Value))))
        : value.IsBsonArray ? new BsonArray(value.AsBsonArray.Select(Canonical)) : value.DeepClone();
    internal static BsonDocument Original(ExpectedPublication value) => VideoValidationRecords.Original(value);
    internal static BsonDocument Receipt(BsonDocument row) => new() {
        { "evidencePdfValidation", true }, { "versionRowId", row["_id"] }, { "resultHash", row["resultHash"] },
        { "auditId", row["auditId"] }, { "pipelineId", row["pipelineId"] } };
    internal static BsonDocument Audit(BsonDocument row) => EvidenceValidationRecords.Audit(row);
    internal static BsonDocument Provenance(PdfRasterProvenance value) => new() {
        { "pipeline", value.Pipeline }, { "wrapper", value.Wrapper }, { "pdfium", value.Pdfium },
        { "wheelSha256", value.WheelSha256 }, { "pillow", value.Pillow }, { "runtimeSha256", value.RuntimeSha256 } };
    internal static BsonDocument Page(PdfRasterPage value) => new() {
        { "pageIndex", value.PageIndex }, { "width", value.Width }, { "height", value.Height }, { "length", value.Length },
        { "sha256", value.Sha256 }, { "rotationDegrees", value.RotationDegrees },
        { "canvasSize", new BsonArray(value.CanvasSize) }, { "visibleBox", new BsonArray(value.VisibleBox) },
        { "pixelCornersInPdfCanvas", new BsonArray(value.PixelCornersInPdfCanvas.Select(p => new BsonDocument { { "x", p.X }, { "y", p.Y } })) },
        { "reviewReasons", new BsonArray(value.ReviewReasons) } };

    internal static async Task<BsonDocument?> ReadAsync(IMongoDatabase db, IClientSessionHandle tx, BsonDocument job,
        ExpectedPublication original, BsonDocument source, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, db.Client) || job.GetValue("kind", "") != Kind ||
            !job.GetValue("payload", BsonNull.Value).Equals(source)) throw Broken();
        var identity = new BsonDocument("_id", source["versionRowId"]);
        var results = db.GetCollection<BsonDocument>(Results);
        var row = await results.Find(tx, identity).SingleOrDefaultAsync(ct);
        var audits = await db.GetCollection<BsonDocument>(Audits).Find(tx,
            new BsonDocument("versionRowId", source["versionRowId"])).Limit(2).ToListAsync(ct);
        if (row is null) {
            if (audits.Count != 0 || job.Contains("processingReceipt")) throw Broken();
            return null;
        }
        if (!await results.Find(tx, new BsonDocument("$and", new BsonArray { identity, PdfEvidenceValidationSchemas.Validator(Results) })).AnyAsync(ct) ||
            row["outboxId"] != job["_id"] || !row["source"].Equals(source) || !row["original"].Equals(Original(original)) ||
            row["storeId"] != original.Attempt.StoreId || row["generation"] != original.Attempt.Generation || row["length"] != original.Content.Length ||
            row["resultHash"] != Hash(row) || !job.TryGetValue("processingFence", out var fence) || !fence.IsInt64 || fence.AsInt64 <= 0 ||
            audits.Count != 1 || !audits[0].Equals(Audit(row)) || !job.GetValue("processingReceipt", BsonNull.Value).Equals(Receipt(row)) ||
            !CompletePages(row)) throw Broken();
        ct.ThrowIfCancellationRequested(); return row;
    }

    // Checks cross-page facts that cannot be inferred from a MIME label or a clean scanner verdict.
    internal static bool CompletePages(BsonDocument row)
    {
        try {
            var pages = row["pages"].AsBsonArray;
            if (row["status"] == "rejected") return pages.Count == 0 && row["pageCount"].IsBsonNull && row["rasterProvenance"].IsBsonNull;
            if (row["status"] != "passed" || row["mime"] != PdfEvidenceValidationContract.Mime ||
                !row["pageCount"].IsInt32 || row["pageCount"].AsInt32 != pages.Count || pages.Count is < 1 or > 10) return false;
            var provenance = row["rasterProvenance"].AsBsonDocument;
            if (provenance["pipeline"] != PdfRasterProtocol.Pipeline || provenance["wrapper"] != PdfRasterProtocol.Wrapper ||
                provenance["pdfium"] != PdfRasterProtocol.Pdfium || provenance["wheelSha256"] != PdfRasterProtocol.WheelSha256 ||
                !PdfRasterProtocol.Hash(provenance["runtimeSha256"].AsString)) return false;
            var pillow = provenance["pillow"].AsString;
            if (pillow.Length is < 3 or > 32 || pillow.Any(c => c is not (>= '0' and <= '9') && c != '.')) return false;
            long pixels = 0, bytes = 0;
            for (var index = 0; index < pages.Count; index++) {
                var page = pages[index].AsBsonDocument; var width = page["width"].AsInt32; var height = page["height"].AsInt32;
                var rotation = page["rotationDegrees"].AsInt32; var length = page["length"].AsInt64;
                if (page["pageIndex"].AsInt32 != index || width is < 1 or > 4096 || height is < 1 or > 4096 ||
                    (long)width * height > 4_000_000 || rotation is not (0 or 90 or 180 or 270) ||
                    length is < 57 or > 10 * 1024 * 1024 || !PdfRasterProtocol.Hash(page["sha256"].AsString)) return false;
                pixels += (long)width * height; bytes += length;
                if (pixels > 25_000_000 || bytes > 12 * 1024 * 1024) return false;
                var canvas = page["canvasSize"].AsBsonArray.Select(v => v.AsDouble).ToArray();
                var box = page["visibleBox"].AsBsonArray.Select(v => v.AsDouble).ToArray();
                if (canvas.Length != 2 || box.Length != 4 || canvas.Concat(box).Any(v => !double.IsFinite(v)) ||
                    canvas.Any(v => v <= 0) || box[2] <= box[0] || box[3] <= box[1] ||
                    Math.Ceiling(canvas[0] * (25d / 9)) != width || Math.Ceiling(canvas[1] * (25d / 9)) != height ||
                    !Near(canvas[0], rotation % 180 == 0 ? box[2] - box[0] : box[3] - box[1]) ||
                    !Near(canvas[1], rotation % 180 == 0 ? box[3] - box[1] : box[2] - box[0])) return false;
                var corners = page["pixelCornersInPdfCanvas"].AsBsonArray;
                if (corners.Count != 4) return false;
                (double X, double Y)[] unrotated = [(box[0], box[3]), (box[2], box[3]), (box[2], box[1]), (box[0], box[1])];
                for (var corner = 0; corner < corners.Count; corner++) {
                    var expected = unrotated[(corner + 4 - rotation / 90) % 4];
                    var actual = corners[corner].AsBsonDocument;
                    if (!Near(actual["x"].AsDouble, expected.X) || !Near(actual["y"].AsDouble, expected.Y)) return false;
                }
                var reasons = page["reviewReasons"].AsBsonArray;
                if (reasons.Count is < 1 or > 2 || reasons[0] != "human_review_required" ||
                    reasons.Count == 2 && reasons[1] != "no_visible_marks") return false;
            }
            return true;
        } catch (Exception error) when (error is InvalidCastException or KeyNotFoundException or FormatException or OverflowException or InvalidOperationException) { return false; }
    }
    // Persisted geometry uses the same PDF-point tolerance as the already verified raster protocol.
    private static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= .001;
    internal static PersistenceConflictException Broken() => new("evidence_pdf_validation_integrity", "Private PDF validation history needs operator review");
}
