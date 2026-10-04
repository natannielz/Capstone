using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfEvidenceAdmissionTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Explicit_payment_pdf_is_recoverable_and_only_exact_complete_real_page_proof_can_open_private_original() {
        await using var data = await PaymentPdfData.CreateAsync(fixture);
        var bytes = PdfEvidenceAdmissionTestRuntime.Pdf(); var (reserved, reference) = await data.UploadAsync(bytes);
        var restarted = new PaymentEvidenceService(data.Database, new(data.Catalog.Client), new(data.Database), data.Clock, data.Payment.Service, data.Files.Registration);
        Assert.Equal(reserved, await restarted.RecoverPdfReservationAsync(data.Actor, data.DeclarationId, "payment-pdf"));
        Assert.Equal(reference, (await restarted.PdfStatusAsync(data.Actor, data.DeclarationId, reserved.ReservationId)).Reference);
        using var repeatBytes = new MustNotRead();
        Assert.Equal("recovery_required", (await restarted.UploadPdfAsync(data.Actor, data.DeclarationId, reserved.ReservationId, 1, repeatBytes, EvidenceAdmissionTestData.Meta("same-original-retry"))).State);
        Assert.Equal(0, repeatBytes.Reads);
        await using var runtime = await data.RuntimeAsync(approve: false);
        Assert.Equal("unavailable", await runtime.Runtime.RunOnceAsync());
        Assert.Equal("pending", (await data.JobAsync())["status"]);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync(reserved, reference));
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument()).AnyAsync());
        var queue = new OutboxStore(data.Database, data.Clock);
        Assert.Null(await queue.ClaimAsync("synthetic-image-worker", EvidenceValidationTests.ClaimPolicy));
        await runtime.ApproveAsync(data.Files.StoreId);
        Assert.Equal("completed", await runtime.Runtime.RunOnceAsync()); Assert.Equal(bytes, runtime.Scanner.Bytes);
        var proof = await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("passed", proof["status"]); Assert.Equal("application/pdf", proof["mime"]); Assert.Equal(2, proof["pageCount"].AsInt32);
        Assert.Equal(new[] { 0, 1 }, proof["pages"].AsBsonArray.Select(page => page["pageIndex"].AsInt32));
        Assert.Equal(new[] { 0, 90 }, proof["pages"].AsBsonArray.Select(page => page["rotationDegrees"].AsInt32));
        Assert.True(PdfEvidenceValidationRecords.CompletePages(proof));
        Assert.Equal(PdfEvidenceValidationRecords.Hash(proof), proof["resultHash"]);
        Assert.Equal(PdfEvidenceValidationRecords.Receipt(proof), (await data.JobAsync())["processingReceipt"]);
        Assert.False(await data.Rows("evidenceValidationResults").Find(new BsonDocument()).AnyAsync());
        Assert.Equal("decoded", (await data.Evidence.PdfStatusAsync(data.Actor, data.DeclarationId, reserved.ReservationId)).DecoderStatus);
        await using (var file = await data.OpenAsync(reserved, reference)) {
            Assert.Equal("application/pdf", file.Mime); using var copy = new MemoryStream(); await file.Content.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray());
        }
        Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
        // Immutable result, queue receipt and audit must agree; a MIME or stored-page list alone cannot grant a lease.
        var corrupted = proof.DeepClone().AsBsonDocument; corrupted["pages"][0]["sha256"] = new string('f', 64);
        await data.Rows(PdfEvidenceValidationRecords.Results).ReplaceOneAsync(new BsonDocument("_id", proof["_id"]), corrupted);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync(reserved, reference));
        await data.Rows(PdfEvidenceValidationRecords.Results).ReplaceOneAsync(new BsonDocument("_id", proof["_id"]), proof);
        var financeBefore = await data.FinanceAsync();
        var metadata = EvidenceAdmissionTestData.Meta("pdf-verified-submit");
        foreach (var attempt in Enumerable.Range(0, 2)) {
            var refused = await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.SubmitAsync(data.Actor, data.DeclarationId, 1, reserved.ReservationId, reference, metadata));
            Assert.Equal("payment_image_evidence_required", refused.Code);
            Assert.Null(await data.Evidence.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
        }
        Assert.False(await data.Rows("paymentSubmissions").Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSubmission")).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentSubmissionBson.Operation)).AnyAsync());
        foreach (var kind in new[] { "payment.ocr.requested", PdfDocumentExtractionContract.PaymentJobKind })
            Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", kind)).AnyAsync());
        var policy = await data.Rows(PdfEvidenceValidationRecords.Policies).Find(new BsonDocument()).SingleAsync();
        await data.Rows(PdfEvidenceValidationRecords.Policies).UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync(reserved, reference));
        Assert.Equal("unavailable", (await data.Evidence.PdfStatusAsync(data.Actor, data.DeclarationId, reserved.ReservationId)).DecoderStatus);
        Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
        Assert.Null(await data.Evidence.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
        Assert.Equal(financeBefore, await data.FinanceAsync());
    }
    [WindowsStorageTheory]
    [InlineData("approval")]
    [InlineData("membership")]
    public async Task PDF_read_final_fence_refuses_revocation_after_real_original_verification_and_closes_the_file(string fault) {
        await using var data = await PaymentPdfData.CreateAsync(fixture); var (reserved, reference) = await data.UploadAsync(PdfEvidenceAdmissionTestRuntime.Pdf());
        await using var runtime = await data.RuntimeAsync(); Assert.Equal("completed", await runtime.Runtime.RunOnceAsync()); var observed = 0;
        data.Evidence.ReadObserver = new(async () => {
            observed++;
            if (fault == "approval") await data.Rows(PdfEvidenceValidationRecords.Policies).UpdateOneAsync(new BsonDocument("consumerId", PdfEvidenceAdmissionTestRuntime.Consumer), Builders<BsonDocument>.Update.Set("active", false));
            else await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        });
        await Assert.ThrowsAnyAsync<Exception>(() => data.OpenAsync(reserved, reference)); Assert.Equal(1, observed);
        Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
        var version = await data.Rows("documentVersions").Find(new BsonDocument("_id", reserved.VersionRowId)).SingleAsync();
        using var unlocked = File.Open(Path.Combine(data.Files.Root, version["generation"].AsString + ".original"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(unlocked.Length > 0); Assert.Equal(0, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Concurrent_same_pdf_reservation_key_retains_one_durable_identity_and_recovers_the_original() {
        await using var data = await PaymentPdfData.CreateAsync(fixture);
        async Task<(ReservationResult? Result, PersistenceConflictException? Error)> Attempt() {
            try { return (await data.Evidence.ReservePdfAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta("pdf-concurrent-key")), null); }
            catch (PersistenceConflictException error) { return (null, error); }
        }
        var outcomes = await Task.WhenAll(Attempt(), Attempt()); Assert.Contains(outcomes, row => row.Result is not null);
        var recovered = await data.Evidence.RecoverPdfReservationAsync(data.Actor, data.DeclarationId, "pdf-concurrent-key"); Assert.NotNull(recovered);
        foreach (var result in outcomes.Where(row => row.Result is not null)) Assert.Equal(recovered, result.Result);
        foreach (var failure in outcomes.Where(row => row.Error is not null)) Assert.Equal("version_conflict", failure.Error!.Code);
        Assert.Equal(1, await data.Rows("evidenceUploadReservations").CountDocumentsAsync(new BsonDocument("purpose", "PaymentPdf")));
        Assert.Equal(1, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "evidence.payment-pdf.reserve")));
        Assert.Equal(1L, (await data.Rows("evidenceStorePolicies").Find(new BsonDocument()).SingleAsync())["chargedReservations"].AsInt64);
    }
    [WindowsStorageFact]
    public async Task Persisted_pdf_purpose_operation_and_target_prevent_cross_format_owner_and_retry_confusion_without_consuming_bytes() {
        await using var data = await PaymentPdfData.CreateAsync(fixture); const string key = "same-format-independent-key";
        var reserved = await data.Evidence.ReservePdfAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta(key));
        Assert.Equal(reserved, await data.Evidence.ReservePdfAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta(key)));
        Assert.Equal(reserved, await data.Evidence.RecoverPdfReservationAsync(data.Actor, data.DeclarationId, key));
        Assert.Null(await data.Evidence.RecoverReservationAsync(data.Actor, data.DeclarationId, key));
        var image = await data.Evidence.ReserveAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta(key)); Assert.NotEqual(image.ReservationId, reserved.ReservationId);
        Assert.Equal(image, await data.Evidence.RecoverReservationAsync(data.Actor, data.DeclarationId, key));
        Assert.Equal(reserved.ReservationId, Assert.Single(await data.Evidence.ListPdfsAsync(data.Actor, data.DeclarationId)).ReservationId);
        Assert.Equal(image.ReservationId, Assert.Single(await data.Evidence.ListAsync(data.Actor, data.DeclarationId)).ReservationId);
        using var never = new MustNotRead();
        Assert.Equal("evidence_format_mismatch", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.UploadPdfAsync(data.Actor,
            data.DeclarationId, image.ReservationId, 1, never, EvidenceAdmissionTestData.Meta("wrong-format")))).Code);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.UploadAsync(data.Actor, data.DeclarationId, reserved.ReservationId, 1, never, EvidenceAdmissionTestData.Meta("wrong-image-route")));
        var wrongDraft = await data.Payment.Save(null, 0, data.Payment.Input, "another-payment-owner");
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.UploadPdfAsync(data.Actor, wrongDraft["declarationId"].AsInt64,
            reserved.ReservationId, 1, never, EvidenceAdmissionTestData.Meta("wrong-declaration")));
        var outsider = await data.Catalog.ActorAsync(9964, CatalogAuthoringTestData.OtherOwner, UnitToko.Modules.Identity.Permissions.PaymentEvidence, UnitToko.Modules.Identity.Permissions.InvoiceRead);
        await Assert.ThrowsAnyAsync<Exception>(() => data.Evidence.UploadPdfAsync(outsider, data.DeclarationId, reserved.ReservationId, 1, never, EvidenceAdmissionTestData.Meta("wrong-branch")));
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.UploadPdfAsync(data.Actor, data.DeclarationId, reserved.ReservationId, 1, never, EvidenceAdmissionTestData.Meta("revoked")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.RecoverPdfReservationAsync(data.Actor, data.DeclarationId, key));
        Assert.Equal(0, never.Reads); Assert.Equal("fresh", (await data.Rows("evidenceUploadAttempts").Find(new BsonDocument("_id", reserved.AttemptId)).SingleAsync())["state"]);
        Assert.Equal(2, await data.Rows("evidenceUploadReservations").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageTheory]
    [InlineData("malformed")]
    [InlineData("encrypted")]
    [InlineData("infected")]
    [InlineData("page-limit")]
    [InlineData("pixel-limit")]
    [InlineData("changed-pin")]
    public async Task Genuine_private_source_refuses_bad_content_or_runtime_without_partial_page_proof_or_read_grant(string fault) {
        await using var data = await PaymentPdfData.CreateAsync(fixture);
        var bytes = fault switch { "malformed" => "%PDF-1.7\ntruncated"u8.ToArray(), "encrypted" => PdfEvidenceAdmissionTestRuntime.EncryptedPdf(), "page-limit" => PdfEvidenceAdmissionTestRuntime.Pdf(11),
            "pixel-limit" => PdfEvidenceAdmissionTestRuntime.Pdf(2, oversizePage: true), _ => PdfEvidenceAdmissionTestRuntime.Pdf() };
        var (reserved, reference) = await data.UploadAsync(bytes);
        if (fault == "encrypted") {
            var documents = new EvidenceService(data.Database, new(data.Catalog.Client), new(data.Database), data.Clock, data.Files.Registration);
            var job = await data.JobAsync();
            var source = await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, token) => documents.PdfValidationOriginalAsync(tx, job, token));
            var opened = Assert.IsType<UnitToko.Platform.Storage.NativeOpenOutcome.Opened>(await data.Files.Registration.Store!.OpenVerifiedForNativeValidationAsync(source.Publication, source.Budget, default));
            await using var handle = opened.Handle;
            var actual = await new WindowsPdfRaster(WindowsPdfRasterTests.Options()).RasterizeAsync(handle, source.Publication, DateTimeOffset.UtcNow.AddSeconds(60), default);
            Assert.Equal("failed", actual.Status); Assert.Equal("pdf_raster_encrypted_pdf", actual.FailureCode); Assert.Empty(actual.Pages);
        }
        await using var runtime = await data.RuntimeAsync(infected: fault == "infected", badPin: fault == "changed-pin");
        Assert.Equal(fault == "changed-pin" ? "retry" : "completed", await runtime.Runtime.RunOnceAsync());
        if (fault == "changed-pin") {
            Assert.False(await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows(PdfEvidenceValidationRecords.Audits).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows("evidenceLifecycles").Find(new BsonDocument()).AnyAsync());
        } else {
            var proof = await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument()).SingleAsync();
            Assert.Equal("rejected", proof["status"]); Assert.Empty(proof["pages"].AsBsonArray); Assert.True(proof["pageCount"].IsBsonNull);
            Assert.Equal(fault == "infected" ? "infected_media" : "invalid_media", proof["safeFailure"]);
            Assert.Equal("failed", (await data.Rows("evidenceLifecycles").Find(new BsonDocument()).SingleAsync())["state"]);
        }
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync(reserved, reference));
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("paymentSubmissions").Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("payments").Find(new BsonDocument()).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Actual_supplier_pdf_keeps_separate_image_admissions_current_source_scope_and_submits_one_distinct_document_job() {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture);
        var bytes = PdfEvidenceAdmissionTestRuntime.Pdf(); const string key = "supplier-pdf-and-image-key";
        var reserved = await data.Evidence.ReservePdfAsync(data.Actor, data.DraftId, 1, SupplierDocumentTestData.Meta(key));
        var image = await data.Evidence.ReserveAsync(data.Actor, data.DraftId, 1, SupplierDocumentTestData.Meta(key));
        Assert.Equal(reserved, await data.Evidence.RecoverPdfReservationAsync(data.Actor, data.DraftId, key));
        Assert.Equal(image, await data.Evidence.RecoverReservationAsync(data.Actor, data.DraftId, key));
        using var original = new MemoryStream(bytes);
        var outcome = await data.Evidence.UploadPdfAsync(data.Actor, data.DraftId, reserved.ReservationId, reserved.Revision, original, SupplierDocumentTestData.Meta("supplier-pdf-upload"));
        var reference = outcome.Evidence!.Reference; Assert.Equal("quarantined", outcome.State);
        Assert.Equal(outcome, await data.Evidence.RecoverPdfUploadAsync(data.Actor, data.DraftId, reserved.ReservationId, SupplierDocumentTestData.Meta("supplier-pdf-upload")));
        await using var runtime = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, data.Unit, data.Clock, data.Files.Path, ProcurementInspectionTestData.StoreId);
        Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        Assert.Equal(reserved.ReservationId, Assert.Single(await data.Evidence.ListPdfsAsync(data.Actor, data.DraftId)).ReservationId);
        Assert.Equal(image.ReservationId, Assert.Single(await data.Evidence.ListAsync(data.Actor, data.DraftId)).ReservationId);
        await using (var read = await data.Evidence.OpenPdfReadAsync(data.Actor, data.DraftId, reserved.ReservationId, 1, reference, SupplierDocumentTestData.ReadMetadata)) {
            using var copy = new MemoryStream(); await read.Content.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray()); Assert.Equal("application/pdf", read.Mime);
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenPdfReadAsync(data.Outsider, data.DraftId, reserved.ReservationId, 1, reference, SupplierDocumentTestData.ReadMetadata));
        using var never = new MustNotRead(); await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.UploadAsync(data.Actor, data.DraftId, reserved.ReservationId, 1, never, SupplierDocumentTestData.Meta("image-cannot-claim-pdf"))); Assert.Equal(0, never.Reads);
        var preview = await data.Service.PreviewSubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference);
        var metadata = SupplierDocumentTestData.Meta("supplier-pdf-submitted");
        var submitted = await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference, preview["binding"].AsString, metadata);
        Assert.Equal("submitted_unmatched", submitted["state"]);
        var row = await data.Rows(SupplierDocumentBson.Submissions).Find(new BsonDocument()).SingleAsync();
        var pdfJob = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind)).SingleAsync();
        Assert.Equal(submitted["documentId"], row["_id"]); Assert.Equal(row["_id"], pdfJob["payload"]["submissionId"]);
        Assert.Equal(row["pinId"], pdfJob["payload"]["pinId"]); Assert.Equal(row["draftHash"], pdfJob["payload"]["draftHash"]);
        Assert.Equal(row["actorId"], pdfJob["payload"]["uploaderId"]); Assert.Equal(reference.Sha256, pdfJob["payload"]["original"]["sha256"]);
        Assert.Equal("pending", pdfJob["status"]);
        Assert.Equal(row["_id"], (await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "SupplierDocument")).SingleAsync())["associationId"]);
        Assert.Equal(submitted, await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference, preview["binding"].AsString, metadata));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", PdfDocumentExtractionContract.SupplierJobKind)));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", "supplierdocument.ocr.requested")).AnyAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenPdfReadAsync(data.Outsider, data.DraftId, reserved.ReservationId, 1, reference, SupplierDocumentTestData.ReadMetadata));
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }
    private sealed class MustNotRead : MemoryStream {
        internal int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Reads++; throw new InvalidOperationException("Denied input was read"); }
    }
    internal sealed class PaymentPdfData : IAsyncDisposable {
        internal EvidenceAdmissionTestData Files = null!;
        internal PaymentDraftData Payment = null!;
        internal PaymentEvidenceService Evidence = null!;
        internal long DeclarationId;
        internal IMongoDatabase Database => Payment.Invoice.Order.Database;
        internal CatalogAuthoringTestData Catalog => Payment.Invoice.Order.Prices.Catalog;
        internal MutableAuthorizationClock Clock => Catalog.Clock;
        internal UnitToko.Modules.Identity.Contracts.ActorContext Actor => Payment.Invoice.Order.Buyer;
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal static async Task<PaymentPdfData> CreateAsync(MongoFixture fixture) {
            var data = new PaymentPdfData();
            try {
                data.Files = await EvidenceAdmissionTestData.CreateAsync(fixture); data.Payment = await PaymentDraftData.CreateAsync(fixture);
                var policy = EvidenceAdmissionSchemaTests.Policy(1024 * 1024, 10, 30 * 1024 * 1024); policy["_id"] = (await new IdAllocator(data.Database).NextAsync("evidenceStorePolicies")).Value;
                await data.Rows("evidenceStorePolicies").InsertOneAsync(policy);
                await PaymentEvidenceReadTests.ReadData.PolicyAsync(data.Database, data.Files.StoreId, data.Clock.UtcNow);
                await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("storeId", data.Files.StoreId), Builders<BsonDocument>.Update.Set("maximumBytes", 1024L * 1024));
                data.Evidence = new(data.Database, new(data.Catalog.Client), new(data.Database), data.Clock, data.Payment.Service, data.Files.Registration);
                var saved = await data.Payment.Save(null, 0, data.Payment.Input, "pdf-evidence-owner"); data.DeclarationId = saved["declarationId"].AsInt64;
                return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        internal async Task<(ReservationResult Reservation, EvidenceRef Reference)> UploadAsync(byte[] bytes, string key = "payment-pdf") {
            var reservation = await Evidence.ReservePdfAsync(Actor, DeclarationId, 1, EvidenceAdmissionTestData.Meta(key));
            using var input = new MemoryStream(bytes); var outcome = await Evidence.UploadPdfAsync(Actor, DeclarationId, reservation.ReservationId, reservation.Revision, input, EvidenceAdmissionTestData.Meta(key + "-upload"));
            Assert.Equal("quarantined", outcome.State); Assert.NotNull(outcome.Evidence);
            Assert.Equal(outcome, await Evidence.RecoverPdfUploadAsync(Actor, DeclarationId, reservation.ReservationId, EvidenceAdmissionTestData.Meta(key + "-upload")));
            return (reservation, outcome.Evidence.Reference);
        }
        internal Task<PdfEvidenceAdmissionTestRuntime> RuntimeAsync(bool approve = true, bool infected = false, bool badPin = false) =>
            PdfEvidenceAdmissionTestRuntime.CreateAsync(Database, new(Catalog.Client), Clock, Files.Root, Files.StoreId, approve, infected, badPin);
        internal Task<ProtectedEvidenceRead> OpenAsync(ReservationResult reservation, EvidenceRef reference) => Evidence.OpenPdfReadAsync(Actor, DeclarationId,
            reservation.ReservationId, 1, reference, new("payment-evidence-view", "synthetic-pdf-read"));
        internal Task<BsonDocument> JobAsync() => Rows("outboxEvents").Find(new BsonDocument("kind", PdfEvidenceValidationRecords.Kind)).SingleAsync();
        internal async Task<BsonDocument> FinanceAsync() {
            var state = new BsonDocument(); foreach (var name in new[] { "payments", "bankReceipts", "journalEntries", "stockBalances", "movements", "reservations", "paymentAllocations", "refundPayouts", "disbursements" })
                state[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
        public async ValueTask DisposeAsync() { if (Payment is not null) await Payment.DisposeAsync(); if (Files is not null) await Files.DisposeAsync(); }
    }
}
