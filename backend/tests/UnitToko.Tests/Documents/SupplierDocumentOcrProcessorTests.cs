using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using D = UnitToko.Tests.Procurement.SupplierDocumentTestData;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class SupplierDocumentOcrProcessorTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_supplier_original_recovers_one_result_and_missing_result_audit_or_receipt_never_reprocesses_or_exposes_suggestions() {
        await using var data = await SubmittedAsync(fixture);
        var owner = await OwnerStateAsync(data); var engine = new FixtureEngine(); var processor = Processor(data, engine);
        Assert.Equal("unavailable", await processor.RunOnceAsync()); Assert.Equal(0, engine.Calls);
        Assert.Equal("queued", (await ReadAsync(data)).State);
        await PolicyAsync(data, processor);
        var queue = new OutboxStore(data.Database, data.Clock); var claim = await queue.ClaimAsync(Consumer, SupplierDocumentOcrProcessor.ClaimPolicy); Assert.NotNull(claim);
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(claim)); Assert.Equal(data.Bytes, engine.Bytes);
        var result = await data.Rows(Results).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.DocumentId, result["binding"]["submissionId"].AsInt64); Assert.Equal(data.DraftId, result["binding"]["draftId"].AsInt64);
        Assert.Equal("SupplierInvoice", result["purpose"]); Assert.Equal("human_line_matching_required", result["output"]["qualityFlags"][0]);
        var view = await ReadAsync(data); Assert.Equal("succeeded", view.State); Assert.Equal(result["_id"].AsInt64, view.ResultId);
        Assert.Equal("SYNTH-INVOICE-001", Assert.Single(view.Fields, value => value.Field == "document_reference").Value);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.ReadExtractionAsync(data.Reader, data.DraftId, 1, data.Reference, ReadMetadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.ReadExtractionAsync(data.Outsider, data.DraftId, 1, data.Reference, ReadMetadata));
        engine.Before = () => throw new InvalidOperationException("A committed supplier extraction must not invoke OCR again");
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(claim)); Assert.Equal(1, engine.Calls);
        foreach (var table in new[] { Results, Audits }) {
            var row = await data.Rows(table).Find(new BsonDocument()).SingleAsync();
            Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try { await BrokenAsync(() => ReadAsync(data)); await BrokenAsync(() => processor.ProcessClaimAsync(claim)); }
            finally { await data.Rows(table).InsertOneAsync(row); }
            Assert.Equal(1, engine.Calls);
        }
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("_id", claim["_id"])).SingleAsync();
        Assert.True(job["processingReceipt"]["supplierDocumentExtraction"].AsBoolean);
        Assert.Equal(1, (await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]),
            new BsonDocument("$unset", new BsonDocument("processingReceipt", "")))).ModifiedCount);
        try { await BrokenAsync(() => ReadAsync(data)); await BrokenAsync(() => processor.ProcessClaimAsync(claim)); }
        finally { await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), job); }
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(31);
        var recovered = await queue.ClaimAsync(Consumer, SupplierDocumentOcrProcessor.ClaimPolicy); Assert.NotNull(recovered);
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(recovered)); Assert.Equal(1, engine.Calls);
        Assert.False(await queue.CompleteAsync(claim)); Assert.True(await queue.CompleteAsync(recovered)); Assert.Equal("idle", await processor.RunOnceAsync());
        foreach (var table in new[] { Results, Audits, Reads }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(owner, await OwnerStateAsync(data)); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Withdrawal_or_same_revision_policy_change_after_private_bytes_rolls_back_output_and_releases_original() {
        await using var data = await SubmittedAsync(fixture); var owner = await OwnerStateAsync(data);
        var engine = new FixtureEngine(); var processor = Processor(data, engine); await PolicyAsync(data, processor);
        var queue = new OutboxStore(data.Database, data.Clock); var claim = await queue.ClaimAsync(Consumer, SupplierDocumentOcrProcessor.ClaimPolicy); Assert.NotNull(claim);
        foreach (var (field, changed, original) in new[] {
            ("active", (BsonValue)false, (BsonValue)true),
            ("modelSelectionRef", (BsonValue)"synthetic-changed-review-without-revision", (BsonValue)ModelReference) }) {
            engine.Before = async () => {
                Assert.Equal(data.Bytes, engine.Bytes);
                Assert.Equal(1, (await data.Rows(Policies).UpdateOneAsync(new BsonDocument("consumerId", Consumer), new BsonDocument("$set", new BsonDocument(field, changed)))).ModifiedCount);
            };
            try {
                var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(claim));
                Assert.Equal("document_processing_policy_unavailable", error.Code);
                await NoOutputAsync(data); Assert.Equal(owner, await OwnerStateAsync(data));
            } finally { await data.Rows(Policies).UpdateOneAsync(new BsonDocument("consumerId", Consumer), new BsonDocument("$set", new BsonDocument(field, original))); }
        }
        var beforeExpiry = data.Clock.UtcNow;
        var readPolicy = await data.Rows("evidenceReadPolicies").Find(new BsonDocument("storeId", data.Registration.StoreId)).SingleAsync();
        engine.Before = () => {
            Assert.Equal(data.Bytes, engine.Bytes);
            data.Clock.UtcNow = beforeExpiry.AddMilliseconds(readPolicy["leaseDurationMs"].AsInt64 + 1);
            return Task.CompletedTask;
        };
        try {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(claim));
            Assert.Equal("document_processing_lease_lost", error.Code);
            await NoOutputAsync(data); Assert.Equal(owner, await OwnerStateAsync(data));
        } finally { data.Clock.UtcNow = beforeExpiry; }
        engine.Before = null;
        // A successful later admission proves that all rejected exclusive original handles were disposed.
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(claim)); Assert.Equal(4, engine.Calls);
        Assert.True(await queue.CompleteAsync(claim)); Assert.Equal(owner, await OwnerStateAsync(data)); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Late_result_audit_failure_safe_retry_and_replaced_claim_never_publish_partial_or_late_supplier_output() {
        await using var data = await SubmittedAsync(fixture); var owner = await OwnerStateAsync(data);
        var engine = new FixtureEngine(); var processor = Processor(data, engine); await PolicyAsync(data, processor);
        var queue = new OutboxStore(data.Database, data.Clock); var claim = await queue.ClaimAsync(Consumer, SupplierDocumentOcrProcessor.ClaimPolicy); Assert.NotNull(claim);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", Audits) })).SingleAsync();
        var validator = definition["options"]["validator"];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Audits },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("resultId", new BsonDocument("$lt", 0L)) }) } });
        try { await Assert.ThrowsAnyAsync<MongoException>(() => processor.ProcessClaimAsync(claim)); await NoOutputAsync(data); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Audits }, { "validator", validator } }); }
        engine.Failure = "ocr_unavailable";
        Assert.Equal("ocr_unavailable", await processor.ProcessClaimAsync(claim));
        var failed = await data.Rows(Failures).Find(new BsonDocument()).SingleAsync(); Assert.Equal("ocr_unavailable", failed["safeFailure"]); Assert.False(failed.Contains("output"));
        Assert.True(await queue.FailAsync(claim, "ocr_unavailable", true, TimeSpan.FromMinutes(1)));
        var pending = await ReadAsync(data); Assert.Equal("retry", pending.State); Assert.Equal("ocr_unavailable", pending.FailureCode); Assert.Empty(pending.Lines); Assert.Empty(pending.Fields);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1); engine.Failure = null;
        engine.Before = async () => {
            data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(31);
            Assert.NotNull(await queue.ClaimAsync(Consumer, SupplierDocumentOcrProcessor.ClaimPolicy));
        };
        var replaced = await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.RunOnceAsync()); Assert.Equal("evidence_validation_claim_lost", replaced.Code);
        await NoOutputAsync(data); engine.Before = null; data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(31);
        Assert.Equal("completed", await processor.RunOnceAsync()); Assert.Equal(4, engine.Calls);
        Assert.Equal(1, await data.Rows(Results).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(Audits).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(Failures).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(owner, await OwnerStateAsync(data)); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [LocalOcrFact]
    public async Task Actual_native_supplier_worker_processes_the_genuine_pinned_invoice_and_scoped_derivative_without_automatic_matching() {
        await using var data = await D.CreateAsync(fixture);
        await SupplierDocumentOcrTestData.ConfigureImageAsync(data, "supplier-invoice.png");
        await SubmitAsync(data); var owner = await OwnerStateAsync(data);
        var options = LocalOcrTests.Options(); var processor = Processor(data, new WindowsLocalOcrEngine(options, ExtractionPurpose.SupplierInvoice));
        var runtime = SupplierDocumentOcrTestData.CreateRuntime(data);
        Assert.Equal(processor.PipelineId, runtime.PipelineId); Assert.Equal("unavailable", await runtime.RunOnceAsync());
        await PolicyAsync(data, processor); Assert.Equal("completed", await runtime.RunOnceAsync());
        var read = await ReadAsync(data); Assert.Equal("succeeded", read.State); Assert.Equal(data.Reference, read.Evidence);
        Assert.Equal("SYNTH-INVOICE-001", Assert.Single(read.Fields, field => field.Field == "document_reference").Value);
        Assert.Equal("36000.00", Assert.Single(read.Fields, field => field.Field == "total_amount").Value);
        Assert.Equal("SYNTH-PO-001", Assert.Single(read.Fields, field => field.Field == "purchase_order_reference").Value);
        Assert.Equal("SYNTH-SJ-001", Assert.Single(read.Fields, field => field.Field == "delivery_reference").Value);
        Assert.Equal("PROC-PENCIL-12", Assert.Single(read.Fields, field => field.Field == "source_sku").Value);
        Assert.Equal(WindowsLocalOcrEngine.Engine, read.Provenance!.EngineVersion); Assert.Contains("human_line_matching_required", read.QualityFlags);
        Assert.Equal(owner, await OwnerStateAsync(data)); Assert.False(await data.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument()).AnyAsync());
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    private const string Consumer = SupplierDocumentOcrTestData.Consumer, ModelReference = SupplierDocumentOcrTestData.ModelReference;
    private const string Policies = "supplierDocumentProcessingPolicies", Results = "supplierDocumentExtractionResults", Audits = "supplierDocumentExtractionAudits",
        Reads = "supplierDocumentProcessingReadAudits", Failures = "supplierDocumentProcessingFailures";
    private static AuthorizationReadMetadata ReadMetadata => new("supplier-document-ocr-view", "synthetic-supplier-ocr-review");
    private static Task<SupplierDocumentExtractionView> ReadAsync(D data) => data.Evidence.ReadExtractionAsync(data.EvidenceReader, data.DraftId, 1, data.Reference, ReadMetadata);
    private static async Task<D> SubmittedAsync(MongoFixture fixture) {
        var data = await D.CreateAsync(fixture); try { await SubmitAsync(data); return data; } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task SubmitAsync(D data) { await data.UploadAsync(); await data.ValidateAsync(); await data.SubmitAsync(await data.PreviewSubmitAsync()); }
    private static SupplierDocumentOcrProcessor Processor(D data, ILocalOcrEngine engine) => new(data.Database, data.Unit, new(data.Database), data.Clock, data.Registration, Consumer, engine);
    private static Task PolicyAsync(D data, SupplierDocumentOcrProcessor processor) => SupplierDocumentOcrTestData.ApproveAsync(data, processor.PipelineId);
    private static async Task BrokenAsync(Func<Task> action) {
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(action); Assert.Equal("supplier_document_processing_history_invalid", error.Code);
    }
    private static async Task NoOutputAsync(D data) {
        Assert.False(await data.Rows(Results).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(Audits).Find(new BsonDocument()).AnyAsync());
        Assert.False((await data.Rows("outboxEvents").Find(new BsonDocument("kind", SupplierDocumentExtractionRecords.Kind)).SingleAsync()).Contains("processingReceipt"));
    }
    private static async Task<BsonDocument> OwnerStateAsync(D data) {
        var result = new BsonDocument();
        foreach (var table in new[] { SupplierDocumentBson.Drafts, SupplierDocumentBson.Submissions, SupplierDocumentBson.Matches, SupplierDocumentBson.Identities,
            SupplierDocumentBson.Heads, SupplierDocumentBson.InspectionHeads, SupplierDocumentBson.OrderHeads }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence");
            result[table] = new BsonArray(rows);
        }
        result["inspection"] = await data.Inspection.StateAsync(); result["purchaseOrder"] = await data.Inspection.Receiving.Orders.StateAsync(); return result;
    }
    private sealed class FixtureEngine : ILocalOcrEngine {
        public ExtractionProvenance Provenance { get; } = new("synthetic-test-engine", "synthetic-test-model", new string('c', 64), new string('d', 64), "synthetic-test/" + SupplierDocumentFieldExtractor.Version);
        internal Func<Task>? Before; internal string? Failure; internal int Calls; internal byte[] Bytes = [];
        public async Task<ExtractionResult> ExtractAsync(string jobId, EvidenceRef reference, ExtractionPurpose purpose, Stream original, CancellationToken ct) {
            Calls++; Assert.Equal(ExtractionPurpose.SupplierInvoice, purpose); using var copy = new MemoryStream(); await original.CopyToAsync(copy, ct); Bytes = copy.ToArray(); Assert.NotEmpty(Bytes);
            if (Before is not null) await Before();
            return Failure is not null ? new(jobId, reference, purpose, Provenance, "failed", Failure, []) :
                new(jobId, reference, purpose, Provenance, "succeeded", null, [new("Nomor invoice: SYNTH-INVOICE-001", .98m, new(.1m, .1m, .8m, .1m)), new("Total tagihan: Rp 36.000,00", .98m, new(.1m, .3m, .8m, .1m))]);
        }
    }
}
