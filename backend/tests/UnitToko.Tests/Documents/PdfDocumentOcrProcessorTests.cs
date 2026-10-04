using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfDocumentOcrProcessorTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_two_page_supplier_preserves_all_candidates_exact_private_owner_and_retry_receipt_without_money() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture, approve: false);
        var before = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database); var queued = await data.JobAsync();
        Assert.Equal("pending", queued["status"]); Assert.Equal("unavailable", await data.Runtime.RunOnceAsync());
        Assert.Equal(0, (await data.JobAsync())["attempts"].AsInt32);
        await PdfDocumentOcrProcessingTestData.PolicyAsync(data.Database, data.Data.Clock, ProcurementInspectionTestData.StoreId, data.Runtime);
        var claim = await data.ClaimAsync(); Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim));
        var job = await data.JobAsync(); var result = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, job, ct));
        Assert.NotNull(result); Assert.Equal(2, result["pageCount"]); Assert.Equal("SupplierInvoice", result["sourceKind"]);
        Assert.Equal(data.Reference.Sha256, result["binding"]["reference"]["sha256"]); Assert.Equal(data.Data.Actor.UserId, result["binding"]["uploaderId"]);
        Assert.Equal(result["resultHash"], job["processingReceipt"]["resultHash"]); Assert.True(job["processingReceipt"]["pdfDocumentExtraction"].AsBoolean);
        var view = PdfDocumentExtractionRecords.Extract(result); Assert.Equal(data.Reference, view.Evidence); Assert.True(view.RequiresHumanReview);
        Assert.Equal(new[] { 0, 90 }, view.Pages.Select(page => page.Raster.RotationDegrees));
        var amount = Assert.Single(view.Fields, field => field.Field == "total_amount");
        Assert.Equal(new[] { "1234567.89", "2345678.90" }, amount.Observations.Select(observation => observation.Candidate.Value));
        Assert.Contains("contradictory_pages", amount.ReviewReasons);
        Assert.All(view.Pages, page => { Assert.NotEmpty(page.Lines); Assert.Equal(view.OcrProvenance, page.OcrProvenance); Assert.NotEqual(data.Reference.Sha256, page.Raster.Sha256); });
        data.Runtime.Processor.Observer = new(BeforeOriginal: () => throw new InvalidOperationException("Committed result must recover without reading or computing the original again"));
        Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim)); Assert.True(await data.Queue.CompleteAsync(claim));
        Assert.Equal("idle", await data.Runtime.RunOnceAsync());
        foreach (var name in new[] { PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.ProcessingReads })
            Assert.Equal(1, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
        Assert.Equal(0, await data.Rows("supplierDocumentMatches").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Supplier_worker_refuses_payment_and_image_namespace_claims_without_output_or_source_changes() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture);
        var claim = await data.ClaimAsync(); var before = await data.JobAsync();
        foreach (var kind in new[] { PdfDocumentExtractionContract.PaymentJobKind, "payment.ocr.requested", SupplierDocumentExtractionRecords.Kind }) {
            var wrong = claim.DeepClone().AsBsonDocument; wrong["kind"] = kind;
            await Assert.ThrowsAnyAsync<Exception>(() => data.Runtime.Processor.ProcessClaimAsync(wrong));
            Assert.Equal(before, await data.JobAsync());
            await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, claim);
        }
        Assert.Equal(0, await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", PdfDocumentExtractionContract.PaymentJobKind)));
        Assert.Equal(0, await data.Rows("supplierDocumentExtractionResults").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageTheory]
    [InlineData(PdfDocumentExtractionRecords.Policies)]
    [InlineData("evidenceReadPolicies")]
    [InlineData(PdfEvidenceValidationRecords.Policies)]
    public async Task Revocation_after_actual_native_original_acquisition_cannot_publish_any_result(string revokedPolicy) {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var before = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        var acquired = false; data.Runtime.Processor.Observer = new(AfterOriginal: async () => {
            acquired = true; await data.Rows(revokedPolicy).UpdateManyAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false)));
        });
        Assert.Equal("retry", await data.Runtime.RunOnceAsync()); Assert.True(acquired); var job = await data.JobAsync();
        Assert.Equal("ocr_unavailable", job["lastFailureCode"]); await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, job);
        Assert.Equal(1, await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
    }

    [WindowsStorageFact]
    public async Task Policy_revoked_after_real_whole_document_computation_rolls_back_result_audit_and_receipt() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var computed = false;
        data.Runtime.Processor.Observer = new(AfterComputation: async () => {
            computed = true; await data.Rows(PdfDocumentExtractionRecords.Policies).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false)));
        });
        Assert.Equal("retry", await data.Runtime.RunOnceAsync()); Assert.True(computed);
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, await data.JobAsync());
    }

    [WindowsStorageFact]
    public async Task Expired_captured_window_cannot_be_restarted_by_the_same_claim_and_preserves_only_immutable_read_attempt() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var claim = await data.ClaimAsync(); BsonDocument? captured = null;
        data.Runtime.Processor.Observer = new(AfterOriginal: async () => {
            captured = await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).Find(new BsonDocument()).SingleAsync();
            Assert.InRange(captured["until"].AsBsonDateTime.MillisecondsSinceEpoch - captured["createdAt"].AsBsonDateTime.MillisecondsSinceEpoch, 50000L, 60000L);
            data.Data.Clock.UseSystemClock = false;
            data.Data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(captured["until"].AsBsonDateTime.MillisecondsSinceEpoch + 1);
        });
        await Assert.ThrowsAnyAsync<Exception>(() => data.Runtime.Processor.ProcessClaimAsync(claim)); Assert.NotNull(captured);
        data.Runtime.Processor.Observer = null;
        await Assert.ThrowsAnyAsync<Exception>(() => data.Runtime.Processor.ProcessClaimAsync(claim));
        Assert.Equal(captured, await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).Find(new BsonDocument()).SingleAsync());
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, claim);
    }

    [WindowsStorageFact]
    public async Task Current_validation_age_expiry_inside_live_claim_refuses_a_still_locked_original() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var acquired = false;
        data.Runtime.Processor.Observer = new(AfterOriginal: async () => {
            acquired = true; var proof = await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument()).SingleAsync();
            await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("maximumValidationAgeMs", 1000L)));
            data.Data.Clock.UseSystemClock = false; data.Data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(proof["checkedAt"].AsBsonDateTime.MillisecondsSinceEpoch + 2000);
        });
        Assert.Equal("retry", await data.Runtime.RunOnceAsync()); Assert.True(acquired); await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, await data.JobAsync());
    }

    [WindowsStorageFact]
    public async Task Failure_on_later_real_page_keeps_bounded_failure_and_never_publishes_first_page_or_image_result() {
        var options = LocalOcrTests.Options(); options = options with { Limits = new(options.Limits.MaximumBytes, 32768, 1200, options.Limits.MaximumPixels) };
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture, options);
        Assert.Equal("dead", await data.Runtime.RunOnceAsync()); var job = await data.JobAsync(); Assert.Equal("ocr_invalid_media", job["lastFailureCode"]);
        var failure = await data.Rows(PdfDocumentExtractionRecords.Failures).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("pdf_document_page_limit", failure["safeFailure"]); Assert.Equal(1, failure["failurePageIndex"]); Assert.False(failure.Contains("output")); Assert.False(failure.Contains("pages"));
        Assert.Equal(PdfDocumentExtractionRecords.Hash(failure), failure["recordHash"]);
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, job, noFailure: false);
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        var history = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).HistoricalFailureAsync(tx, failure["_id"].AsInt64, ct));
        Assert.Equal(failure, history.Failure);
    }

    [WindowsStorageFact]
    public async Task Wrong_real_runtime_pin_is_a_bounded_retry_without_suggestions_or_receipt() {
        var options = LocalOcrTests.Options(); options = options with { Files = options.Files.Select(pin => pin.Path == options.HelperPath ? pin with { Sha256 = new string('0', 64) } : pin).ToArray() };
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture, options); Assert.Equal("retry", await data.Runtime.RunOnceAsync());
        var failure = await data.Rows(PdfDocumentExtractionRecords.Failures).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("pdf_document_unavailable", failure["safeFailure"]); Assert.Equal(BsonNull.Value, failure["failurePageIndex"]);
        var job = await data.JobAsync(); Assert.Equal("ocr_unavailable", job["lastFailureCode"]); await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, job, noFailure: false);
    }

    [WindowsStorageFact]
    public async Task Required_result_audit_rejection_rolls_back_all_output_and_receipt_after_actual_computation() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var before = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", PdfDocumentExtractionRecords.Audits) })).SingleAsync();
        var validator = definition["options"]["validator"];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PdfDocumentExtractionRecords.Audits },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("resultId", new BsonDocument("$lt", 0L)) }) } });
        await Assert.ThrowsAnyAsync<MongoException>(() => data.Runtime.RunOnceAsync()); await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, await data.JobAsync());
        Assert.Equal(before, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));
    }

    [WindowsStorageFact]
    public async Task Two_overlapping_attempts_of_the_same_live_claim_commit_only_one_whole_original_result_and_receipt() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var before = await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database);
        var claim = await data.ClaimAsync();
        var firstComputed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondComputed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrivals = 0;
        data.Runtime.Processor.Observer = new(AfterComputation: async () => {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 1) firstComputed.TrySetResult(); else if (arrival == 2) secondComputed.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        });
        // Reach the persistence race after the first real computation has released
        // its source transaction. An early error is captured, never hidden by a peer's wait.
        var first = CaptureAsync(data.Runtime.Processor.ProcessClaimAsync(claim));
        Task<(string? Outcome, Exception? Error)>? second = null;
        var firstReachedBoundary = false; var secondReachedBoundary = false; Exception? rendezvousFailure = null;
        try {
            var boundary = await Task.WhenAny(firstComputed.Task, first).WaitAsync(TimeSpan.FromSeconds(30));
            firstReachedBoundary = boundary == firstComputed.Task;
            if (firstReachedBoundary) {
                second = CaptureAsync(data.Runtime.Processor.ProcessClaimAsync(claim));
                boundary = await Task.WhenAny(secondComputed.Task, second).WaitAsync(TimeSpan.FromSeconds(30));
                secondReachedBoundary = boundary == secondComputed.Task;
            }
        } catch (Exception error) { rendezvousFailure = error;
        } finally { release.TrySetResult(); }
        var outcomes = second is null ? new[] { await first } : await Task.WhenAll(first, second);
        var diagnostic = string.Join("; ", outcomes.Select(Describe));
        Assert.True(rendezvousFailure is null, "Native writer rendezvous failed: " + rendezvousFailure?.GetType().Name + "; " + diagnostic);
        Assert.True(firstReachedBoundary, "First native computation failed before the writer boundary: " + diagnostic);
        Assert.True(secondReachedBoundary, "Second native computation failed before the writer boundary: " + diagnostic);
        Assert.True(arrivals == 2, "Both real computations must reach final persistence. " + string.Join("; ", outcomes.Select(Describe)));
        Assert.Contains(outcomes, value => value.Outcome == "succeeded" && value.Error is null);
        Assert.All(outcomes, value => Assert.True(value.Outcome == "succeeded" && value.Error is null ||
            value.Outcome is null && value.Error is PersistenceConflictException { Code: "version_conflict" }, Describe(value)));
        foreach (var name in new[] { PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.ProcessingReads })
            Assert.Equal(1, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        var job = await data.JobAsync(); var result = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, job, ct));
        Assert.NotNull(result); Assert.Equal(PdfDocumentExtractionRecords.Receipt(result), job["processingReceipt"]);
        data.Runtime.Processor.Observer = new(BeforeOriginal: () => throw new InvalidOperationException("Committed race recovery must not recompute or read original bytes"));
        Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim));
        Assert.Equal(1, await data.Rows(PdfDocumentExtractionRecords.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.True(await data.Queue.CompleteAsync(claim)); Assert.Equal(before, await PdfDocumentOcrProcessingTestData.FinancialAsync(data.Database));

        static async Task<(string? Outcome, Exception? Error)> CaptureAsync(Task<string> attempt) {
            try { return (await attempt, null); } catch (Exception error) { return (null, error); }
        }
        static string Describe((string? Outcome, Exception? Error) attempt) => attempt.Error is PersistenceConflictException conflict
            ? "persistence conflict " + conflict.Code : attempt.Error is { } error ? "unexpected " + error.GetType().Name : "outcome " + attempt.Outcome;
    }

    [WindowsStorageFact]
    public async Task Missing_immutable_result_audit_cannot_be_acknowledged_as_a_successful_recovery() {
        await using var data = await SupplierPdfOcrData.CreateAsync(fixture); var claim = await data.ClaimAsync();
        Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim));
        var audit = await data.Rows(PdfDocumentExtractionRecords.Audits).Find(new BsonDocument()).SingleAsync();
        await data.Rows(PdfDocumentExtractionRecords.Audits).DeleteOneAsync(new BsonDocument("_id", audit["_id"]));
        var failed = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Runtime.Processor.ProcessClaimAsync(claim));
        Assert.Equal("pdf_document_processing_history_invalid", failed.Code);
        Assert.Equal("leased", (await data.JobAsync())["status"]);
        await data.Rows(PdfDocumentExtractionRecords.Audits).InsertOneAsync(audit);
        data.Runtime.Processor.Observer = new(BeforeOriginal: () => throw new InvalidOperationException("Restored exact audit recovers without recomputation"));
        Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim)); Assert.True(await data.Queue.CompleteAsync(claim));
        Assert.Equal(1, await data.Rows(PdfDocumentExtractionRecords.Results).CountDocumentsAsync(new BsonDocument()));
    }

}
