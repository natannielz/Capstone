using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentReviewTests(MongoFixture fixture)
{
    internal static AuthorizationReadMetadata Metadata => new("payment-review", "synthetic-review");
    internal static Task<ActorContext> Reviewer(PaymentEvidenceReadTests.ReadData data) => data.Catalog.ActorAsync(9952, CatalogAuthoringTestData.Shop, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
    [WindowsStorageFact]
    public async Task Explicit_reviewer_sees_only_submitted_queue_and_audited_original_and_OCR_without_money_effects() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var reviewer = await Reviewer(data);
        Assert.Empty((await data.Payment.Service.ListReviewsAsync(reviewer, null, Metadata)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ReadReviewAsync(reviewer, data.DeclarationId, Metadata));
        await PaymentOcrProcessorTests.Submit(data);
        var page = await data.Payment.Service.ListReviewsAsync(reviewer, null, Metadata); Assert.False(page.HasMore); Assert.Equal(data.DeclarationId, Assert.Single(page.Items).Id.Value);
        Assert.Empty((await data.Payment.Service.ListReviewsAsync(reviewer, data.DeclarationId, Metadata)).Items);
        var detail = await data.Payment.Service.ReadReviewAsync(reviewer, data.DeclarationId, Metadata); Assert.Equal("submitted", detail.State);
        await using (var file = await data.Service.OpenReviewReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId, detail.Revision, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata)) {
            using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        var queued = await data.Service.ReadReviewExtractionAsync(reviewer, data.DeclarationId, detail.Revision, data.Reference, new("payment-ocr-view", "synthetic-review")); Assert.Equal("queued", queued.State);
        var engine = new PaymentOcrProcessorTests.FixtureEngine(); var processor = PaymentOcrProcessorTests.Processor(data, engine); await PaymentOcrProcessorTests.Policy(data, processor); Assert.Equal("completed", await processor.RunOnceAsync());
        var extracted = await data.Service.ReadReviewExtractionAsync(reviewer, data.DeclarationId, detail.Revision, data.Reference, new("payment-ocr-view", "synthetic-review")); Assert.Equal("succeeded", extracted.State);
        Assert.Equal("100.00", extracted.Extraction!.Fields.Single(field => field.Field == "amount").Value);
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", reviewer.UserId)).SingleAsync(); Assert.Equal("released", lease["state"]);
        Assert.Equal(2, await data.Rows("documentExtractionReadAudits").CountDocumentsAsync(new BsonDocument("actorId", reviewer.UserId)));
        Assert.True(await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument { { "kind", "billing.payment.review.read" }, { "actorId", reviewer.UserId } }) >= 4);
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("submitted", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
    }
    [WindowsStorageFact]
    public async Task Scope_intersection_and_explicit_permission_protect_all_review_routes_and_buyer_routes_stay_private() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data); var reviewer = await Reviewer(data);
        var other = await data.Catalog.ActorAsync(9953, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        Assert.Empty((await data.Payment.Service.ListReviewsAsync(other, null, Metadata)).Items);
        foreach (var denied in new[] { data.Actor, data.Payment.Invoice.Finance, other }) {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ReadReviewAsync(denied, data.DeclarationId, Metadata));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.OpenReviewReadAsync(denied, data.DeclarationId, data.Reservation.ReservationId, 1, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadReviewExtractionAsync(denied, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "synthetic-review")));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ReadAsync(reviewer, data.DeclarationId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.OpenReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId, 1, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata));
        var noInvoices = await data.Catalog.ActorAsync(9954, CatalogAuthoringTestData.Shop, Permissions.PaymentReviewRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ListReviewsAsync(noInvoices, null, Metadata));
        await data.Catalog.Base.AddGrantAsync(noInvoices, 99540, CatalogAuthoringTestData.OtherOwner, [Permissions.InvoiceRead]);
        Assert.Empty((await data.Payment.Service.ListReviewsAsync(noInvoices, null, Metadata)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ReadReviewAsync(noInvoices, data.DeclarationId, Metadata));
    }
    [WindowsStorageFact]
    public async Task Stale_review_reference_and_unsubmitted_upload_cannot_read_and_revoked_grant_stops_late_access() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var reviewer = await Reviewer(data);
        await data.ValidateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.OpenReviewReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId, 1, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata));
        await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("review-submit"));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.Service.OpenReviewReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId, 2, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata));
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.Service.ReadReviewExtractionAsync(reviewer, data.DeclarationId, 1, new(data.Reference.Id, 1, new string('f',64)), new("payment-ocr-view", "synthetic-review")));
        data.Service.ReadObserver = new(async () => { await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", reviewer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked")); });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.OpenReviewReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId, 1, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata));
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("actor.userId", reviewer.UserId)).SingleAsync(); Assert.Equal("released", lease["state"]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadReviewExtractionAsync(reviewer, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "synthetic-review")));
    }
    [WindowsStorageFact]
    public async Task Required_review_audit_failure_returns_no_detail_original_or_derivative_and_rolls_back_fences() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data); var reviewer = await Reviewer(data);
        var before = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync();
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray {
            definition["options"]["validator"], new BsonDocument("kind", new BsonDocument("$ne", "billing.payment.review.read")) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Payment.Service.ListReviewsAsync(reviewer, null, Metadata));
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Payment.Service.ReadReviewAsync(reviewer, data.DeclarationId, Metadata));
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.OpenReviewReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId, 1, data.Reference, PaymentEvidenceReadTests.ReadData.Metadata));
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.ReadReviewExtractionAsync(reviewer, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "synthetic-review")));
        Assert.Equal(before, await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync());
        Assert.Equal(0, await data.Rows("documentExtractionReadAudits").CountDocumentsAsync(new BsonDocument("actorId", reviewer.UserId)));
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument("actor.userId", reviewer.UserId)));
    }
}
