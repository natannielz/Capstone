using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

// Uses the real pinned image primary, separate private PDF admission/association and
// whole-page engine. Each approval and scanner endpoint is explicitly synthetic.
[SupportedOSPlatform("windows")]
internal sealed class PaymentSupplementPdfOcrTestData : IAsyncDisposable
{
    internal const string Consumer = "synthetic-payment-supplement-whole-pdf-worker";
    internal const long MaximumOriginalBytes = 1024L * 1024;
    internal PaymentSupplementEvidenceTestData Evidence { get; private set; } = null!;
    internal PaymentEvidenceReadTests.ReadData Data => Evidence.Data;
    internal PdfEvidenceAdmissionTestRuntime Scanner { get; private set; } = null!;
    internal PdfDocumentOcrRuntime Runtime { get; private set; } = null!;
    internal ReservationResult Reservation { get; private set; } = null!;
    internal EvidenceRef Reference { get; private set; } = null!;
    internal byte[] Bytes { get; private set; } = null!;
    internal BsonDocument Before { get; private set; } = null!;
    internal IMongoDatabase Database => Data.Database;
    internal MongoUnitOfWork Unit => new(Data.Catalog.Client);
    internal OutboxStore Queue => new(Database, Data.Catalog.Clock);
    internal IMongoCollection<BsonDocument> Rows(string name) => Data.Rows(name);

    internal static async Task<PaymentSupplementPdfOcrTestData> CreateAsync(MongoFixture fixture,
        LocalOcrOptions? engine = null, bool approve = true, byte[]? primaryImageBytes = null, long maximumValidationAgeMs = 86400000)
    {
        var result = await CreateDraftAsync(fixture, engine, approve, primaryImageBytes, maximumValidationAgeMs);
        try {
            var primary = result.Evidence.Primary; var supplement = result.Evidence.Supplement;
            result.Reservation = await result.Evidence.Facade.ReservePdfAsync(result.Data.Actor, primary.DeclarationId,
                supplement.SupplementId, 1, PaymentSupplementEvidenceTestData.Meta("whole-PDF-reserve"));
            result.Bytes = PdfDocumentOcrProcessingTestData.Pdf(ExtractionPurpose.Payment);
            using var bytes = new MemoryStream(result.Bytes);
            Assert.InRange(result.Bytes.LongLength, 1L, MaximumOriginalBytes);
            var uploaded = await result.Evidence.Facade.UploadPdfAsync(result.Data.Actor, primary.DeclarationId, supplement.SupplementId,
                result.Reservation.ReservationId, result.Reservation.Revision, bytes, PaymentSupplementEvidenceTestData.Meta("whole-PDF-upload"));
            // Quarantine proves durable upload only; eligibility comes from the subsequent real validation/status read.
            Assert.Equal("quarantined", uploaded.State); Assert.NotNull(uploaded.Evidence);
            Assert.Equal(result.Reservation.ReservationId, uploaded.Evidence.ReservationId);
            Assert.Equal(PdfRasterProtocol.Digest(result.Bytes), uploaded.Evidence.Reference.Sha256);
            await RequireQueuedValidationAsync(result.Data, result.Bytes, result.Reservation.ReservationId);
            Assert.Equal("completed", await result.Scanner.Runtime.RunOnceAsync());
            var eligible = await result.Evidence.Facade.PdfStatusAsync(result.Data.Actor, primary.DeclarationId,
                supplement.SupplementId, 1, result.Reservation.ReservationId);
            Assert.Equal("quarantined", eligible.State); Assert.Equal("clean", eligible.ScanStatus);
            Assert.Equal("decoded", eligible.DecoderStatus); Assert.Null(eligible.SafeFailure);
            Assert.NotNull(eligible.Reference); result.Reference = eligible.Reference!;
            var proof = await result.Rows(PdfEvidenceValidationRecords.Results)
                .Find(new BsonDocument("_id", result.Reservation.VersionRowId)).SingleAsync();
            Assert.Equal("passed", proof["status"]); Assert.Equal("application/pdf", proof["mime"]);
            Assert.Equal(2, proof["pageCount"].AsInt32); Assert.True(PdfEvidenceValidationRecords.CompletePages(proof));
            Assert.Equal(result.Reference.Sha256, proof["original"]["sha256"]);
            Assert.Equal(result.Reservation.ReservationId, proof["original"]["reservationId"]);
            Assert.Equal(PdfEvidenceValidationRecords.Hash(proof), proof["resultHash"]);
            var attached = await result.Evidence.Facade.AttachAsync(result.Data.Actor, primary.DeclarationId, supplement.SupplementId, 1,
                result.Reservation.ReservationId, result.Reference, PaymentSupplementEvidenceTestData.Meta("whole-PDF-attach"));
            Assert.Equal(2L, attached["supplementRevision"]);
            return result;
        } catch { await result.DisposeAsync(); throw; }
    }

