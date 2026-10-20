using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Operations;

/// <summary>One background queue: how many items wait, retry, failed for good or are being worked on, and since when
/// the oldest item still in flight (pending, retrying or leased) has waited. Failed items are counted, not aged.</summary>
public sealed record QueueHealth(long Pending, long Retrying, long Dead, long Leased, DateTimeOffset? OldestWaitingAt);

/// <summary>Q10 operator view. Counts, ages and states only: no names, amounts, identifiers or payloads. The purge audit
/// is only written when a draft is actually deleted, so overdue discards (restore window ended over a day ago) show
/// whether the purge worker is keeping up.</summary>
public sealed record OperatorHealthReport(DateTimeOffset ObservedAt, QueueHealth Outbox, QueueHealth Ocr, DateTimeOffset? LastDraftPurgeAt,
    long OverdueDraftPurges, int SchemaVersion, int TargetSchemaVersion, bool DatabaseWritable, double? ReplicationLagSeconds);

public sealed class OperatorHealthService(IMongoDatabase database, IClock clock, AuthorizationStore authorization)
{
    private static readonly ResourceScope Administrative = new(IsAdministrative: true);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    // OCR work is queued as outbox events whose kind ends in ".ocr.requested" (payment proofs, supplier documents).
    private static readonly FilterDefinition<BsonDocument> OcrKinds = F.Regex("kind", new BsonRegularExpression(@"\.ocr\.requested$"));

    public async Task<OperatorHealthReport> ReadAsync(ActorContext actor, CancellationToken ct)
    {
        var snapshot = await authorization.ReadCurrentAsync(actor, ct);
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.TechnicalConfigure, clock.UtcNow);
        AuthorizationStore.Require(query);
        if (!query.Matches(Administrative)) throw new AuthorizationDeniedException("permission_denied");

        var outbox = database.GetCollection<BsonDocument>("outboxEvents");
        var ocr = await QueueAsync(outbox, OcrKinds, ct);
        var other = await QueueAsync(outbox, F.Not(OcrKinds), ct);
        var purge = await database.GetCollection<BsonDocument>("auditEvents").Find(F.Eq("kind", "ordering.draft.purged"))
            .Sort(Builders<BsonDocument>.Sort.Descending("createdAt")).Limit(1).Project(Builders<BsonDocument>.Projection.Include("createdAt").Exclude("_id"))
            .FirstOrDefaultAsync(ct);
        var overdue = await database.GetCollection<BsonDocument>(OrderDraftDiscardSchemas.Collection)
            .CountDocumentsAsync(F.Lte("restorableUntil", clock.UtcNow.AddDays(-1).UtcDateTime), cancellationToken: ct);
        var applied = await database.GetCollection<BsonDocument>("schemaMigrations").Find(F.Eq("state", "applied"))
            .Sort(Builders<BsonDocument>.Sort.Descending("_id")).Limit(1).Project(Builders<BsonDocument>.Projection.Include("_id")).FirstOrDefaultAsync(ct);
        var (writable, lag) = await ReplicationAsync(ct);
        return new(clock.UtcNow, other, ocr, purge is null ? null : Time(purge["createdAt"]), overdue, applied is null ? 0 : applied["_id"].ToInt32(),
            CollectionMigrations.CurrentVersion, writable, lag);
    }

    private static async Task<QueueHealth> QueueAsync(IMongoCollection<BsonDocument> outbox, FilterDefinition<BsonDocument> kinds, CancellationToken ct)
    {
        Task<long> Count(string status) => outbox.CountDocumentsAsync(kinds & F.Eq("status", status), cancellationToken: ct);
        var oldest = await outbox.Find(kinds & F.In("status", new[] { "pending", "retry", "leased" }))
            .Sort(Builders<BsonDocument>.Sort.Ascending("createdAt")).Limit(1).Project(Builders<BsonDocument>.Projection.Include("createdAt").Exclude("_id"))
            .FirstOrDefaultAsync(ct);
        return new(await Count("pending"), await Count("retry"), await Count("dead"), await Count("leased"), oldest is null ? null : Time(oldest["createdAt"]));
    }

    /// <summary>Whether this node accepts writes, and the replication lag when the account may read replica-set status
    /// (null when it may not; the report never fails on missing monitor rights).</summary>
    private async Task<(bool Writable, double? LagSeconds)> ReplicationAsync(CancellationToken ct)
    {
        var admin = database.Client.GetDatabase("admin");
        var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct);
        var writable = hello.GetValue("isWritablePrimary", false).ToBoolean();
        try
        {
            var status = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1), cancellationToken: ct);
            var members = status["members"].AsBsonArray.Select(member => member.AsBsonDocument).ToArray();
            var primary = members.FirstOrDefault(member => member.GetValue("stateStr", "").AsString == "PRIMARY");
            if (primary is null) return (writable, null);
            var lag = members.Where(member => member.GetValue("stateStr", "").AsString == "SECONDARY")
                .Select(member => (primary["optimeDate"].ToUniversalTime() - member["optimeDate"].ToUniversalTime()).TotalSeconds).DefaultIfEmpty(0).Max();
            return (writable, Math.Max(0, lag));
        }
        catch (MongoCommandException) { return (writable, null); }
    }

    private static DateTimeOffset Time(BsonValue value) => new(DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc));
}
