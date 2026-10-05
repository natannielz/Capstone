using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

// Genuine sealed/mixed inspection, optional approved quality subset, immutable PDF,
// named synthetic scanner approvals and the actual pinned all-page OCR engine.
[SupportedOSPlatform("windows")]
internal sealed class SupplierDocumentPdfMatchTestData : IAsyncDisposable
{
    internal SupplierDocumentTestData Data { get; private set; } = null!;
    internal ProcurementQualityAcceptanceTestData? Quality { get; private set; }
    internal BsonDocument? Allocation { get; private set; }
    internal PdfEvidenceAdmissionTestRuntime Scanner { get; private set; } = null!;
    internal PdfDocumentOcrRuntime Runtime { get; private set; } = null!;
    internal byte[] Bytes { get; private set; } = null!;
    internal SupplierDocumentInput Input { get; private set; } = null!;
    internal BsonDocument Result { get; private set; } = null!;
    internal BsonDocument Baseline { get; private set; } = null!;
    internal IMongoCollection<BsonDocument> Rows(string name) => Data.Rows(name);
    internal SupplierDocumentPdfMatchObservation Observation(int page = 1, string field = "total_amount") =>
        new(field, new(Result["_id"].AsInt64, Result["resultHash"].AsString, Data.Reference, page, field), true);
    internal SupplierDocumentMatchInput PlainInput() {
        var input = Data.MatchInput(reviewed: Input);
        return Allocation is null ? input : input with { Lines = [input.Lines[0] with { QualityAllocationId = Allocation["allocationId"].AsInt64 }] };
    }
    internal SupplierDocumentMatchInput AnnotatedInput() => PlainInput() with { PdfObservations = ImmutableArray.Create(Observation()) };

