using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentQueueTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Bulk_queue_uses_current_ownership_and_exact_OCR_history_and_refuses_corruption_or_revoked_scope_without_money_effects() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var reader = data.Reader; var drafts = data.Read.Payment.Service; var initial = Assert.Single((await drafts.ListReviewQueueAsync(reader, null, PaymentReviewTests.Metadata)).Items);
        Assert.Equal("pending", initial.State); Assert.Equal("succeeded", initial.OcrState);
        var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "claim"), PaymentVerificationTestData.Meta("queue-owner"));
        var assigned = Assert.Single((await drafts.ListReviewQueueAsync(reader, null, PaymentReviewTests.Metadata)).Items); Assert.Equal("assigned", assigned.State);
        Assert.Equal(data.Reviewer.UserId, assigned.AssigneeId); Assert.Equal(1, assigned.ReviewRevision); Assert.NotNull(assigned.RecordedAt);
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync();
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]), Builders<BsonDocument>.Update.Unset("processingReceipt")));
        // Force historical damage only in this isolated fixture after proving the live validator rejects it.
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]), Builders<BsonDocument>.Update.Unset("processingReceipt"), new UpdateOptions { BypassDocumentValidation = true });
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => drafts.ListReviewQueueAsync(reader, null, PaymentReviewTests.Metadata));
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]), Builders<BsonDocument>.Update.Set("processingReceipt", job["processingReceipt"]));
        shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 1, new(shown.Binding, "request_replacement"), PaymentVerificationTestData.Meta("queue-close"));
        var closed = Assert.Single((await drafts.ListReviewQueueAsync(reader, null, PaymentReviewTests.Metadata)).Items);
        Assert.Equal("replacement_requested", closed.State); Assert.Equal(data.Reviewer.UserId, closed.AssigneeId); Assert.Equal(2, closed.ReviewRevision);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", reader.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => drafts.ListReviewQueueAsync(reader, null, PaymentReviewTests.Metadata));
        Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument())); Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
