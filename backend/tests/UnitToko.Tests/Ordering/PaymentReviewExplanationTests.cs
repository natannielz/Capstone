using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentReviewExplanationTests(MongoFixture fixture)
{
    [Fact]
    public void Legacy_body_hash_is_unchanged_and_public_text_is_explicit_bounded_and_closing_only() {
        var input = new PaymentReviewActionInput(new string('a', 64), "reject");
        var old = new { declarationId = 51L, expectedReviewRevision = 1L, input = new { input.Binding, input.Action } };
        Assert.Equal(AuthorizationStore.CommandHash(old, "Internal reason"), AuthorizationStore.CommandHash(W.Body(51, 1, input), "Internal reason"));
        Assert.NotEqual(AuthorizationStore.CommandHash(old, "Internal reason"), AuthorizationStore.CommandHash(W.Body(51, 1, input with { PublicExplanation = "Customer explanation" }), "Internal reason"));
        foreach (var text in new[] { "", " hidden ", new string('a', 1001), "hidden\u200b" })
            Assert.Throws<DomainValidationException>(() => PaymentReviewActionPolicy.Copy(input with { PublicExplanation = text }));
        Assert.Throws<DomainValidationException>(() => PaymentReviewActionPolicy.Copy(input with { Action = "claim", PublicExplanation = "Customer explanation" }));
    }
    [WindowsStorageTheory]
    [InlineData("reject", "rejected")] [InlineData("request_replacement", "replacement_requested")]
    public async Task Public_explanation_is_hash_bound_to_one_closing_action_and_private_notice_with_safe_retry_and_no_money(string action, string state) {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var id = data.Read.DeclarationId; var source = await data.Service.ReadReviewActionsAsync(data.Reviewer, id);
        var input = new PaymentReviewActionInput(source.Binding, action, "Unggah bukti asli yang terbaca jelas.");
        var metadata = new AuthorizationCommandMetadata("public-explanation", "Internal independent review note", "synthetic-public-explanation");
        var money = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var result = await data.Service.RecordReviewActionAsync(data.Reviewer, id, 0, input, metadata);
        Assert.Equal(result, await data.Service.RecordReviewActionAsync(data.Reviewer, id, 0, input, metadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.RecordReviewActionAsync(data.Reviewer, id, 0, input with { PublicExplanation = "Changed customer explanation" }, metadata));
        Assert.Equal(result, await data.Service.RecoverReviewActionAsync(data.Reviewer, id, metadata.IdempotencyKey));
        var row = await data.Rows(W.Versions).Find(new BsonDocument()).SingleAsync(); Assert.Equal(2, row["schemaVersion"]);
        Assert.Equal(input.PublicExplanation, row["publicExplanation"]); Assert.Equal(metadata.Reason, row["reason"]);
        var buyer = await data.Read.Payment.Service.ReadFollowUpAsync(data.Read.Actor, id, 1); Assert.Equal(state, buyer.State); Assert.Equal(input.PublicExplanation, buyer.PublicExplanation);
        Assert.Equal("pending", buyer.Notification.State);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Read.Payment.Service.ReadFollowUpAsync(data.Reader, id, 1));
        var processor = new PaymentReviewNotifications(data.Read.Database, new(data.Read.Catalog.Client), data.Read.Catalog.Clock);
        Assert.Equal("completed", await processor.RunOnceAsync()); Assert.Equal("idle", await processor.RunOnceAsync());
        Assert.Equal("available", (await data.Read.Payment.Service.ReadFollowUpAsync(data.Read.Actor, id, 1)).Notification.State);
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", W.Operation)).SingleAsync(); Assert.Equal(row["snapshotHash"], job["notificationReceipt"]["actionHash"]);
        await data.Rows(W.Versions).UpdateOneAsync(new BsonDocument("_id", row["_id"]), Builders<BsonDocument>.Update.Set("publicExplanation", "Changed text without its receipt"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Read.Payment.Service.ReadFollowUpAsync(data.Read.Actor, id, 1));
        await data.Rows(W.Versions).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
        Assert.Equal(input.PublicExplanation, (await data.Read.Payment.Service.ReadFollowUpAsync(data.Read.Actor, id, 1)).PublicExplanation);
        Assert.Equal(money, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(0, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
    }
}