    internal static async Task<SupplierDocumentPdfMatchTestData> CreateAsync(MongoFixture fixture, bool quality = false, bool submit = true) {
        var result = new SupplierDocumentPdfMatchTestData();
        try {
            if (quality) {
                result.Quality = await ProcurementQualityAcceptanceTestData.CreateAsync(fixture, receiptPackages: 2);
                result.Data = await SupplierDocumentTestData.CreateFromInspectionAsync(result.Quality.Inspection);
                var accepted = result.Quality.Input(packages: 2);
                var proposal = await result.Quality.ProposeAsync("pdf-match-quality-propose", accepted);
                result.Allocation = await result.Quality.DecideAsync(proposal["proposalId"].AsInt64, "pdf-match-quality-approved", accepted);
            } else result.Data = await SupplierDocumentTestData.CreateAsync(fixture);
            // Every business fixture remains at the genuinely migrated current registry.
            // Explicit predecessor rehearsals below have their own finite empty-owner guard.
            if (!submit) result.Data.Clock.UseSystemClock = true;
            Assert.True(CollectionMigrations.CurrentVersion >= 89);
            await new CollectionMigrations(result.Data.Database, result.Data.Clock).ApplyAsync();
            Assert.True(await new CollectionMigrations(result.Data.Database, result.Data.Clock).IsCurrentAsync());
            await PdfDocumentOcrProcessingTestData.ReadPolicyAsync(result.Data.Database, ProcurementInspectionTestData.StoreId);
            await result.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("storeId", ProcurementInspectionTestData.StoreId),
                Builders<BsonDocument>.Update.Set("maximumValidationAgeMs", 300000L));
            var draft = await result.Rows("supplierDocumentDrafts").Find(new BsonDocument("_id", result.Data.DraftId)).SingleAsync();
            result.Input = SupplierDocumentBson.Input(draft["input"]);
            var money = CultureInfo.GetCultureInfo("id-ID");
            var text = new[] {
                "Nomor invoice : " + result.Input.DocumentReference,
                "Tanggal invoice : " + result.Input.DocumentDate!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                "Nama pemasok : " + draft["source"]["proposal"]["supplier"]["displayName"].AsString,
                "Total tagihan : Rp " + result.Input.DocumentTotal!.Value.Value.ToString("N2", money),
                "Nomor PO : " + result.Input.AnchorOrderId.ToString(CultureInfo.InvariantCulture),
                "Kode barang pemasok : " + result.Input.Lines[0].SourceCode,
                "Jumlah kemasan : " + result.Input.Lines[0].Packages!.Value.ToString(CultureInfo.InvariantCulture),
                "Harga per kemasan : Rp " + result.Input.Lines[0].NetPricePerPackage!.Value.Value.ToString("N2", money),
                "Pajak : Rp " + result.Input.Lines[0].TaxAmount!.Value.Value.ToString("N2", money),
                "Biaya pemasok : Rp " + result.Input.Lines[0].ChargeAmount!.Value.Value.ToString("N2", money) };
            result.Bytes = PdfDocumentOcrProcessingTestData.Pdf((text, 0), (text, 90));
            result.Scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(result.Data.Database, result.Data.Unit, result.Data.Clock,
                result.Data.Files.Path, ProcurementInspectionTestData.StoreId);
            result.Runtime = PdfDocumentOcrRuntime.CreateSupplierInvoice(result.Data.Database, result.Data.Unit, new(result.Data.Database), result.Data.Clock,
                PdfDocumentOcrProcessingTestData.Options(result.Data.Files.Path, ProcurementInspectionTestData.StoreId, PdfDocumentOcrProcessingTestData.SupplierConsumer));
            // The reused installer has an explicit ID1; retain its actual counter provenance.
            Assert.Equal(1L, (await new IdAllocator(result.Data.Database).NextAsync(PdfDocumentExtractionRecords.Policies)).Value);
            await PdfDocumentOcrProcessingTestData.PolicyAsync(result.Data.Database, result.Data.Clock, ProcurementInspectionTestData.StoreId, result.Runtime);
            if (submit) await result.SubmitAsync();
            result.Baseline = await result.PhysicalAndMoneyAsync(); return result;
        } catch { await result.DisposeAsync(); throw; }
    }
    internal async Task SubmitAsync() {
        Data.Reservation = await Data.Evidence.ReservePdfAsync(Data.Actor, Data.DraftId, 1, SupplierDocumentTestData.Meta("pdf-match-reserve"));
        using var content = new MemoryStream(Bytes);
        var uploaded = await Data.Evidence.UploadPdfAsync(Data.Actor, Data.DraftId, Data.Reservation.ReservationId, Data.Reservation.Revision,
            content, SupplierDocumentTestData.Meta("pdf-match-upload"));
        Assert.Equal("quarantined", uploaded.State); Assert.NotNull(uploaded.Evidence);
        Data.Reference = uploaded.Evidence.Reference; Assert.Equal(PdfRasterProtocol.Digest(Bytes), Data.Reference.Sha256);
        Assert.Equal("completed", await Scanner.Runtime.RunOnceAsync());
        var proof = await Rows(UnitToko.Platform.Documents.PdfEvidenceValidationRecords.Results)
            .Find(new BsonDocument("_id", Data.Reservation.VersionRowId)).SingleAsync();
        Assert.Equal("passed", proof["status"]); Assert.Equal(2, proof["pageCount"]);
        Assert.True(UnitToko.Platform.Documents.PdfEvidenceValidationRecords.CompletePages(proof));
        Data.Submission = await Data.Evidence.SubmitAsync(Data.Actor, Data.DraftId, 1, Data.Reservation.ReservationId, Data.Reference,
            (await Data.PreviewSubmitAsync())["binding"].AsString, SupplierDocumentTestData.Meta("pdf-match-submit"));
        Assert.Equal("completed", await Runtime.RunOnceAsync()); Assert.Equal(Bytes, Scanner.Scanner.Bytes);
        var job = await Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind)).SingleAsync();
        Result = await Data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(Data.Database).ResultAsync(tx, job, ct))
            ?? throw new InvalidOperationException("The genuine complete SupplierInvoice result is required");
        var extraction = PdfDocumentExtractionRecords.Extract(Result);
        Assert.Equal(2, extraction.Pages.Count); Assert.Equal("SupplierInvoice", Result["sourceKind"]);
        Assert.Equal(PdfDocumentExtractionRecords.Receipt(Result), job["processingReceipt"]);
        foreach (var page in extraction.Pages) {
            Assert.Equal("34000.00", Assert.Single(page.Fields, field => field.Field == "total_amount").Value);
            Assert.Equal(Input.DocumentReference, Assert.Single(page.Fields, field => field.Field == "document_reference").Value);
        }
        Assert.Equal(Data.DocumentId, Result["binding"]["submissionId"]); Assert.Equal(Data.Reference.Sha256, Result["original"]["sha256"]);
        foreach (var viewer in new[] { Data.Actor, Data.Matcher }) {
            await using var original = await Data.Evidence.OpenPdfReadAsync(viewer, Data.DraftId, Data.Reservation.ReservationId, 1,
                Data.Reference, SupplierDocumentTestData.ReadMetadata);
            using var copy = new MemoryStream(); await original.Content.CopyToAsync(copy); Assert.Equal(Bytes, copy.ToArray());
        }
    }
    internal async Task<BsonDocument> PhysicalAndMoneyAsync() {
        var result = await PdfDocumentOcrProcessingTestData.FinancialAsync(Data.Database); result.Remove(SupplierDocumentBson.Matches);
        foreach (var name in new[] { "stockBalances", "procurementInspectionSubmissions", "procurementReceivingReceipts", "procurementStockReceipts",
            "procurementQualityAcceptanceAllocations", "supplierDocumentSubmissions", "supplierDocumentExtractionResults" })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task UnchangedAsync() {
        Assert.Equal(Baseline, await PhysicalAndMoneyAsync());
        Assert.Equal(0L, await Rows("supplierDocumentExtractionResults").CountDocumentsAsync(new BsonDocument()));
    }
    public async ValueTask DisposeAsync() {
        try { if (Scanner is not null) await Scanner.DisposeAsync(); }
        finally { try { if (Data is not null) await Data.DisposeAsync(); } finally { if (Quality is not null) await Quality.DisposeAsync(); } }
    }
}
