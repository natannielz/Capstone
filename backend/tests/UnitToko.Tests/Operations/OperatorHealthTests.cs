using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Operations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Operations;

// Q10 (Phase E task 5): operators see whether background work is keeping up, using counts, ages and states only;
// no names, amounts, identifiers or payloads ever leave the database through this report.
[Collection("Mongo")]
public sealed class OperatorHealthTests(MongoFixture fixture)
{
    private static BsonDocument Outbox(long id, string kind, string status, DateTimeOffset createdAt) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "sourceKey", "synthetic-" + id }, { "kind", kind }, { "status", status }, { "attempts", 0 },
        { "payload", new BsonDocument { { "customer", "PT Rahasia Sejahtera" }, { "amount", "4250000.00" } } },
        { "dueAt", createdAt.UtcDateTime }, { "createdAt", createdAt.UtcDateTime },
    };

    [Fact]
    public async Task Health_report_counts_queues_and_ages_without_business_payload()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var operatorActor = await data.AddActorAsync(7700, new(GrantScopeKind.System), Permissions.TechnicalConfigure);
        var now = data.Clock.UtcNow;
        // Synthetic rows for a read-only report: the outbox validator's per-kind payload rules are not under test here.
        await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertManyAsync([
            Outbox(1, "payment.ocr.requested", "pending", now.AddMinutes(-30)),
            Outbox(2, "supplierdocument.ocr.requested", "dead", now.AddHours(-5)),
            Outbox(3, "payment.review.notification", "retry", now.AddMinutes(-12)),
            Outbox(4, "payment.review.notification", "completed", now.AddDays(-2)),
        ], new InsertManyOptions { BypassDocumentValidation = true });
        await data.Database.GetCollection<BsonDocument>("auditEvents").InsertOneAsync(new BsonDocument {
            { "_id", 9001L }, { "kind", "ordering.draft.purged" }, { "createdAt", now.AddHours(-2).UtcDateTime },
            { "result", new BsonDocument { { "draftId", 77L }, { "ownerId", 12L } } } }, new InsertOneOptions { BypassDocumentValidation = true });
        // One discard whose restore window ended three days ago (overdue) and one that ended two hours ago (within the grace day).
        await data.Database.GetCollection<BsonDocument>("orderDraftDiscards").InsertManyAsync([
            new BsonDocument { { "_id", 501L }, { "discardedBy", 12L }, { "requestKey", "k-501" }, { "reason", "Rahasia" }, { "restorableUntil", now.AddDays(-3).UtcDateTime } },
            new BsonDocument { { "_id", 502L }, { "discardedBy", 12L }, { "requestKey", "k-502" }, { "reason", "Rahasia" }, { "restorableUntil", now.AddHours(-2).UtcDateTime } },
        ], new InsertManyOptions { BypassDocumentValidation = true });

        var report = await new OperatorHealthService(data.Database, data.Clock, data.Store).ReadAsync(operatorActor, default);

        Assert.Equal(new QueueHealth(1, 0, 1, 0, now.AddMinutes(-30)), report.Ocr); // the failed item is counted, not aged
        Assert.Equal(new QueueHealth(0, 1, 0, 0, now.AddMinutes(-12)), report.Outbox);
        Assert.Equal(now.AddHours(-2), report.LastDraftPurgeAt);
        Assert.Equal(1, report.OverdueDraftPurges);
        Assert.Equal(CollectionMigrations.CurrentVersion, report.TargetSchemaVersion);
        Assert.True(report.DatabaseWritable);
        Assert.Equal(now, report.ObservedAt);
        var json = JsonSerializer.Serialize(report);
        foreach (var secret in new[] { "Rahasia", "4250000", "synthetic-", "draftId", "77" }) Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public async Task Health_report_requires_the_technical_configuration_permission()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var reviewer = await data.AddActorAsync(7701, new(GrantScopeKind.System), Permissions.AccessAuditRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new OperatorHealthService(data.Database, data.Clock, data.Store).ReadAsync(reviewer, default));
    }
}
