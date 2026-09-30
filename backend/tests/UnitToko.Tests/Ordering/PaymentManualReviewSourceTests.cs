using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentManualReviewSourceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Manual_source_preserves_actual_failed_attempt_after_later_OCR_success_without_fabricating_results() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        var engine = new PaymentOcrProcessorTests.FixtureEngine { Failure = "ocr_unavailable" };
        var worker = PaymentOcrProcessorTests.Processor(data, engine); await PaymentOcrProcessorTests.Policy(data, worker); Assert.Equal("retry", await worker.RunOnceAsync());
        var reviewer = await PaymentReviewTests.Reviewer(data);
        var saved = await Consume(data, reviewer);
        Assert.Equal("ocr_unavailable", saved["safeFailure"]); Assert.Equal("retry", saved["jobState"]); Assert.Equal(1, saved["attempt"].AsInt32);
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(PaymentVerificationBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        engine.Failure = null; data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(1); Assert.Equal("completed", await worker.RunOnceAsync());
        var denied = await Assert.ThrowsAsync<DomainValidationException>(() => Consume(data, reviewer)); Assert.Equal("payment_manual_review_failure_required", denied.Code);
        var submission = await data.Rows("paymentSubmissions").Find(new BsonDocument("declarationId", data.DeclarationId)).SingleAsync();
        await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, token) => { await data.Service.ValidateAcceptedManualFailureAsync(tx, data.DeclarationId, 1, submission["_id"].AsInt64, data.Reference, saved, token); return true; });
        Assert.Equal(1, await data.Rows("documentProcessingFailures").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        saved["failureHash"] = new string('f', 64);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, token) => {
            await data.Service.ValidateAcceptedManualFailureAsync(tx, data.DeclarationId, 1, submission["_id"].AsInt64, data.Reference, saved, token); return true;
        }));
    }
    [WindowsStorageFact]
    public async Task Outage_does_not_bypass_scope_clean_original_or_final_source_deadline_and_abort_rolls_back_job_fence() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        var worker = PaymentOcrProcessorTests.Processor(data, new PaymentOcrProcessorTests.FixtureEngine { Failure = "ocr_timeout" });
        await PaymentOcrProcessorTests.Policy(data, worker); Assert.Equal("retry", await worker.RunOnceAsync()); var reviewer = await PaymentReviewTests.Reviewer(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Consume(data, data.Payment.Invoice.Finance));
        var before = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Consume(data, reviewer, () => throw new InvalidOperationException("synthetic downstream failure")));
        Assert.Equal(before, await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync());
        var now = data.Catalog.Clock.UtcNow;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Consume(data, reviewer, () => data.Catalog.Clock.UtcNow = now.AddSeconds(6)));
        data.Catalog.Clock.UtcNow = now;
        Assert.Equal(before, await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync());
        await data.Rows("evidenceValidationPolicies").UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Consume(data, reviewer));
        Assert.Equal(before, await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync());
    }
    private static Task<BsonDocument> Consume(PaymentEvidenceReadTests.ReadData data, ActorContext reviewer, Action? after = null) =>
        data.Service.WithVerificationOriginalAsync<BsonDocument>(reviewer, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, (consume, token) =>
            new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, inner) => consume(tx, async (view, eligible, final) => {
                var source = await data.Service.ManualFailureAsync(tx, view, 1, final);
                after?.Invoke(); source.RequireCurrent(); eligible(); return source.Snapshot;
            }, inner), token), CancellationToken.None);
}
