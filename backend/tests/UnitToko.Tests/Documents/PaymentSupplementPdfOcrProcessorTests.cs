using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementPdfOcrProcessorTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_image_primary_and_separate_two_page_payment_PDF_keep_distinct_jobs_scoped_reads_and_exact_retry_receipt()
    {
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture, approve: false);
        Assert.Equal("unavailable", await data.Runtime.RunOnceAsync());
        Assert.Equal(0, (await data.JobAsync())["attempts"].AsInt32);
        await data.ApproveProcessingAsync();
        var claim = await data.ClaimAsync();
        Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim));
        var job = await data.JobAsync();
        var result = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).ResultAsync(tx, job, ct));
        Assert.NotNull(result);
        Assert.Equal("PaymentSupplement", result["sourceKind"]); Assert.Equal("Payment", result["purpose"]);
        Assert.Equal(2, result["pageCount"]); Assert.Equal(2L, result["binding"]["supplementRevision"]);
        Assert.Equal(data.Evidence.Supplement.SupplementId, result["binding"]["supplementId"]);
        Assert.True(PaymentSupplementBson.Primary(data.Evidence.Primary).Equals(result["binding"]["primary"]), "Exact primary-image tuple changed");
        Assert.Equal(data.Reference.Sha256, result["binding"]["reference"]["sha256"]);
        Assert.Equal(data.Data.Actor.UserId, result["binding"]["uploaderId"]);
        Assert.Equal(PaymentSupplementBson.JobKey(data.Evidence.Supplement.SupplementId), job["sourceKey"]);
        var attachment = await data.Rows(PaymentSupplementBson.Attachments).Find(new BsonDocument()).SingleAsync();
        Assert.True(PaymentSupplementBson.JobPayload(attachment).Equals(job["payload"]), "Distinct whole-PDF job payload changed");
        Assert.Equal(result["resultHash"], job["processingReceipt"]["resultHash"]);
        Assert.True(PdfDocumentExtractionRecords.Receipt(result).Equals(job["processingReceipt"]), "Exact whole-PDF receipt changed");
        var output = PdfDocumentExtractionRecords.Extract(result);
        Assert.Equal(ExtractionPurpose.Payment, output.Purpose); Assert.Equal(data.Reference, output.Evidence); Assert.True(output.RequiresHumanReview);
        Assert.Equal(new[] { 0, 90 }, output.Pages.Select(page => page.Raster.RotationDegrees));
        var amount = Assert.Single(output.Fields, field => field.Field == "amount");
        Assert.Equal(new[] { "1234567.89", "2345678.90" }, amount.Observations.Select(value => value.Candidate.Value));
        Assert.Equal(new[] { 0, 1 }, amount.Observations.Select(value => value.PageIndex));
        Assert.Contains("contradictory_pages", amount.ReviewReasons);
        Assert.All(output.Pages, page => {
            Assert.NotEmpty(page.Lines); Assert.Equal(output.OcrProvenance, page.OcrProvenance);
            Assert.NotEqual(data.Reference.Sha256, page.Raster.Sha256);
        });
        data.Runtime.Processor.Observer = new(BeforeOriginal: () => throw new InvalidOperationException("Immutable PDF recovery must not reread or recompute"));
        Assert.Equal("succeeded", await data.Runtime.Processor.ProcessClaimAsync(claim));
        Assert.True(await data.Queue.CompleteAsync(claim)); Assert.Equal("idle", await data.Runtime.RunOnceAsync());
        foreach (var name in new[] { PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits, PdfDocumentExtractionRecords.ProcessingReads })
            Assert.Equal(1, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));

        var restarted = data.Evidence.Restart(); var primary = data.Evidence.Primary; var supplementId = data.Evidence.Supplement.SupplementId;
        var requester = await restarted.ReadPdfExtractionAsync(data.Data.Actor, primary.DeclarationId, supplementId, 2, data.Reference,
            new("payment-supplement-pdf-ocr-view", "synthetic-requester-native-PDF"));
        var reviewer = await PaymentReviewTests.Reviewer(data.Data);
        Assert.NotEqual(data.Data.Actor.UserId, reviewer.UserId);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Data.Catalog.Clock, reviewer.UserId);
        var staff = await restarted.ReadReviewPdfExtractionAsync(reviewer, primary.DeclarationId, supplementId, 2, data.Reference,
            new("payment-review-supplement-pdf-ocr-view", "synthetic-reviewer-native-PDF"));
        foreach (var view in new[] { requester, staff }) {
            Assert.Equal(primary.DeclarationId, view.DeclarationId); Assert.Equal(primary.DeclarationRevision, view.DeclarationRevision);
            Assert.Equal(primary.SubmissionId, view.SubmissionId); Assert.Equal(primary.PrimaryEvidence, view.PrimaryEvidence);
            Assert.Equal(supplementId, view.SupplementId); Assert.Equal(2L, view.SupplementRevision); Assert.Equal(data.Reference, view.PdfEvidence);
            Assert.Equal("PaymentSupplement", view.Ocr.SourceKind); Assert.Equal("succeeded", view.Ocr.State);
            Assert.Equal(supplementId, view.Ocr.SourceId); Assert.Equal(2L, view.Ocr.SourceRevision); Assert.Equal(supplementId, view.Ocr.SubmissionId);
            Assert.Equal(result["_id"].AsInt64, view.Ocr.ResultId); Assert.Equal(result["resultHash"].AsString, view.Ocr.ResultHash);
            Assert.Equal(2, view.Ocr.Extraction!.Pages.Count);
        }
        var reads = await data.Rows(PdfDocumentExtractionRecords.Reads).Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, reads.Count); Assert.Contains(reads, row => row["actorId"] == reviewer.UserId);
        Assert.Contains(reads, row => row["actorId"] == data.Data.Actor.UserId);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", reviewer.UserId),
            Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => restarted.ReadReviewPdfExtractionAsync(reviewer,
            primary.DeclarationId, supplementId, 2, data.Reference, new("payment-review-supplement-pdf-ocr-view", "synthetic-revoked-reader")));
        Assert.Equal(2, await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument()));
        await data.UnchangedPrimaryAndMoneyAsync();
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.PaymentJobKind)).AnyAsync());
    }

    [WindowsStorageFact]
    public async Task Wrong_namespaces_payloads_primary_image_and_current_read_authority_are_refused_without_a_PDF_read_or_result()
    {
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture);
        var claim = await data.ClaimAsync(); var before = await data.JobAsync();
        foreach (var kind in new[] { PdfDocumentExtractionContract.PaymentJobKind, "payment.ocr.requested", PdfDocumentExtractionContract.SupplierJobKind }) {
            var wrong = claim.DeepClone().AsBsonDocument; wrong["kind"] = kind;
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Runtime.Processor.ProcessClaimAsync(wrong));
            Assert.Equal("pdf_document_processing_policy_unavailable", error.Code);
        }
        foreach (var field in new[] { "primary", "evidence", "supplementRevision" }) {
            var wrong = claim.DeepClone().AsBsonDocument;
            if (field == "primary") wrong["payload"]["primary"]["submissionId"] = data.Evidence.Primary.SubmissionId + 1000;
            else if (field == "evidence") wrong["payload"]["evidence"] = PaymentSubmissionBson.Reference(data.Evidence.Primary.PrimaryEvidence);
            else wrong["payload"]["supplementRevision"] = 1L;
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Runtime.Processor.ProcessClaimAsync(wrong));
            Assert.Equal("evidence_validation_claim_lost", error.Code);
        }
        Assert.True(before.Equals(await data.JobAsync()), "Denied forged claim changed the stored queue row");
        Assert.Equal(0, await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).CountDocumentsAsync(new BsonDocument()));
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, claim);
        Assert.Throws<NotSupportedException>(() => PdfDocumentOcrRuntime.CreatePayment(null!, null!, null!, null!, null!));

        var service = data.Evidence.Restart(); var primary = data.Evidence.Primary; var supplementId = data.Evidence.Supplement.SupplementId;
        var requesterMetadata = new AuthorizationReadMetadata("payment-supplement-pdf-ocr-view", "synthetic-denied-PDF-read");
        var reviewerMetadata = new AuthorizationReadMetadata("payment-review-supplement-pdf-ocr-view", "synthetic-denied-PDF-review");
        var foreign = await data.Data.Catalog.ActorAsync(99881, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReviewPdfExtractionAsync(foreign, primary.DeclarationId, supplementId, 2, data.Reference, reviewerMetadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReviewPdfExtractionAsync(data.Data.Actor, primary.DeclarationId, supplementId, 2, data.Reference, reviewerMetadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ReadPdfExtractionAsync(data.Data.Actor, primary.DeclarationId, supplementId, 1, data.Reference, requesterMetadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ReadPdfExtractionAsync(data.Data.Actor, primary.DeclarationId, supplementId, 2, primary.PrimaryEvidence, requesterMetadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadPdfExtractionAsync(data.Data.Actor, primary.DeclarationId + 1000, supplementId, 2, data.Reference, requesterMetadata));
        Assert.Equal(0, await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument()));
        await data.UnchangedPrimaryAndMoneyAsync();
    }

    [WindowsStorageTheory]
    [InlineData(PaymentSupplementBson.Policies)]
    [InlineData(PdfDocumentExtractionRecords.Policies)]
    [InlineData("evidenceReadPolicies")]
    [InlineData(PdfEvidenceValidationRecords.Policies)]
    public async Task Revoked_live_decision_or_native_policy_after_original_acquisition_cannot_publish_supplemental_output(string policy)
    {
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture); var acquired = false;
        data.Runtime.Processor.Observer = new(AfterOriginal: async () => {
            acquired = true;
            await data.Rows(policy).UpdateManyAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false)));
        });
        Assert.Equal("retry", await data.Runtime.RunOnceAsync()); Assert.True(acquired);
        var job = await data.JobAsync(); Assert.Equal("ocr_unavailable", job["lastFailureCode"]);
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, job);
        Assert.Equal(1, await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).CountDocumentsAsync(new BsonDocument()));
        await data.UnchangedPrimaryAndMoneyAsync();
    }

    [WindowsStorageFact]
    public async Task D06_revocation_after_real_all_page_computation_rolls_back_result_audit_receipt_and_primary_image()
    {
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture); var computed = false;
        data.Runtime.Processor.Observer = new(AfterComputation: async () => {
            computed = true;
            await data.Rows(PaymentSupplementBson.Policies).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false)));
        });
        Assert.Equal("retry", await data.Runtime.RunOnceAsync()); Assert.True(computed);
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, await data.JobAsync());
        await data.UnchangedPrimaryAndMoneyAsync();
    }

    [WindowsStorageFact]
    public async Task Captured_D06_deadline_is_finite_and_same_claim_cannot_restart_it_after_current_decision_renewal()
    {
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture);
        var approvedUntil = data.Data.Catalog.Clock.UtcNow.AddSeconds(20);
        await data.Rows(PaymentSupplementBson.Policies).UpdateOneAsync(new BsonDocument(),
            Builders<BsonDocument>.Update.Set("expiresAt", approvedUntil.UtcDateTime).Inc("revision", 1L));
        var claim = await data.ClaimAsync(); BsonDocument? captured = null;
        data.Runtime.Processor.Observer = new(AfterOriginal: async () => {
            captured = await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).Find(new BsonDocument()).SingleAsync();
            Assert.True(captured["until"].AsBsonDateTime.MillisecondsSinceEpoch <= approvedUntil.ToUnixTimeMilliseconds(), "Read exceeded captured D06 validity");
            Assert.InRange(captured["until"].AsBsonDateTime.MillisecondsSinceEpoch - captured["createdAt"].AsBsonDateTime.MillisecondsSinceEpoch, 1L, 20000L);
            data.Data.Catalog.Clock.UseSystemClock = false;
            data.Data.Catalog.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(captured["until"].AsBsonDateTime.MillisecondsSinceEpoch + 1);
        });
        var expired = await Assert.ThrowsAsync<PdfDocumentOcrProcessor.ProcessingUnavailable>(() => data.Runtime.Processor.ProcessClaimAsync(claim));
        Assert.Equal("pdf_document_deadline", expired.Code); Assert.NotNull(captured);
        await data.Rows(PaymentSupplementBson.Policies).UpdateOneAsync(new BsonDocument(),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Data.Catalog.Clock.UtcNow.AddDays(1).UtcDateTime).Inc("revision", 1L));
        data.Runtime.Processor.Observer = new(BeforeOriginal: () => throw new InvalidOperationException("Expired original-key retry cannot restart the captured window"));
        expired = await Assert.ThrowsAsync<PdfDocumentOcrProcessor.ProcessingUnavailable>(() => data.Runtime.Processor.ProcessClaimAsync(claim));
        Assert.Equal("pdf_document_deadline", expired.Code);
        Assert.True(captured.Equals(await data.Rows(PdfDocumentExtractionRecords.ProcessingReads).Find(new BsonDocument()).SingleAsync()), "Captured private read window changed");
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, claim); await data.UnchangedPrimaryAndMoneyAsync();
    }

    [WindowsStorageFact]
    public async Task Required_supplement_PDF_result_audit_rejection_rolls_back_complete_native_output_and_receipt()
    {
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture); var computed = false;
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions {
            Filter = new BsonDocument("name", PdfDocumentExtractionRecords.Audits) })).SingleAsync();
        var original = definition["options"]["validator"].DeepClone();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PdfDocumentExtractionRecords.Audits },
            { "validator", new BsonDocument("$and", new BsonArray { original, new BsonDocument("resultId", new BsonDocument("$lt", 0L)) }) } });
        data.Runtime.Processor.Observer = new(AfterComputation: () => { computed = true; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<MongoException>(() => data.Runtime.RunOnceAsync()); Assert.True(computed);
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, await data.JobAsync());
        await data.UnchangedPrimaryAndMoneyAsync();
    }

    [WindowsStorageFact]
    public async Task Later_real_page_failure_retains_bounded_supplement_failure_without_first_page_or_primary_OCR_result()
    {
        var engine = LocalOcrTests.Options(); engine = engine with { Limits = new(engine.Limits.MaximumBytes, 32768, 1200, engine.Limits.MaximumPixels) };
        await using var data = await PaymentSupplementPdfOcrTestData.CreateAsync(fixture, engine);
        Assert.Equal("dead", await data.Runtime.RunOnceAsync());
        var job = await data.JobAsync(); Assert.Equal("ocr_invalid_media", job["lastFailureCode"]);
        var failure = await data.Rows(PdfDocumentExtractionRecords.Failures).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("PaymentSupplement", failure["sourceKind"]); Assert.Equal("Payment", failure["purpose"]);
        Assert.Equal("pdf_document_page_limit", failure["safeFailure"]); Assert.Equal(1, failure["failurePageIndex"]);
        Assert.False(failure.Contains("output")); Assert.False(failure.Contains("pages"));
        Assert.Equal(PdfDocumentExtractionRecords.Hash(failure), failure["recordHash"]);
        await PdfDocumentOcrProcessingTestData.NoOutputAsync(data.Database, job, noFailure: false);
        var history = await data.Unit.ExecuteAsync((tx, ct) => new PdfDocumentExtractionRecords(data.Database).HistoricalFailureAsync(tx, failure["_id"].AsInt64, ct));
        Assert.True(failure.Equals(history.Failure), "Bounded whole-PDF failure history changed");
        await data.UnchangedPrimaryAndMoneyAsync();
    }
}
