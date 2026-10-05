using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Billing;

[SupportedOSPlatform("windows")]
internal sealed class PaymentSupplementCorrectionTestData : IAsyncDisposable
{
    internal PaymentSupplementPdfOcrTestData Pdf { get; private set; } = null!;
    internal PaymentEvidenceReadTests.ReadData Data => Pdf.Data;
    internal ActorContext Reviewer { get; private set; } = null!;
    internal PaymentCorrectionView Review { get; private set; } = null!;
    internal BsonDocument ImageResult { get; private set; } = null!;
    internal BsonDocument PdfResult { get; private set; } = null!;
    internal BsonDocument Before { get; private set; } = null!;
    internal IMongoCollection<BsonDocument> Rows(string name) => Data.Rows(name);
    internal ImmutableArray<PaymentReviewField> Fields => PaymentCorrectionTests.Changed(Review, "4000.00");
    internal PaymentSupplementReviewObservation Observation(int page = 1) => new("amount", Pdf.Evidence.Supplement.SupplementId, 2,
        new(PdfResult["_id"].AsInt64, PdfResult["resultHash"].AsString, Pdf.Reference, page, "amount"), true);
    internal static AuthorizationCommandMetadata Meta(string key) => new("supplement-correction." + key,
        "Nilai dimasukkan petugas secara manual; rujukan PDF diperiksa terpisah", "synthetic-supplement-correction");

    internal static async Task<PaymentSupplementCorrectionTestData> CreateAsync(MongoFixture fixture)
    {
        var result = new PaymentSupplementCorrectionTestData();
        try {
            var image = await ImageAsync();
            result.Pdf = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture, primaryImageBytes: image, maximumValidationAgeMs: 300000);
            var data = result.Data;
            // Finite upload/decoder/read/proof-age policies were created before admission.
            await ApplyAsync(data.Database, data.Catalog.Clock);
            var imageWorker = PaymentOcrProcessorTests.Processor(data, new WindowsLocalOcrEngine(LocalOcrTests.Options()));
            await PaymentOcrProcessorTests.Policy(data, imageWorker);
            Assert.Equal("completed", await imageWorker.RunOnceAsync());
            result.ImageResult = await result.Rows("documentExtractionResults").Find(new BsonDocument()).SingleAsync();
            Assert.Equal("completed", await result.Pdf.Runtime.RunOnceAsync());
            var pdfJob = await result.Pdf.JobAsync();
            result.PdfResult = await result.Pdf.Unit.ExecuteAsync((tx, ct) =>
                new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, pdfJob, ct))
                ?? throw new InvalidOperationException("Expected persisted actual whole-PDF result");
            result.Reviewer = await PaymentCorrectionTests.Corrector(data, 98551);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Catalog.Clock, result.Reviewer.UserId);
            var imageView = await data.Service.ReadReviewExtractionAsync(result.Reviewer, data.DeclarationId, 1, data.Reference,
                new("payment-ocr-view", "actual-text-primary-supplement-correction"));
            Assert.Equal("succeeded", imageView.State); Assert.NotNull(imageView.Extraction);
            Assert.Equal("1234567.89", Assert.Single(imageView.Extraction.Fields, field => field.Field == "amount").Value);
            Assert.Equal("DEMO-20260930-1", Assert.Single(imageView.Extraction.Fields, field => field.Field == "reference").Value);
            Assert.Equal(WindowsLocalOcrEngine.Engine, imageView.Extraction.Provenance.EngineVersion);
            result.Review = await data.Service.ReadCorrectionsAsync(result.Reviewer, data.DeclarationId,
                new(1, imageView.SubmissionId, data.Reference, imageView.ResultId!.Value), new("payment-ocr-view", "actual-image-correction-view"));
            var pages = PdfDocumentExtractionRecords.Extract(result.PdfResult);
            Assert.Equal(2, pages.Pages.Count); Assert.Equal("PaymentSupplement", result.PdfResult["sourceKind"]);
            Assert.Equal("2345678.90", Assert.Single(pages.Pages[1].Fields, field => field.Field == "amount").Value);
            result.Before = await result.SnapshotAsync(); return result;
        } catch { await result.DisposeAsync(); throw; }
    }

    internal static async Task<byte[]> ImageAsync()
    {
        var root = Environment.GetEnvironmentVariable("UNITTOKO_OCR_FIXTURES") ?? throw new InvalidOperationException("Configure actual native OCR fixtures");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "payment.png"));
        Assert.Equal(48373, bytes.Length);
        Assert.Equal("e0ef5971246af2aad9e967eb5a994230cdb41a9eb90ef501623230b95e4bd083", PdfRasterProtocol.Digest(bytes));
        return bytes;
    }
    internal static async Task ApplyAsync(IMongoDatabase database, UnitToko.Platform.IClock clock)
    {
        var lease = await new MigrationGate(database, clock, 88).AcquireAsync(88, default); if (lease is null) return;
        try { await new PaymentSupplementCorrectionMigration(database).ApplyAsync(lease, default); await lease.CompleteAsync(default); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    internal PaymentEvidenceService Restart()
    {
        var declarations = new PaymentDraftService(Data.Database, new(Data.Catalog.Client), new(Data.Database), Data.Catalog.Clock, Data.Payment.Invoice.Service);
        return new(Data.Database, new(Data.Catalog.Client), new(Data.Database), Data.Catalog.Clock, declarations, Data.Files.Registration);
    }
    internal async Task<BsonDocument> SnapshotAsync()
    {
        var snapshot = await Pdf.Evidence.FinancialAsync();
        foreach (var name in new[] { "documentExtractionAudits", "evidencePins", "evidencePdfValidationResults", "evidencePdfValidationAudits", PdfDocumentExtractionRecords.Results })
            snapshot[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return snapshot;
    }
    internal async Task UnchangedAsync()
    {
        var after = await SnapshotAsync();
        foreach (var field in Before.Names) Assert.True(Before[field].Equals(after[field]), "Immutable image/PDF or financial collection changed: " + field);
    }
    public async ValueTask DisposeAsync() { if (Pdf is not null) await Pdf.DisposeAsync(); }
}
