using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

internal sealed class DeliveryAttemptRecords(IMongoDatabase database)
{
    internal const string Attempts = "deliveryAttempts", Revocations = "receiptChallengeRevocations", Operation = "fulfillment.delivery-attempt.record";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task StorageAsync(CancellationToken ct) {
        foreach (var name in DeliveryAttemptSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, DeliveryAttemptSchemas.Validator(name), DeliveryAttemptSchemas.Key(name), ct);
    }
    internal static object Body(long shipmentId, long sequence, string outcome, DateTimeOffset occurredAt) => new { shipmentId, expectedSequence = sequence, outcome, occurredAt = occurredAt.ToUniversalTime() };
    internal static BsonDocument Result(BsonDocument row) => new() { { "attemptId", row["_id"] }, { "shipmentId", row["shipmentId"] }, { "sequence", row["sequence"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", CatalogRepository.Digest(row) }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static BsonDocument Revocation(BsonDocument row, long challengeId) => new() { { "_id", challengeId }, { "schemaVersion", 1 }, { "shipmentId", row["shipmentId"] },
        { "attemptId", row["_id"] }, { "sequence", row["sequence"] }, { "recordedAt", row["recordedAt"] } };
    internal async Task<IReadOnlyList<BsonDocument>> HistoryAsync(IClientSessionHandle tx, ShipmentDocumentSource source, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Invalid();
        var shipmentId = source.Snapshot["shipmentId"].AsInt64;
        var rows = await Rows(Attempts).Find(tx, new BsonDocument("shipmentId", shipmentId)).Sort(new BsonDocument("sequence", 1)).Limit(DeliveryAttemptPolicy.MaximumAttempts + 1).ToListAsync(ct);
        if (rows.Count > DeliveryAttemptPolicy.MaximumAttempts) throw Invalid();
        var revocations = await Rows(Revocations).Find(tx, new BsonDocument("shipmentId", shipmentId)).Limit(DeliveryAttemptPolicy.MaximumAttempts * DeliveryReceiptPolicy.MaximumChallengesPerHour + 1).ToListAsync(ct);
        var references = new HashSet<long>(); BsonDocument? previous = null;
        foreach (var row in rows) {
            var sequence = previous is null ? 1L : previous["sequence"].AsInt64 + 1;
            var occurred = ShipmentReceiptRecords.Time(row["occurredAt"]); var recorded = ShipmentReceiptRecords.Time(row["recordedAt"]);
            DeliveryAttemptPolicy.Require(sequence - 1, row["outcome"].AsString, occurred, ShipmentReceiptRecords.Time(source.Snapshot["dispatchedAt"]), recorded, row["reason"].AsString);
            if (row["sequence"] != sequence || row["shipmentRevision"] != source.Snapshot["shipmentRevision"] || row["dispatchHash"] != source.Snapshot["dispatchHash"] ||
                row["actorId"] != source.Snapshot["courierId"] || row["courierId"] != row["actorId"] ||
                row["previousAttemptId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : new BsonString(CatalogRepository.Digest(previous))) ||
                previous is not null && (recorded < ShipmentReceiptRecords.Time(previous["recordedAt"]) || occurred < ShipmentReceiptRecords.Time(previous["occurredAt"])) ||
                row["bodyHash"] != AuthorizationStore.CommandHash(Body(shipmentId, sequence - 1, row["outcome"].AsString, occurred), row["reason"].AsString)) throw Invalid();
            var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            if (audit is null || !CatalogSchemas.SemanticEquals(audit, Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
                !CatalogSchemas.SemanticEquals(receipt.GetValue("result", BsonNull.Value), Result(row))) throw Invalid();
            foreach (var value in row["revokedChallengeIds"].AsBsonArray) {
                var id = value.AsInt64; if (!references.Add(id)) throw Invalid();
                var revoked = revocations.SingleOrDefault(item => item["_id"] == id);
                if (revoked is null || !CatalogSchemas.SemanticEquals(revoked, Revocation(row, id))) throw Invalid();
                var challenge = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
                if (challenge["shipmentId"] != shipmentId || challenge["courierId"] != row["courierId"] || !challenge["consumedReceiptId"].IsBsonNull ||
                    ShipmentReceiptRecords.Time(challenge["recordedAt"]) > recorded || ShipmentReceiptRecords.Time(challenge["expiresAt"]) <= recorded) throw Invalid();
                await new ShipmentReceiptRecords(database).VerifyEffectsAsync(tx, challenge, ShipmentReceiptRecords.Issue, ct);
            }
            previous = row;
        }
        if (references.Count != revocations.Count) throw Invalid();
        // Reverse accepted effects detect missing roots, including a missing last history item.
        var acceptedIds = rows.Select(row => row["_id"]).ToArray();
        foreach (var (name, operationField, sourceField) in new[] { ("auditEvents", "operation", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") }) {
            var effects = await Rows(name).Find(tx, new BsonDocument { { operationField, Operation }, { sourceField + ".shipmentId", shipmentId } }).Limit(DeliveryAttemptPolicy.MaximumAttempts + 1).ToListAsync(ct);
            if (effects.Any(effect => !effect.Contains(sourceField) || !effect[sourceField].IsBsonDocument || !acceptedIds.Contains(effect[sourceField].AsBsonDocument.GetValue("attemptId", BsonNull.Value)))) throw Invalid();
        }
        return rows;
    }
    internal async Task RequireUsableChallengeAsync(IClientSessionHandle tx, ShipmentDocumentSource source, long challengeId, CancellationToken ct) {
        await HistoryAsync(tx, source, ct);
        if (await Rows(Revocations).Find(tx, new BsonDocument("_id", challengeId)).AnyAsync(ct)) throw new AuthorizationDeniedException("receipt_challenge_revoked");
    }
    internal static AuthorizationDeniedException Invalid() => new("delivery_attempt_unavailable");
}
