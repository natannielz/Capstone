using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentNotificationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Populated_interrupted_upgrade_preserves_the_exact_producer_graph_and_no_worker_claims_before_its_receipt() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var db = data.Read.Database; var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "request_replacement"), PaymentVerificationTestData.Meta("notice-upgrade"));
        var events = await data.Rows("outboxEvents").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var receipts = await data.Rows("schemaMigrations").Find(new BsonDocument("_id", new BsonDocument("$lt", 45))).Sort(new BsonDocument("_id", 1)).ToListAsync();
        // Isolated interrupted-install fixture: the extension exists, but migration completion was not recorded.
        await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 45));
        var processor = new PaymentReviewNotifications(db, new(data.Read.Catalog.Client), data.Read.Catalog.Clock);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.RunOnceAsync());
        Assert.Equal(events, await data.Rows("outboxEvents").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await new CollectionMigrations(db, data.Read.Catalog.Clock).ApplyAsync();
        Assert.Equal(receipts, await data.Rows("schemaMigrations").Find(new BsonDocument("_id", new BsonDocument("$lt", 45))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(events, await data.Rows("outboxEvents").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal("completed", await processor.RunOnceAsync());
    }
    [WindowsStorageFact]
    public async Task In_app_delivery_fences_expired_claims_records_real_retry_and_has_one_source_bound_receipt_without_external_or_money_effects() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var db = data.Read.Database; var clock = data.Read.Catalog.Clock; var drafts = data.Read.Payment.Service;
        var before = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "claim"), PaymentVerificationTestData.Meta("notice-claim"));
        clock.UtcNow = clock.UtcNow.AddMilliseconds(1);
        shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 1, new(shown.Binding, "request_replacement"), PaymentVerificationTestData.Meta("notice-replacement"));
        var outbox = new OutboxStore(db, clock); var policy = new OutboxClaimPolicy([PaymentReviewActionRecords.Operation], 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        var processor = new PaymentReviewNotifications(db, new(data.Read.Catalog.Client), clock);
        var first = (await outbox.ClaimAsync(PaymentReviewNotifications.ConsumerId, policy))!;
        await Assert.ThrowsAsync<MongoWriteException>(() => outbox.CompleteAsync(first)); // Generic completion cannot claim delivery.
        await processor.ProcessClaimAsync(first); await processor.ProcessClaimAsync(first); // Original-token lookup validates the same durable result.
        var firstJob = await data.Rows("outboxEvents").Find(new BsonDocument("_id", first["_id"])).SingleAsync();
        Assert.Equal("not_applicable", firstJob["notificationReceipt"]["disposition"]);
        var expired = (await outbox.ClaimAsync(PaymentReviewNotifications.ConsumerId, policy))!;
        clock.UtcNow = clock.UtcNow.AddSeconds(31);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(expired));
        var retry = (await outbox.ClaimAsync(PaymentReviewNotifications.ConsumerId, policy))!;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(expired));
        Assert.True(await outbox.RetryAsync(retry, TimeSpan.FromSeconds(5)));
        Assert.Equal("retry", (await drafts.ReadFollowUpAsync(data.Read.Actor, data.Read.DeclarationId, 1)).Notification.State);
        Assert.Equal("retry", Assert.Single((await drafts.ListReviewQueueAsync(data.Reader, null, PaymentReviewTests.Metadata)).Items).Notification.State);
        await new MongoUnitOfWork(data.Read.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            var latest = await new PaymentReviewActionRecords(db).LatestAsync(tx, data.Read.DeclarationId, null, ct);
            Assert.Equal("retry", (await PaymentReviewNotifications.ReadAsync(db, tx, latest, ct)).State); return true;
        });
        clock.UtcNow = clock.UtcNow.AddSeconds(6);
        var results = await Task.WhenAll(processor.RunOnceAsync(), new PaymentReviewNotifications(db, new(data.Read.Catalog.Client), clock).RunOnceAsync());
        Assert.Equal(new[] { "completed", "idle" }, results.OrderBy(x => x).ToArray());
        var saved = await data.Rows("outboxEvents").Find(new BsonDocument("_id", expired["_id"])).SingleAsync();
        Assert.Equal("available", saved["notificationReceipt"]["disposition"]); Assert.Equal(data.Read.Actor.UserId, saved["notificationReceipt"]["recipientId"].AsInt64);
        Assert.Equal(3, saved["attempts"]); Assert.Equal("idle", await processor.RunOnceAsync());
        var available = await drafts.ReadFollowUpAsync(data.Read.Actor, data.Read.DeclarationId, 1); Assert.Equal("available", available.Notification.State); Assert.NotNull(available.Notification.AvailableAt);
        Assert.Equal(available.Notification, Assert.Single((await drafts.ListReviewQueueAsync(data.Reader, null, PaymentReviewTests.Metadata)).Items).Notification);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(retry));
        var wrong = saved["notificationReceipt"].AsBsonDocument.DeepClone().AsBsonDocument; wrong["recipientId"] = data.Reviewer.UserId; wrong["snapshotHash"] = B.Hash(wrong);
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", saved["_id"]), Builders<BsonDocument>.Update.Set("notificationReceipt", wrong));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => drafts.ReadFollowUpAsync(data.Read.Actor, data.Read.DeclarationId, 1));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => new MongoUnitOfWork(data.Read.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            var latest = await new PaymentReviewActionRecords(db).LatestAsync(tx, data.Read.DeclarationId, null, ct); return await PaymentReviewNotifications.ReadAsync(db, tx, latest, ct);
        }));
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", saved["_id"]), Builders<BsonDocument>.Update.Set("notificationReceipt", saved["notificationReceipt"]));
        Assert.Equal(before, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("submitted", (await drafts.ReadAsync(data.Read.Actor, data.Read.DeclarationId)).State);
    }
    [WindowsStorageFact]
    public async Task Notification_completion_and_independent_bank_verification_can_race_without_losing_the_notice_or_posting_cash_twice() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "claim"), PaymentVerificationTestData.Meta("notice-bank-race"));
        var preview = await data.PreviewAsync(); var processor = new PaymentReviewNotifications(data.Read.Database, new(data.Read.Catalog.Client), data.Read.Catalog.Clock);
        var notice = processor.RunOnceAsync(); var verification = data.VerifyAsync(preview, "notice-bank-verification"); await Task.WhenAll(notice, verification);
        Assert.Equal("completed", await notice); Assert.Equal(await verification, await data.VerifyAsync(preview, "notice-bank-verification"));
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentReviewActionRecords.Operation)).SingleAsync();
        Assert.Equal("completed", job["status"]); Assert.Equal("not_applicable", job["notificationReceipt"]["disposition"]);
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task A_real_expired_processing_budget_is_failed_and_never_presented_as_available_to_the_buyer_or_queue() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await PaymentReviewActionTests.Grant(data);
        var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "reject"), PaymentVerificationTestData.Meta("notice-deadline"));
        var db = data.Read.Database; var clock = data.Read.Catalog.Clock; var outbox = new OutboxStore(db, clock);
        Assert.NotNull(await outbox.ClaimAsync(PaymentReviewNotifications.ConsumerId, new([PaymentReviewActionRecords.Operation], 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2))));
        clock.UtcNow = clock.UtcNow.AddSeconds(121);
        Assert.Equal("idle", await new PaymentReviewNotifications(db, new(data.Read.Catalog.Client), clock).RunOnceAsync());
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentReviewActionRecords.Operation)).SingleAsync();
        Assert.Equal("dead", job["status"]); Assert.Equal("processing_deadline_exceeded", job["lastFailureCode"]); Assert.False(job.Contains("notificationReceipt"));
        Assert.Equal("failed", (await data.Read.Payment.Service.ReadFollowUpAsync(data.Read.Actor, data.Read.DeclarationId, 1)).Notification.State);
        Assert.Equal("failed", Assert.Single((await data.Read.Payment.Service.ListReviewQueueAsync(data.Reader, null, PaymentReviewTests.Metadata)).Items).Notification.State);
        Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument())); Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
