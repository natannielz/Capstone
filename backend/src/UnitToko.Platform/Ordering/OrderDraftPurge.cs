using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderDraftBson;
using D = UnitToko.Platform.Ordering.OrderDraftDiscardSchemas;

namespace UnitToko.Platform.Ordering;

public sealed record OrderDraftPurgeResult(int Purged, int Refused);

/// <summary>D02 retention for unsubmitted drafts: deletes drafts whose 30-day restore window has passed: head, versions,
/// their receipts, outbox rows and the discard row. Audit events are kept and one "ordering.draft.purged" event is added.
/// Each draft is deleted in its own transaction; a discard that does not match its audit event is refused, never purged.</summary>
public sealed class OrderDraftPurge(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, int batchLimit = OrderDraftPurge.BatchLimit)
{
    public const int BatchLimit = 200;
    internal const string Operation = "ordering.draft.purge";
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);

    public async Task<OrderDraftPurgeResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (batchLimit is < 1 or > BatchLimit) throw new InvalidOperationException("Order draft purge batch is out of range");
        if (!await new CollectionMigrations(database, clock).IsCurrentAsync(ct)) throw B.Invalid("order_draft_storage_unavailable");
        await D.RequireStorageAsync(database, ct);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
        var due = await Rows(D.Collection).Find(F.Lte("restorableUntil", now.UtcDateTime)).Sort(new BsonDocument("restorableUntil", 1))
            .Limit(batchLimit).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        int purged = 0, refused = 0;
        foreach (var candidate in due) {
            ct.ThrowIfCancellationRequested();
            try { if (await PurgeAsync(candidate["_id"].AsInt64, now, ct)) purged++; }
            catch (AuthorizationDeniedException) { refused++; } // integrity mismatch: kept for investigation, never deleted early
        }
        return new(purged, refused);
    }

    private async Task<bool> PurgeAsync(long draftId, DateTimeOffset now, CancellationToken ct)
    {
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await unit.ExecuteAsync(async (tx, token) => {
            var discard = await Rows(D.Collection).Find(tx, F.Eq("_id", draftId)).SingleOrDefaultAsync(token);
            if (discard is null) return false; // restored meanwhile
            if (!await Rows(D.Collection).Find(tx, F.Eq("_id", draftId) & D.Validator()).AnyAsync(token)) throw B.Invalid();
            var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", discard["auditId"])).SingleOrDefaultAsync(token);
            if (audit is null || !CatalogSchemas.SemanticEquals(audit, OrderDraftService.DiscardAudit(discard))) throw B.Invalid();
            if (B.Time(discard["restorableUntil"]) > now) return false;
            var owner = discard["discardedBy"];
            var head = await Rows(B.Heads).Find(tx, F.Eq("_id", draftId) & F.Eq("createdBy", owner)).SingleOrDefaultAsync(token) ?? throw B.Invalid();
            var versions = await Rows(B.Versions).Find(tx, F.Eq("draftId", draftId) & F.Eq("createdBy", owner)).ToListAsync(token);
            if (versions.Count == 0 || versions.Count != await Rows(B.Versions).CountDocumentsAsync(tx, F.Eq("draftId", draftId), cancellationToken: token)) throw B.Invalid();
            var receipts = F.Eq("actorId", owner) & (
                F.Eq("operation", B.Operation) & F.In("key", versions.Select(version => version["requestKey"])) |
                F.In("operation", new[] { B.DiscardOperation, B.RestoreOperation }) & F.Eq("result.draftId", draftId));
            var removedReceipts = (await Rows("idempotencyRecords").DeleteManyAsync(tx, receipts, cancellationToken: token)).DeletedCount;
            var removedOutbox = (await Rows("outboxEvents").DeleteManyAsync(tx,
                F.In("sourceKey", versions.Select(version => "ordering.draft.saved:" + version["_id"].AsInt64)), cancellationToken: token)).DeletedCount;
            await Rows(B.Versions).DeleteManyAsync(tx, F.Eq("draftId", draftId), cancellationToken: token);
            if ((await Rows(B.Heads).DeleteOneAsync(tx, head, cancellationToken: token)).DeletedCount != 1 ||
                (await Rows(D.Collection).DeleteOneAsync(tx, discard, cancellationToken: token)).DeletedCount != 1) throw B.Invalid();
            // No delivery details or input content: identifiers and counts only.
            await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", auditId }, { "schemaVersion", 1 }, { "kind", "ordering.draft.purged" },
                { "actorKind", "system" }, { "operation", Operation }, { "createdAt", now.UtcDateTime }, { "context", discard["context"].DeepClone() },
                { "result", new BsonDocument { { "draftId", draftId }, { "ownerId", owner }, { "discardAuditId", discard["auditId"] },
                    { "discardedAt", discard["discardedAt"] }, { "versions", versions.Count }, { "receipts", (int)removedReceipts }, { "outbox", (int)removedOutbox } } } },
                cancellationToken: token);
            return true;
        }, ct);
    }
}