    // Browser acceptance performs reserve/upload/attach through the actual API.
    // This creates only the real IMAGE primary, own1 supplement and explicit test policies.
    internal static async Task<PaymentSupplementPdfOcrTestData> CreateDraftAsync(MongoFixture fixture,
        LocalOcrOptions? engine = null, bool approve = true, byte[]? primaryImageBytes = null, long maximumValidationAgeMs = 86400000)
    {
        var result = new PaymentSupplementPdfOcrTestData();
        try {
            result.Evidence = await PaymentSupplementEvidenceTestData.CreateAsync(fixture, maximumContentBytes: MaximumOriginalBytes,
                primaryImageBytes: primaryImageBytes, primaryImageLimits: primaryImageBytes is null ? null : new(MaximumOriginalBytes, 1001, 651, 651651),
                maximumReadBytes: primaryImageBytes is null ? 1024 : MaximumOriginalBytes,
                verificationDurationMs: primaryImageBytes is null ? 10000 : 60000, leaseDurationMs: primaryImageBytes is null ? 5000 : 10000,
                maximumValidationAgeMs: maximumValidationAgeMs);
            result.Before = await result.SnapshotAsync();
            result.Scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(result.Database, result.Unit, result.Data.Catalog.Clock,
                result.Data.Files.Root, result.Data.Files.StoreId);
            await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(result.Database, result.Data.Files.StoreId);
            result.Runtime = PdfDocumentOcrRuntime.CreatePaymentSupplement(result.Database, result.Unit, new(result.Database), result.Data.Catalog.Clock,
                PdfDocumentOcrProcessingTestData.Options(result.Data.Files.Root, result.Data.Files.StoreId, Consumer, engine));
            if (approve) await result.ApproveProcessingAsync();
            return result;
        } catch { await result.DisposeAsync(); throw; }
    }

    internal static async Task RequireQueuedValidationAsync(PaymentEvidenceReadTests.ReadData data, byte[] bytes,
        long? reservationId = null)
    {
        var filter = new BsonDocument("purpose", "PaymentSupplementPdf");
        if (reservationId is { } id) filter.Add("_id", id);
        var reservation = await data.Rows("evidenceUploadReservations").Find(filter).SingleAsync();
        var version = await data.Rows("documentVersions").Find(new BsonDocument("_id", reservation["versionRowId"])).SingleAsync();
        Assert.Equal("quarantined", reservation["state"]); Assert.Equal("quarantined", version["state"]);
        Assert.Equal(MaximumOriginalBytes, reservation["admittedLimits"]["maximumContentBytes"].AsInt64);
        Assert.Equal(bytes.LongLength, version["length"].AsInt64); Assert.Equal(PdfRasterProtocol.Digest(bytes), version["sha256"].AsString);
        var sourceKey = FormattableString.Invariant($"evidence.quarantined:{version["_id"].AsInt64}:1");
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", sourceKey)).SingleAsync();
        Assert.Equal(PdfEvidenceValidationRecords.Kind, job["kind"]); Assert.Equal("pending", job["status"]);
        Assert.Equal(0, job["attempts"].AsInt32); Assert.False(job.Contains("processingReceipt"));
        var payload = new BsonDocument {
            { "lineageId", reservation["lineageId"] }, { "versionRowId", version["_id"] }, { "reservationId", reservation["_id"] },
            { "attemptId", reservation["attemptId"] }, { "contentVersion", 1L }, { "contentRevision", 1L },
            { "sha256", version["sha256"] }, { "lifecycleRevision", version["lifecycleRevision"] } };
        Assert.True(payload.Equals(job["payload"]), "Queued security job must bind the exact uploaded supplementary original");
        Assert.False(await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument("_id", version["_id"])).AnyAsync());
    }

    internal Task ApproveProcessingAsync() => ApproveProcessingAsync(Database, Data.Catalog.Clock, Data.Files.StoreId, Runtime);
    internal static async Task ApproveProcessingAsync(IMongoDatabase database, UnitToko.Platform.IClock clock, string storeId,
        PdfDocumentOcrRuntime runtime) => await database.GetCollection<BsonDocument>(PdfDocumentExtractionRecords.Policies).InsertOneAsync(new BsonDocument {
        { "_id", (await new IdAllocator(database).NextAsync(PdfDocumentExtractionRecords.Policies)).Value }, { "schemaVersion", 1 },
        { "consumerId", Consumer }, { "storeId", storeId }, { "pipelineId", runtime.PipelineId },
        { "sourceKind", "PaymentSupplement" }, { "purpose", "Payment" }, { "inputMime", PdfDocumentExtractionContract.InputMime },
        { "rasterProvenance", PdfDocumentExtractionRecords.RasterProvenance(runtime.Processor.RasterProvenance) },
        { "ocrProvenance", PdfDocumentExtractionRecords.OcrProvenance(runtime.Processor.OcrProvenance) },
        { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", clock.UtcNow.AddMinutes(-1).UtcDateTime },
        { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime },
        { "hostAcceptanceRef", PdfDocumentOcrProcessingTestData.Host }, { "modelSelectionRef", PdfDocumentOcrProcessingTestData.Model },
        { "rasterAcceptanceRef", PdfDocumentOcrProcessingTestData.Raster } });

    internal Task<BsonDocument> JobAsync() => Rows("outboxEvents").Find(new BsonDocument("kind", UnitToko.Platform.Billing.PaymentSupplementBson.JobKind)).SingleAsync();
    internal async Task<BsonDocument> ClaimAsync() => await Queue.ClaimAsync(Consumer, Runtime.Processor.ClaimPolicy)
        ?? throw new InvalidOperationException("Expected exact separate supplemental PDF claim");
    internal async Task<BsonDocument> SnapshotAsync()
    {
        var result = await Evidence.FinancialAsync();
        result["primaryPins"] = new BsonArray(await Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSubmission")).ToListAsync());
        return result;
    }
    internal async Task UnchangedPrimaryAndMoneyAsync()
    {
        var after = await SnapshotAsync();
        foreach (var field in Before.Names)
            Assert.True(Before[field].Equals(after[field]), "Primary image or financial collection changed: " + field);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (Scanner is not null) await Scanner.DisposeAsync(); }
        finally { if (Evidence is not null) await Evidence.DisposeAsync(); }
    }
}
