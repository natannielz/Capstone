using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentNotificationStatus(string State, DateTimeOffset? AvailableAt);

// Durable availability in the account's private payment workspace; no email, SMS or external transport.
public sealed class PaymentReviewNotifications(IMongoDatabase database, MongoUnitOfWork unitOfWork, IClock clock)
{
    internal const string ConsumerId = "payment-review-in-app-v1";
    private readonly OutboxStore outbox = new(database, clock);
    private static readonly OutboxClaimPolicy Policy = new([PaymentReviewActionRecords.Operation], 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    internal static Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) =>
        DocumentProcessingStorage.RequireOutboxAsync(database, PaymentNotificationSchemas.OutboxValidator(), ct);
    private async Task RequireRuntimeAsync(CancellationToken ct) {
        await RequireStorageAsync(database, ct);
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw Broken();
    }
    public async Task<string> RunOnceAsync(CancellationToken ct = default) {
        await RequireRuntimeAsync(ct);
        var claim = await outbox.ClaimAsync(ConsumerId, Policy, ct); if (claim is null) return "idle";
        try { await ProcessClaimAsync(claim, ct); return "completed"; }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or DomainValidationException) {
            await outbox.FailAsync(claim, "integrity_mismatch", false, TimeSpan.Zero, ct); return "claim_or_source_changed";
        }
        // An uncertain commit may have made the notice available. Never rewrite its outcome as failed.
    }
    internal async Task ProcessClaimAsync(BsonDocument claim, CancellationToken ct = default) {
        await RequireRuntimeAsync(ct);
        await unitOfWork.ExecuteAsync(async (tx, token) => {
            var job = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx,
                Builders<BsonDocument>.Filter.Eq("_id", claim["_id"]) & PaymentNotificationSchemas.OutboxValidator()).SingleOrDefaultAsync(token) ?? throw Broken();
            var (head, action) = await SourceAsync(database, tx, job, token);
            if (job["status"] == "completed") {
                _ = Status(job, action);
                if (job["notificationReceipt"]["claimToken"] != claim.GetValue("leaseToken", BsonNull.Value)) throw Broken();
                return true;
            }
            var at = new BsonDateTime(clock.UtcNow.UtcDateTime);
            if (at.CompareTo(action["recordedAt"]) < 0) throw Broken();
            var receipt = new BsonDocument { { "schemaVersion", 1 }, { "actionId", action["_id"] }, { "actionHash", action["snapshotHash"] },
                { "recipientId", action["source"]["recordedBy"] }, { "disposition", Closed(action) ? "available" : "not_applicable" }, { "consumerId", ConsumerId },
                { "claimToken", claim["leaseToken"] }, { "claimAttempt", claim["attempts"] }, { "claimedAt", claim["claimStartedAt"] },
                { "leaseUntil", claim["leaseUntil"] }, { "claimDeadline", claim["claimDeadline"] }, { "createdAt", at } };
            receipt.Add("snapshotHash", B.Hash(receipt));
            await new PaymentDraftRecords(database).TouchAsync(tx, head, token);
            await outbox.CompletePaymentNotificationAsync(tx, claim, receipt, token); return true;
        }, ct);
    }
    internal static bool Closed(BsonDocument action) => action["state"].AsString is "rejected" or "replacement_requested";
    internal static async Task<(BsonDocument Head, BsonDocument Action)> SourceAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        if (job.GetValue("kind", "") != PaymentReviewActionRecords.Operation || !job.TryGetValue("payload", out var payload) || !payload.IsBsonDocument ||
            !payload.AsBsonDocument.TryGetValue("actionId", out var actionId) || !actionId.IsInt64 || actionId.AsInt64 <= 0) throw Broken();
        var action = await database.GetCollection<BsonDocument>(PaymentReviewActionRecords.Versions).Find(tx,
            Builders<BsonDocument>.Filter.Eq("_id", actionId) & PaymentReviewExplanationSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw Broken();
        var drafts = new PaymentDraftRecords(database); var head = await drafts.HeadAsync(tx, action["source"]["recordedBy"].AsInt64, action["declarationId"].AsInt64, ct);
        if (head["state"] != "submitted") throw Broken();
        var version = await drafts.VersionAsync(tx, head["versionId"].AsInt64, ct);
        var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        _ = await new PaymentReviewActionRecords(database).LatestAsync(tx, head["_id"].AsInt64, PaymentDraftBson.SubmittedSource(head, version, submission), ct) ?? throw Broken();
        if (job.GetValue("sourceKey", "") != PaymentReviewActionRecords.Operation + ":" + actionId.AsInt64 || job.GetValue("actorId", BsonNull.Value) != action["actorId"] ||
            job.GetValue("requestKey", BsonNull.Value) != action["requestKey"] || job.GetValue("createdAt", BsonNull.Value) != action["recordedAt"] ||
            !InvoiceRecords.Equal(payload, PaymentReviewActionRecords.Result(action)) || action["recordedAt"].CompareTo(submission["submittedAt"]) < 0) throw Broken();
        return (head, action);
    }
    internal static async Task<PaymentNotificationStatus> ReadAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument? action, CancellationToken ct) {
        if (action is null) return new("not_applicable", null);
        var job = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx,
            Builders<BsonDocument>.Filter.Eq("sourceKey", PaymentReviewActionRecords.Operation + ":" + action["_id"].AsInt64) & PaymentNotificationSchemas.OutboxValidator()).SingleOrDefaultAsync(ct) ?? throw Broken();
        // The caller has validated this entire immutable action history and its producer effects in the same snapshot.
        return Status(job, action);
    }
    private static PaymentNotificationStatus Status(BsonDocument job, BsonDocument action) {
        if (job["status"] == "completed") {
            if (!job.TryGetValue("notificationReceipt", out var raw) || !raw.IsBsonDocument) throw Broken();
            var receipt = raw.AsBsonDocument;
            if (receipt.GetValue("snapshotHash", "") != B.Hash(receipt) || receipt.GetValue("schemaVersion", 0) != 1 || receipt.GetValue("consumerId", "") != ConsumerId ||
                receipt.GetValue("actionId", BsonNull.Value) != action["_id"] || receipt.GetValue("actionHash", "") != action["snapshotHash"] ||
                receipt.GetValue("recipientId", BsonNull.Value) != action["source"]["recordedBy"] || receipt.GetValue("disposition", "") != (Closed(action) ? "available" : "not_applicable") ||
                receipt.GetValue("createdAt", BsonNull.Value) != job.GetValue("completedAt", BsonNull.Value) || receipt.GetValue("claimAttempt", BsonNull.Value) != job["attempts"]) throw Broken();
            if (!Closed(action)) return new("not_applicable", null);
            return new("available", new(receipt["createdAt"].ToUniversalTime()));
        }
        if (job.Contains("notificationReceipt")) throw Broken();
        if (!Closed(action)) return new("not_applicable", null);
        return new(job["status"].AsString switch { "pending" => "pending", "leased" => "running", "retry" => "retry", "dead" => "failed", _ => throw Broken() }, null);
    }
    private static PersistenceConflictException Broken() => new("payment_notification_history_invalid", "The private notification source is unavailable");
}
