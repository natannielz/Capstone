using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Procurement;
using static UnitToko.Tests.Documents.PdfEvidenceAdmissionTests;

namespace UnitToko.Tests.Documents;

// Actual private PDF admission is distinct from primary payment-image evidence.
// Scanner/host approvals are synthetic test inputs, never production acceptance.
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PdfSubmissionSourceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Validated_payment_PDF_remains_private_draft_and_repeated_submission_cannot_replace_required_image()
    {
        await using var data = await PaymentPdfData.CreateAsync(fixture);
        var (reserved, reference) = await data.UploadAsync(PdfEvidenceAdmissionTestRuntime.Pdf());
        await using var runtime = await data.RuntimeAsync(); Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        var before = await MoneyAndStockAsync(data.Database);
        var metadata = EvidenceAdmissionTestData.Meta("document-pdf-payment-submit");
        data.Evidence.ReadObserver = new(() => throw new InvalidOperationException("Primary-PDF refusal must precede original opening"));
        foreach (var service in new[] { data.Evidence, new PaymentEvidenceService(data.Database, new(data.Catalog.Client), new(data.Database), data.Clock, data.Payment.Service, data.Files.Registration) }) {
            var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => service.SubmitAsync(data.Actor, data.DeclarationId, 1, reserved.ReservationId, reference, metadata));
            Assert.Equal("payment_image_evidence_required", refusal.Code);
            Assert.Null(await service.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
        }
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument()).AnyAsync());
        await NoPrimarySubmissionAsync(data);
        data.Evidence.ReadObserver = null;
        await using (var original = await data.OpenAsync(reserved, reference)) { Assert.Equal("application/pdf", original.Mime); }
        // Expiry affects the private live-read capability, never promotes the PDF to primary evidence.
        await data.Rows(PdfEvidenceValidationRecords.Policies).UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false));
        var expired = await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.SubmitAsync(data.Actor, data.DeclarationId, 1, reserved.ReservationId, reference, metadata));
        Assert.Equal("payment_image_evidence_required", expired.Code);
        await NoPrimarySubmissionAsync(data);
        Assert.Equal(before, await MoneyAndStockAsync(data.Database));
        await NoExtractionAsync(data.Database);
    }

    [WindowsStorageFact]
    public async Task Supplier_pdf_submission_retains_draft_publication_and_pin_under_distinct_job_and_same_key_retry()
    {
        await using var data = await SupplierDocumentTestData.CreateAsync(fixture);
        var reserved = await data.Evidence.ReservePdfAsync(data.Actor, data.DraftId, 1, SupplierDocumentTestData.Meta("supplier-pdf-source"));
        using var bytes = new MemoryStream(PdfEvidenceAdmissionTestRuntime.Pdf());
        var uploaded = await data.Evidence.UploadPdfAsync(data.Actor, data.DraftId, reserved.ReservationId, 1, bytes, SupplierDocumentTestData.Meta("supplier-pdf-source-upload"));
        var reference = uploaded.Evidence!.Reference;
        await using var runtime = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, data.Unit, data.Clock, data.Files.Path, ProcurementInspectionTestData.StoreId);
        Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        var before = await MoneyAndStockAsync(data.Database);
        var preview = await data.Service.PreviewSubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference);
        var metadata = SupplierDocumentTestData.Meta("supplier-pdf-source-submit");
        var pinCalls = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.SubmitWithProofAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference,
            preview["binding"].AsString, SupplierDocumentTestData.Meta("supplier-pdf-missing-capability"),
            (_, _, _, _) => { pinCalls++; return Task.FromResult<Action>(() => { }); }, default));
        Assert.Equal(0, pinCalls); Assert.False(await data.Rows(SupplierDocumentBson.Submissions).Find(new BsonDocument()).AnyAsync());
        var result = await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference, preview["binding"].AsString, metadata);
        Assert.Equal("submitted_unmatched", result["state"]);
        var row = await data.Rows(SupplierDocumentBson.Submissions).Find(new BsonDocument()).SingleAsync();
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PdfDocumentExtractionRecords.Kind("SupplierInvoice"))).SingleAsync();
        var source = new SupplierDocumentOcrSource(data.Service);
        var snapshot = await data.Unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, job, ct));
        Assert.Equal(row["_id"].AsInt64, snapshot.SubmissionId); Assert.Equal(reference, snapshot.Evidence);
        Assert.Equal(data.Actor.UserId, snapshot.UploaderId); Assert.Equal(reserved.ReservationId, snapshot.ReservationId);
        Assert.Equal(SupplierDocumentBson.DocumentOcrKey(row), job["sourceKey"]);
        Assert.Equal(row["actorId"], job["payload"]["uploaderId"]); Assert.Equal(row["draftHash"], job["payload"]["draftHash"]);
        Assert.Equal(reference.Sha256, job["payload"]["original"]["sha256"]);
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", SupplierDocumentBson.OcrKey(row))).AnyAsync());
        data.Reservation = reserved; data.Reference = reference;
        using (var locked = File.Open(await data.OriginalPathAsync(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.True(locked.Length > 0);
            Assert.Equal(result, await data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference, preview["binding"].AsString, metadata));
        }
        Assert.Equal(1, await data.Rows(SupplierDocumentBson.Submissions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "SupplierDocument")));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", PdfDocumentExtractionRecords.Kind("SupplierInvoice"))));
        foreach (var field in new[] { "generation", "uploaderId", "draftId", "draftHash" }) {
            var forged = job.DeepClone().AsBsonDocument;
            if (field == "generation") forged["payload"]["original"][field] = new string('f', 64);
            else if (field == "draftHash") forged["payload"][field] = new string('f', 64);
            else forged["payload"][field] = forged["payload"][field].AsInt64 + 1;
            await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), forged);
            await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, forged, ct)));
            await Assert.ThrowsAnyAsync<Exception>(() => data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, reserved.ReservationId, reference, preview["binding"].AsString, metadata));
            await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), job);
        }
        var image = job.DeepClone().AsBsonDocument; image["kind"] = "supplierdocument.ocr.requested";
        image["sourceKey"] = SupplierDocumentBson.OcrKey(row); image["payload"] = SupplierDocumentBson.OcrPayload(row);
        await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, image, ct)));
        await data.Rows(PdfEvidenceValidationRecords.Policies).UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false));
        Assert.Equal(snapshot, await data.Unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, job, ct)));
        Assert.Equal(before, await MoneyAndStockAsync(data.Database));
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
        await NoExtractionAsync(data.Database);
    }

    [WindowsStorageFact]
    public async Task Owning_payment_command_refuses_PDF_before_association_pin_or_queue_and_does_not_consume_submission_key()
    {
        await using var data = await PaymentPdfData.CreateAsync(fixture);
        var (reserved, reference) = await data.UploadAsync(PdfEvidenceAdmissionTestRuntime.Pdf());
        await using var runtime = await data.RuntimeAsync(); Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        var before = await MoneyAndStockAsync(data.Database); var pinCalls = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => data.Payment.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, reserved.ReservationId, reference,
            EvidenceAdmissionTestData.Meta("pdf-missing-capability"), (_, _, _) => { pinCalls++; return Task.FromResult<Action>(() => { }); }, default));
        Assert.Equal(0, pinCalls); Assert.False(await data.Rows(PaymentSubmissionBson.Collection).Find(new BsonDocument()).AnyAsync());
        var metadata = EvidenceAdmissionTestData.Meta("pdf-atomic-same-key");
        for (var attempt = 0; attempt < 2; attempt++) {
            await Assert.ThrowsAnyAsync<Exception>(() => data.Payment.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, reserved.ReservationId, reference,
                metadata, (_, _, _) => { pinCalls++; return Task.FromResult<Action>(() => { }); }, default));
            Assert.Equal(0, pinCalls); await NoPrimarySubmissionAsync(data);
            Assert.Null(await data.Evidence.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
        }
        var publicRefusal = await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.SubmitAsync(data.Actor, data.DeclarationId, 1, reserved.ReservationId, reference, metadata));
        Assert.Equal("payment_image_evidence_required", publicRefusal.Code);
        Assert.Equal(before, await MoneyAndStockAsync(data.Database));
        await NoExtractionAsync(data.Database);
    }

    [WindowsStorageFact]
    public async Task Genuine_private_PDF_cannot_be_reconstructed_as_primary_payment_history_or_relabelled_as_image_OCR_source()
    {
        await using var data = await PaymentPdfData.CreateAsync(fixture);
        var (reserved, reference) = await data.UploadAsync(PdfEvidenceAdmissionTestRuntime.Pdf());
        await using var runtime = await data.RuntimeAsync(); Assert.Equal("completed", await runtime.Runtime.RunOnceAsync());
        var unit = new MongoUnitOfWork(data.Catalog.Client); var source = new PaymentOcrSource(data.Database); var before = await MoneyAndStockAsync(data.Database);
        var proof = await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument()).SingleAsync();
        // This projection deliberately references the genuine, validated original. There is
        // no fabricated image extraction or persisted primary association to adopt.
        var association = new BsonDocument { { "declarationId", data.DeclarationId }, { "declarationRevision", 1L },
            { "reservationId", reserved.ReservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) }, { "uploaderId", data.Actor.UserId } };
        await Assert.ThrowsAnyAsync<Exception>(() => unit.ExecuteAsync((tx, ct) => new PaymentSubmissionRecords(data.Database).OriginalAsync(tx, association, ct)));
        var head = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync();
        var payload = new BsonDocument { { "sourceKind", "Payment" }, { "submissionId", 42L },
            { "evidence", PaymentSubmissionBson.Reference(reference) }, { "pinId", 43L }, { "submissionHash", new string('a', 64) },
            { "uploaderId", data.Actor.UserId }, { "reservationId", reserved.ReservationId }, { "original", proof["original"] },
            { "declarationId", data.DeclarationId }, { "declarationRevision", 1L }, { "reviewRevision", 1L }, { "declarationVersionId", head["versionId"] } };
        var job = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", PdfDocumentExtractionContract.PaymentJobKind },
            { "sourceKey", "payment.document-ocr.requested:42" }, { "payload", payload }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
            { "dueAt", data.Clock.UtcNow.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } };
        // Even a structurally complete payload containing the real admitted PDF publication
        // cannot activate the unsupported primary-payment queue branch.
        var schemaRefusal = await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("outboxEvents").InsertOneAsync(job));
        Assert.Equal(121, schemaRefusal.WriteError.Code);
        await Assert.ThrowsAnyAsync<Exception>(() => unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, job, ct)));
        var image = job.DeepClone().AsBsonDocument; image["kind"] = "payment.ocr.requested"; image["sourceKey"] = "payment.ocr.requested:42";
        await Assert.ThrowsAnyAsync<Exception>(() => unit.ExecuteAsync((tx, ct) => source.RequireAsync(tx, image, ct)));
        Assert.Equal(proof, await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument("_id", proof["_id"])).SingleAsync());
        await NoPrimarySubmissionAsync(data);
        Assert.Equal(before, await MoneyAndStockAsync(data.Database)); await NoExtractionAsync(data.Database);
    }

    private static async Task NoPrimarySubmissionAsync(PaymentPdfData data)
    {
        Assert.False(await data.Rows(PaymentSubmissionBson.Collection).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSubmission")).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentSubmissionBson.Operation)).AnyAsync());
        foreach (var kind in new[] { "payment.ocr.requested", PdfDocumentExtractionContract.PaymentJobKind })
            Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", kind)).AnyAsync());
        Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
    }

    private static async Task<BsonDocument> MoneyAndStockAsync(IMongoDatabase database)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "payments", "bankReceipts", "journalEntries", "stockBalances", "movements", "reservations", "paymentAllocations", "refundPayouts", "disbursements" })
            result[name] = new BsonArray(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    private static async Task NoExtractionAsync(IMongoDatabase database)
    {
        foreach (var name in new[] { PdfDocumentExtractionRecords.ProcessingReads, PdfDocumentExtractionRecords.Results, PdfDocumentExtractionRecords.Audits,
            PdfDocumentExtractionRecords.Failures, PdfDocumentExtractionRecords.Reads, "documentExtractionResults", "supplierDocumentExtractionResults" })
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
    }
}
