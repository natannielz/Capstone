using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementEvidenceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_primary_image_draft_reserve_retry_restart_recovery_and_expired_D06_status_preserve_financial_identity()
    {
        await using var fixtureData = await PaymentSupplementEvidenceTestData.CreateAsync(fixture); var data = fixtureData.Data;
        var primary = fixtureData.Primary; var supplement = fixtureData.Supplement; var before = await fixtureData.FinancialAsync();
        var metadata = PaymentSupplementEvidenceTestData.Meta("reserve-once");
        var reserved = await fixtureData.Facade.ReservePdfAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, metadata);
        Assert.Equal(reserved, await fixtureData.Facade.ReservePdfAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, metadata));
        var restarted = fixtureData.Restart();
        Assert.Equal(reserved, await restarted.RecoverPdfReservationAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, metadata.IdempotencyKey));
        Assert.Equal(reserved.ReservationId, Assert.Single(await restarted.ListPdfsAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1)).ReservationId);
        Assert.Empty(await restarted.ListPdfsAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, reserved.ReservationId));
        var reservation = await data.Rows("evidenceUploadReservations").Find(new BsonDocument("_id", reserved.ReservationId)).SingleAsync();
        Assert.Equal("PaymentSupplement", reservation["target"]["kind"]); Assert.Equal(supplement.SupplementId, reservation["target"]["id"]);
        Assert.Equal("PaymentSupplementPdf", reservation["purpose"]); Assert.Equal(1L, reservation["admittedTargetRevision"]);
        await data.Rows(PaymentSupplementBson.Policies).UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false).Inc("revision", 1L));
        Assert.Equal(reserved, await restarted.RecoverPdfReservationAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, metadata.IdempotencyKey));
        Assert.Equal(reserved.ReservationId, (await restarted.PdfStatusAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, reserved.ReservationId)).ReservationId);
        Assert.Equal("payment_supplement_policy_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => restarted.ReservePdfAsync(data.Actor,
            primary.DeclarationId, supplement.SupplementId, 1, PaymentSupplementEvidenceTestData.Meta("new-after-expiry")))).Code);
        Assert.Equal(before, await fixtureData.FinancialAsync());
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSupplement")).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Draft_owner_cross_declaration_foreign_scope_wrong_revision_and_primary_image_reference_are_not_supplement_capabilities()
    {
        await using var fixtureData = await PaymentSupplementEvidenceTestData.CreateAsync(fixture); var data = fixtureData.Data;
        var primary = fixtureData.Primary; var supplement = fixtureData.Supplement; var service = fixtureData.Facade;
        var foreignDatabase = data.Catalog.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "_foreign-supplement");
        Assert.Throws<ArgumentException>(() => new PaymentSupplementEvidenceService(foreignDatabase, new(data.Catalog.Client), new(foreignDatabase),
            data.Catalog.Clock, data.Payment.Service, data.Files.Registration));
        Assert.Throws<ArgumentException>(() => new PaymentSupplementEvidenceHistory(foreignDatabase,
            new EvidenceService(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock)));
        var reserved = await service.ReservePdfAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, PaymentSupplementEvidenceTestData.Meta("scoped-reserve"));
        var foreign = await data.Catalog.ActorAsync(95501, new GrantScope(GrantScopeKind.BuyerOwned, 20, 21), Permissions.PaymentEvidence, Permissions.InvoiceRead);
        await Assert.ThrowsAnyAsync<Exception>(() => service.PdfStatusAsync(foreign, primary.DeclarationId, supplement.SupplementId, 1, reserved.ReservationId));
        await Assert.ThrowsAnyAsync<Exception>(() => service.RecoverPdfReservationAsync(data.Actor, primary.DeclarationId + 1000, supplement.SupplementId, 1,
            PaymentSupplementEvidenceTestData.Meta("scoped-reserve").IdempotencyKey));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.PdfStatusAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 2, reserved.ReservationId));
        await Assert.ThrowsAnyAsync<Exception>(() => service.AttachAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1,
            data.Reservation.ReservationId, data.Reference, PaymentSupplementEvidenceTestData.Meta("primary-cannot-attach")));
        Assert.False(await data.Rows(PaymentSupplementBson.Attachments).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", PaymentSupplementBson.AttachOperation },
            { "key", PaymentSupplementEvidenceTestData.Meta("primary-cannot-attach").IdempotencyKey } }).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Actual_validated_whole_pdf_attaches_once_to_real_image_and_restarts_with_exact_job_pin_and_original_key()
    {
        await using var fixtureData = await PaymentSupplementEvidenceTestData.CreateAsync(fixture); var data = fixtureData.Data;
        var primary = fixtureData.Primary; var supplement = fixtureData.Supplement; var service = fixtureData.Facade;
        var financial = await fixtureData.FinancialAsync();
        var primaryHead = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", primary.DeclarationId)).SingleAsync();
        await using (var image = await data.Service.OpenReadAsync(data.Actor, primary.DeclarationId, data.Reservation.ReservationId,
            primaryHead["revision"].AsInt64, primary.PrimaryEvidence, PaymentEvidenceReadTests.ReadData.Metadata)) {
            using var originalImage = new MemoryStream(); await image.Content.CopyToAsync(originalImage); Assert.Equal(data.Bytes, originalImage.ToArray());
        }
        var reserved = await service.ReservePdfAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, PaymentSupplementEvidenceTestData.Meta("actual-PDF"));
        var bytes = PdfEvidenceAdmissionTestRuntime.Pdf(); using var source = new MemoryStream(bytes);
        var uploaded = await service.UploadPdfAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, reserved.ReservationId, reserved.Revision,
            source, PaymentSupplementEvidenceTestData.Meta("upload-original")); Assert.NotNull(uploaded.Evidence);
        await using var scanner = await PdfEvidenceAdmissionTestRuntime.CreateAsync(data.Database, new(data.Catalog.Client), data.Catalog.Clock, data.Files.Root, data.Files.StoreId);
        Assert.Equal("completed", await scanner.Runtime.RunOnceAsync());
        var reference = uploaded.Evidence!.Reference; var command = PaymentSupplementEvidenceTestData.Meta("attach-original");
        var auditDefinition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var auditValidator = auditDefinition["options"]["validator"].AsBsonDocument;
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" },
            { "validator", new BsonDocument("$and", new BsonArray { auditValidator, new BsonDocument("kind", new BsonDocument("$ne", PaymentSupplementBson.AttachOperation)) }) } });
        var refusedCommand = PaymentSupplementEvidenceTestData.Meta("attach-audit-refused");
        try {
            await Assert.ThrowsAnyAsync<MongoException>(() => service.AttachAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1,
                reserved.ReservationId, reference, refusedCommand));
        } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", auditValidator } }); }
        Assert.False(await data.Rows(PaymentSupplementBson.Attachments).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSupplement")).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", PaymentSupplementBson.AttachOperation }, { "key", refusedCommand.IdempotencyKey } }).AnyAsync());
        Assert.Equal("not_confirmed", (await service.RecoverAttachAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, refusedCommand.IdempotencyKey)).Status);
        var attached = await service.AttachAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, reserved.ReservationId, reference, command);
        Assert.Equal(2L, attached["supplementRevision"]); Assert.Equal("attached", attached["state"]);
        var restarted = fixtureData.Restart();
        Assert.Equal(attached, await restarted.AttachAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, reserved.ReservationId, reference, command));
        var recovered = await restarted.RecoverAttachAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, command.IdempotencyKey);
        Assert.Equal("committed", recovered.Status); Assert.Equal(attached, recovered.Result);
        Assert.Equal(1L, await data.Rows(PaymentSupplementBson.Attachments).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows(PaymentSupplementBson.Versions).CountDocumentsAsync(new BsonDocument()));
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "PaymentSupplement")).SingleAsync();
        Assert.Equal(supplement.SupplementId, pin["associationId"]); Assert.Equal(supplement.SupplementId, pin["supplementId"]);
        Assert.Equal(2L, pin["supplementRevision"]); Assert.Equal(PaymentSupplementBson.Primary(primary), pin["primary"]);
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).SingleAsync();
        var attachment = await data.Rows(PaymentSupplementBson.Attachments).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(PaymentSupplementBson.JobKey(supplement.SupplementId), job["sourceKey"]); Assert.Equal(PaymentSupplementBson.JobPayload(attachment), job["payload"]);
        await using (var read = await restarted.OpenPdfReadAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, reserved.ReservationId, 2,
            reference, new("payment-supplement-pdf-view", "synthetic-whole-original"))) {
            using var actual = new MemoryStream(); await read.Content.CopyToAsync(actual); Assert.Equal(bytes, actual.ToArray());
        }
        var proof = await data.Rows(PdfEvidenceValidationRecords.Results).Find(new BsonDocument("source.reservationId", reserved.ReservationId)).SingleAsync();
        Assert.Equal("passed", proof["status"]); Assert.Equal("application/pdf", proof["mime"]);
        Assert.Equal(2, proof["pageCount"]); Assert.True(PdfEvidenceValidationRecords.CompletePages(proof));
        Assert.Equal(proof["resultHash"], pin["validationHash"]);
        Assert.Equal(proof["policyId"], pin["validationPolicyId"]); Assert.Equal(proof["policyRevision"], pin["validationPolicyRevision"]);
        var leases = await data.Rows("evidenceReadLeases").Find(new BsonDocument {
            { "target.kind", "PaymentSupplement" }, { "target.id", supplement.SupplementId } }).ToListAsync();
        Assert.Contains(leases, lease => lease["target"]["revision"] == 2L);
        Assert.All(leases, lease => {
            Assert.Equal("released", lease["state"]); Assert.Equal(pin["reference"], lease["reference"]);
            Assert.Equal(proof["resultHash"], lease["validationHash"]); Assert.Equal(pin["lifecycleRevision"], lease["lifecycleRevision"]);
            Assert.Equal(proof["policyId"], lease["validationPolicyId"]); Assert.Equal(proof["policyRevision"], lease["validationPolicyRevision"]);
        });
        // A syntactically valid lease template cannot substitute for genuine
        // whole-original validation, receipt/audit and contiguous page provenance.
        await data.Rows(PdfEvidenceValidationRecords.Results).UpdateOneAsync(new BsonDocument("_id", proof["_id"]),
            new BsonDocument("$set", new BsonDocument("resultHash", new string('f', 64))));
        try {
            await Assert.ThrowsAnyAsync<Exception>(() => restarted.OpenPdfReadAsync(data.Actor, primary.DeclarationId,
                supplement.SupplementId, reserved.ReservationId, 2, reference, new("payment-supplement-pdf-view", "synthetic-corrupt-proof")));
        } finally {
            await data.Rows(PdfEvidenceValidationRecords.Results).ReplaceOneAsync(new BsonDocument("_id", proof["_id"]), proof);
        }
        primaryHead = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", primary.DeclarationId)).SingleAsync();
        await using (var image = await data.Service.OpenReadAsync(data.Actor, primary.DeclarationId, data.Reservation.ReservationId,
            primaryHead["revision"].AsInt64, primary.PrimaryEvidence, PaymentEvidenceReadTests.ReadData.Metadata)) {
            using var originalImage = new MemoryStream(); await image.Content.CopyToAsync(originalImage); Assert.Equal(data.Bytes, originalImage.ToArray());
        }
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.PdfStatusAsync(data.Actor, primary.DeclarationId, supplement.SupplementId, 1, reserved.ReservationId));
        Assert.Equal(financial, await fixtureData.FinancialAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.document-ocr.requested")).AnyAsync());
    }
}
