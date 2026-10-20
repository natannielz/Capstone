using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

public sealed record DeliveryAttemptView(LongId Id, LongId ShipmentId, long Sequence, string Outcome, string Reason, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, int RevokedQrCount);
public sealed record DeliveryAttemptPage(LongId ShipmentId, long Sequence, bool Received, ImmutableArray<DeliveryAttemptView> Items, bool HasMore);
public sealed record DeliveryAttemptPreview(LongId ShipmentId, long Sequence, bool Received, DateTimeOffset DispatchedAt, DateTimeOffset ServerTime, DateTimeOffset? LastOccurredAt);

/// <summary>A courier's failed visit is not recipient acceptance or a physical stock return.
/// Append-only history, outstanding-QR revocations, audit, receipt and outbox share one transaction.</summary>
public sealed class DeliveryAttemptService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IdAllocator ids;
    private readonly ShipmentQueryService shipments;
    private readonly ShipmentReceiptService receipts;
    private readonly AuthorizationStore authorization;
    private readonly DeliveryAttemptRecords records;
    public DeliveryAttemptService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ShipmentQueryService shipments, ShipmentReceiptService receipts) {
        if (new[] { shipments.SourceDatabase, receipts.SourceDatabase }.Any(source => !ReferenceEquals(source.Client, database.Client) || !source.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Delivery attempt and receipt owners must share one database transaction");
        this.database = database; this.clock = clock; this.ids = ids; this.shipments = shipments; this.receipts = receipts;
        authorization = new(database, unit, clock); records = new(database, clock);
    }
    private async Task StorageAsync(CancellationToken ct) { await shipments.StorageAsync(ct); await records.StorageAsync(ct); await new ShipmentReceiptRecords(database).StorageAsync(ct); }
    private static void Courier(ActorContext actor, ShipmentDocumentSource source) { if (source.Assignment?.CourierId != actor.UserId) throw DeliveryAttemptRecords.Invalid(); }
    private static DeliveryAttemptView View(BsonDocument row) => new(new(row["_id"].AsInt64), new(row["shipmentId"].AsInt64), row["sequence"].AsInt64,
        row["outcome"].AsString, row["reason"].AsString, ShipmentReceiptRecords.Time(row["occurredAt"]), ShipmentReceiptRecords.Time(row["recordedAt"]), row["revokedChallengeIds"].AsBsonArray.Count);
    private static long Sequence(IReadOnlyList<BsonDocument> history) => history.Count == 0 ? 0 : history[^1]["sequence"].AsInt64;
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    public async Task<DeliveryAttemptPreview> PreviewAsync(ActorContext actor, long shipmentId, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await shipments.DocumentSourceAsync(actor, authority, tx, shipmentId, Permissions.DeliveryAttempt, token); Courier(actor, source);
            var history = await records.HistoryAsync(tx, source, token); var received = await receipts.HasRecordedReceiptAsync(tx, source, token);
            var result = new DeliveryAttemptPreview(new(shipmentId), Sequence(history), received, ShipmentReceiptRecords.Time(source.Snapshot["dispatchedAt"]), Now,
                history.Count == 0 ? null : ShipmentReceiptRecords.Time(history[^1]["occurredAt"]));
            await shipments.CompleteAssignmentAsync(tx, source, token);
            await shipments.FenceAsync(actor, authority, tx, [source.Resource], token, Permissions.DeliveryAttempt); return result;
        }, ct);
    }
    public async Task<DeliveryAttemptPage> ReadAsync(ActorContext actor, long shipmentId, long? beforeSequence = null, CancellationToken ct = default) {
        if (beforeSequence is <= 0) throw DeliveryAttemptRecords.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await shipments.DocumentSourceAsync(actor, authority, tx, shipmentId, Permissions.ShipmentRead, token);
            var history = await records.HistoryAsync(tx, source, token); var received = await receipts.HasRecordedReceiptAsync(tx, source, token);
            var selected = history.Reverse().Where(row => beforeSequence is null || row["sequence"].AsInt64 < beforeSequence).Take(26).ToArray();
            var result = new DeliveryAttemptPage(new(shipmentId), Sequence(history), received, selected.Take(25).Select(View).ToImmutableArray(), selected.Length > 25);
            await shipments.CompleteAssignmentAsync(tx, source, token);
            await shipments.FenceAsync(actor, authority, tx, [source.Resource], token); return result;
        }, ct);
    }
    public async Task<DeliveryAttemptView> RecordAsync(ActorContext actor, long shipmentId, long expectedSequence, string outcome, DateTimeOffset occurredAt, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(DeliveryAttemptRecords.Attempts, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = DeliveryAttemptRecords.Body(shipmentId, expectedSequence, outcome, occurredAt);
        ShipmentDocumentSource source = null!; IReadOnlyList<BsonDocument> history = []; BsonDocument final = null!; var received = false;
        await authorization.ExecuteCommandAsync(actor, DeliveryAttemptRecords.Operation, metadata, body, async (authority, tx, token) => {
            source = await shipments.DocumentSourceAsync(actor, authority, tx, shipmentId, Permissions.DeliveryAttempt, token); Courier(actor, source);
            history = await records.HistoryAsync(tx, source, token); received = await receipts.HasRecordedReceiptAsync(tx, source, token);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.DeliveryAttempt, source.Resource, clock.UtcNow),
                ScopePolicy.RequiredNodes(source.Resource, authority.Directory), Permissions.DeliveryAttempt, new(source.Resource));
        }, async (tx, token) => {
            var now = Now;
            if (received) throw new PersistenceConflictException("shipment_already_received", "Recipient acceptance is already recorded; use the after-sales workflow");
            if (Sequence(history) != expectedSequence) throw new PersistenceConflictException("delivery_attempt_changed", "Reload the current delivery attempt history");
            DeliveryAttemptPolicy.Require(expectedSequence, outcome, occurredAt, ShipmentReceiptRecords.Time(source.Snapshot["dispatchedAt"]), now, metadata.Reason);
            if (occurredAt < (source.Assignment ?? throw ShipmentCourierRecords.Invalid()).View.EffectiveAt)
                throw new DomainValidationException("invalid_delivery_attempt_time", "Visit time cannot precede the courier's current assignment");
            var previous = history.LastOrDefault();
            if (previous is not null && (now < ShipmentReceiptRecords.Time(previous["recordedAt"]) || occurredAt < ShipmentReceiptRecords.Time(previous["occurredAt"])))
                throw new DomainValidationException("invalid_delivery_attempt_time", "Visit time cannot precede the last recorded visit");
            var challenges = await receipts.OpenChallengesForFailedVisitAsync(tx, source, now, token);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "shipmentId", shipmentId }, { "shipmentRevision", source.Snapshot["shipmentRevision"] }, { "dispatchHash", source.Snapshot["dispatchHash"] },
                { "sequence", checked(expectedSequence + 1) }, { "previousAttemptId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : new BsonString(CatalogRepository.Digest(previous)) },
                { "courierId", actor.UserId }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "outcome", outcome }, { "occurredAt", occurredAt.UtcDateTime }, { "recordedAt", now.UtcDateTime },
                { "revokedChallengeIds", new BsonArray(challenges.Select(value => new BsonInt64(value))) }, { "auditId", auditId }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } };
            await records.Rows(DeliveryAttemptRecords.Attempts).InsertOneAsync(tx, row, cancellationToken: token);
            await new ShipmentCourierRecords(database, clock).BindAsync(tx, source, "attempt", row, token);
            foreach (var challenge in challenges) await records.Rows(DeliveryAttemptRecords.Revocations).InsertOneAsync(tx, DeliveryAttemptRecords.Revocation(row, challenge), cancellationToken: token);
            await records.Rows("auditEvents").InsertOneAsync(tx, DeliveryAttemptRecords.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", DeliveryAttemptRecords.Operation },
                { "sourceKey", DeliveryAttemptRecords.Operation + ":" + id }, { "payload", DeliveryAttemptRecords.Result(row) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return DeliveryAttemptRecords.Result(row);
        }, ct, async (authority, tx, token) => {
            var accepted = await records.HistoryAsync(tx, source, token);
            final = accepted.SingleOrDefault(row => row["actorId"] == actor.UserId && row["requestKey"] == metadata.IdempotencyKey) ?? throw DeliveryAttemptRecords.Invalid();
            if (final["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw DeliveryAttemptRecords.Invalid();
            await shipments.CompleteAssignmentAsync(tx, source, token);
            await shipments.FenceAsync(actor, authority, tx, [source.Resource], token, Permissions.DeliveryAttempt);
            if (clock.UtcNow < ShipmentReceiptRecords.Time(final["recordedAt"])) throw DeliveryAttemptRecords.Invalid();
        });
        return View(final);
    }
    public async Task<DeliveryAttemptView?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original delivery attempt", "delivery-attempt-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, Permissions.DeliveryAttempt, clock.UtcNow));
            var rows = await records.Rows(DeliveryAttemptRecords.Attempts).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).Limit(2).ToListAsync(token);
            var receipt = await records.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", DeliveryAttemptRecords.Operation }, { "key", key } }).SingleOrDefaultAsync(token);
            if (rows.Count == 0 && receipt is null) { await shipments.FenceAsync(actor, authority, tx, [], token, Permissions.DeliveryAttempt); return null; }
            if (rows.Count != 1 || receipt is null) throw DeliveryAttemptRecords.Invalid();
            var source = await shipments.DocumentSourceAsync(actor, authority, tx, rows[0]["shipmentId"].AsInt64, Permissions.DeliveryAttempt, token); Courier(actor, source);
            var history = await records.HistoryAsync(tx, source, token); var result = View(history.Single(row => row["_id"] == rows[0]["_id"]));
            await shipments.CompleteAssignmentAsync(tx, source, token);
            await shipments.FenceAsync(actor, authority, tx, [source.Resource], token, Permissions.DeliveryAttempt); return result;
        }, ct);
    }
}
