using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Procurement;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Documents;

// These are isolated, named synthetic approvals, not approval of a production scanner, model or host.
[SupportedOSPlatform("windows")]
internal static class PdfDocumentOcrProcessingTestData
{
    internal const string SupplierConsumer = "synthetic-whole-pdf-supplier-worker";
    internal const string Host = "synthetic-pdf-ocr-host-not-production-acceptance", Model = "synthetic-pdf-ocr-model-not-production-acceptance",
        Raster = "synthetic-pdf-ocr-raster-not-production-acceptance";
    internal static PdfDocumentOcrRuntimeOptions Options(string root, string store, string consumer, LocalOcrOptions? engine = null) {
        var raster = WindowsPdfRasterTests.Options();
        return new(root, store, StorageRoot.TrustedOperators, consumer, Host, Model, Raster,
            new(raster.PythonExecutable, raster.HelperPath, raster.PackageRoot, raster.PillowRoot, raster.WheelPath, raster.PillowVersion,
                raster.Files.Select(pin => new PdfEvidenceRuntimePin(pin.Path, pin.Sha256)).ToArray(), raster.Limits.MaximumPages,
                raster.Limits.MaximumPagePixels, raster.Limits.MaximumTotalPixels, raster.Limits.MaximumOutputBytes, raster.MaximumMemoryBytes, raster.Timeout),
            engine ?? LocalOcrTests.Options());
    }
    internal static Task PolicyAsync(IMongoDatabase database, UnitToko.Platform.IClock clock, string store, PdfDocumentOcrRuntime runtime) =>
        database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Policies).InsertOneAsync(new BsonDocument {
            { "_id", 1L }, { "schemaVersion", 1 }, { "consumerId", SupplierConsumer },
            { "storeId", store }, { "pipelineId", runtime.PipelineId }, { "sourceKind", runtime.SourceKind }, { "purpose", runtime.SourceKind },
            { "inputMime", PdfDocumentExtractionContract.InputMime }, { "rasterProvenance", PdfDocumentExtractionRecords.RasterProvenance(runtime.Processor.RasterProvenance) },
            { "ocrProvenance", PdfDocumentExtractionRecords.OcrProvenance(runtime.Processor.OcrProvenance) },
            { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", clock.UtcNow.AddMinutes(-1).UtcDateTime },
            { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime }, { "hostAcceptanceRef", Host }, { "modelSelectionRef", Model }, { "rasterAcceptanceRef", Raster } });
    internal static Task ReadPolicyAsync(IMongoDatabase database, string store) => database.GetCollection<BsonDocument>("evidenceReadPolicies")
        .UpdateOneAsync(new BsonDocument("storeId", store), Builders<BsonDocument>.Update.Set("maximumBytes", 1024L * 1024)
            .Set("verificationDurationMs", 60000L).Set("leaseDurationMs", 10000L).Set("requestDurationMs", 60000L));
    internal static async Task<BsonDocument> FinancialAsync(IMongoDatabase database) {
        var state = new BsonDocument();
        foreach (var name in new[] { "payments", "bankReceipts", "journalEntries", "stockMovements", "inventoryReservations", "purchaseOrderDisbursements", "supplierDocumentMatches" })
            state[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal static byte[] Pdf(ExtractionPurpose purpose) => Pdf((Lines(purpose, false), 0), (Lines(purpose, true), 90));
    private static string[] Lines(ExtractionPurpose purpose, bool alternate) {
        var amount = alternate ? "2.345.678,90" : "1.234.567,89";
        return purpose == ExtractionPurpose.Payment
            ? ["BUKTI TRANSFER SINTETIS", "Nominal : Rp " + amount, "Tanggal : 30/09/2026", "Waktu : 14:25:36",
                "Nama penerima : KOPERASI DEMO", "Rekening tujuan : 1234567890", "Bank tujuan : BANK DEMO", "Referensi : DEMO-20260930-1"]
            : ["Nomor invoice : SYNTH-INVOICE-001", "Tanggal invoice : 03/10/2026", "Nama pemasok : PEMASOK CONTOH", "Total tagihan : Rp " + amount,
                "Nomor PO : SYNTH-PO-001", "Nomor surat jalan : SYNTH-SJ-001", "Kode barang pemasok : PROC-PENCIL-12"];
    }
    // A real two-page PDF with font/text and a rotated second page; no engine outputs are fabricated.
    internal static byte[] Pdf(params (string[] Lines, int Rotation)[] pages) {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<int>();
        foreach (var page in pages) {
            var pageId = objects.Count + 1; kids.Add(pageId);
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 576 360] /Rotate {page.Rotation} /Resources << /Font << /F1 3 0 R >> >> /Contents {pageId + 1} 0 R >>"));
            var contents = new StringBuilder("BT /F1 16 Tf 0 g 24 330 Td 32 TL\n");
            foreach (var line in page.Lines) contents.Append('(').Append(line.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal)).Append(") Tj T*\n");
            contents.Append("ET\n"); objects.Add(FormattableString.Invariant($"<< /Length {contents.Length} >>\nstream\n{contents}\nendstream"));
        }
        objects[1] = FormattableString.Invariant($"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', kids.Select(id => id.ToString(CultureInfo.InvariantCulture) + " 0 R"))}] >>");
        var document = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++) { offsets.Add(document.Length); document.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = document.Length; document.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) document.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        document.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(document.ToString());
    }
    internal static async Task NoOutputAsync(IMongoDatabase database, BsonDocument claim, bool noFailure = true) {
        foreach (var name in new[] { PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits })
            Assert.Equal(0, await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(new BsonDocument()));
        if (noFailure) Assert.Equal(0, await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Failures).CountDocumentsAsync(new BsonDocument()));
        Assert.False((await database.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("_id", claim["_id"])).SingleAsync()).Contains("processingReceipt"));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SupplierPdfOcrData(SupplierDocumentTestData data, PdfEvidenceAdmissionTestRuntime scanner,
    PdfDocumentOcrRuntime runtime, ReservationResult reservation, EvidenceRef reference) : IAsyncDisposable
{
    internal SupplierDocumentTestData Data { get; } = data;
    internal PdfEvidenceAdmissionTestRuntime Scanner { get; } = scanner;
    internal PdfDocumentOcrRuntime Runtime { get; } = runtime;
    internal ReservationResult Reservation { get; } = reservation;
    internal EvidenceRef Reference { get; } = reference;
    internal IMongoDatabase Database => Data.Database;
    internal MongoUnitOfWork Unit => Data.Unit;
    internal OutboxStore Queue => new(Database, Data.Clock);
    internal IMongoCollection<BsonDocument> Rows(string name) => Data.Rows(name);
    internal static async Task<SupplierPdfOcrData> CreateAsync(MongoFixture fixture, LocalOcrOptions? options = null, bool approve = true) {
        var data = await SupplierDocumentTestData.CreateAsync(fixture); PdfEvidenceAdmissionTestRuntime? scanner = null;
        try {
            scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, data.Unit, data.Clock, data.Files.Path, ProcurementInspectionTestData.StoreId);
            await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(data.Database, ProcurementInspectionTestData.StoreId);
            var reservation = await data.Evidence.ReservePdfAsync(data.Actor, data.DraftId, 1, SupplierDocumentTestData.Meta("supplier-native-pdf"));
            using var input = new MemoryStream(PdfDocumentOcrProcessingTestData.Pdf(ExtractionPurpose.SupplierInvoice));
            var uploaded = await data.Evidence.UploadPdfAsync(data.Actor, data.DraftId, reservation.ReservationId, reservation.Revision, input, SupplierDocumentTestData.Meta("supplier-native-pdf-upload"));
            var reference = uploaded.Evidence!.Reference;
            Assert.Equal("completed", await scanner.Runtime.RunOnceAsync());
            var runtime = PdfDocumentOcrRuntime.CreateSupplierInvoice(data.Database, data.Unit, new(data.Database), data.Clock,
                PdfDocumentOcrProcessingTestData.Options(data.Files.Path, ProcurementInspectionTestData.StoreId, PdfDocumentOcrProcessingTestData.SupplierConsumer, options));
            if (approve) await PdfDocumentOcrProcessingTestData.PolicyAsync(data.Database, data.Clock, ProcurementInspectionTestData.StoreId, runtime);
            var preview = await data.Service.PreviewSubmitAsync(data.Actor, data.DraftId, 1, reservation.ReservationId, reference);
            await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reservation.ReservationId, reference, preview["binding"].AsString, SupplierDocumentTestData.Meta("supplier-native-pdf-submit"));
            return new(data, scanner, runtime, reservation, reference);
        } catch { if (scanner is not null) await scanner.DisposeAsync(); await data.DisposeAsync(); throw; }
    }
    internal Task<BsonDocument> JobAsync() => Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind)).SingleAsync();
    internal async Task<BsonDocument> ClaimAsync() => await Queue.ClaimAsync(PdfDocumentOcrProcessingTestData.SupplierConsumer, Runtime.Processor.ClaimPolicy) ?? throw new InvalidOperationException("Expected exact whole-PDF claim");
    public async ValueTask DisposeAsync() { try { await Scanner.DisposeAsync(); } finally { await Data.DisposeAsync(); } }
}
