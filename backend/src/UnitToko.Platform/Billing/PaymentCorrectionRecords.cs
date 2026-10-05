using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentCorrectionBson;

namespace UnitToko.Platform.Billing;

internal sealed record PaymentCorrectionHistory(BsonDocument? Head, ImmutableArray<BsonDocument> Recent);
internal sealed class PaymentCorrectionRecords(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<BsonDocument> VersionAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Versions).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & PaymentSupplementCorrectionSchemas.Validator(B.Versions)).SingleOrDefaultAsync(ct) ?? throw Bad();
        var fields = PaymentReviewCorrectionPolicy.Copy(B.Fields(row["fields"].AsBsonArray));
        var before = PaymentReviewCorrectionPolicy.Copy(B.Fields(row["beforeFields"].AsBsonArray));
        var binding = B.Binding(row["binding"].AsBsonDocument); PaymentReviewCorrectionPolicy.Validate(binding, row["revision"].AsInt64 - 1);
        var observations = B.Observations(row);
        if (!InvoiceRecords.Equal(row["fields"], B.Fields(fields)) || !InvoiceRecords.Equal(row["beforeFields"], B.Fields(before)) ||
            InvoiceRecords.Equal(row["fields"], row["beforeFields"]) || row["personAtCorrection"]["userId"] != row["recordedBy"] || row["snapshotHash"] != B.Hash(row) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(B.Body(row["declarationId"].AsInt64, row["revision"].AsInt64 - 1, binding, fields, observations), row["reason"].AsString)) throw Bad();
        if (observations is not null) {
            await PaymentSupplementCorrectionStorage.RequireAsync(database, B.Versions, ct, requireAnnotated: true);
            await new PaymentCorrectionSupplementRecords(database).RequireAsync(tx, row, ct);
        }
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["recordedBy"] }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var events = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).Limit(2).ToListAsync(ct);
        if (audit is null || !InvoiceRecords.Equal(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !InvoiceRecords.Equal(receipt.GetValue("result", BsonNull.Value), B.Result(row)) || events.Count != 1 || events[0].GetValue("kind", "") != B.Operation ||
            !InvoiceRecords.Equal(events[0].GetValue("payload", BsonNull.Value), B.Result(row)) || events[0].GetValue("createdAt", BsonNull.Value) != row["recordedAt"]) throw Bad();
        return row;
    }
    internal async Task<PaymentCorrectionHistory> HistoryAsync(IClientSessionHandle tx, long id, PaymentCorrectionBinding binding, BsonArray? original, CancellationToken ct) {
        var raw = await Rows(B.Heads).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        var head = raw is null ? null : await Rows(B.Heads).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & PaymentCorrectionSchemas.Validator(B.Heads)).SingleOrDefaultAsync(ct) ?? throw Bad();
        long revision = 0; BsonDocument? previous = null; var recent = new Queue<BsonDocument>();
        using var cursor = await Rows(B.Versions).Find(tx, new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            var row = await VersionAsync(tx, candidate["_id"].AsInt64, ct);
            if (head is null || row["revision"] != ++revision || !InvoiceRecords.Equal(row["binding"], B.Binding(binding)) ||
                row["previousVersionId"] != (previous is null ? BsonNull.Value : previous["_id"]) || row["previousHash"] != (previous is null ? BsonNull.Value : previous["snapshotHash"]) ||
                previous is not null && (row["recordedAt"].CompareTo(previous["recordedAt"]) < 0 || !InvoiceRecords.Equal(row["beforeFields"], previous["fields"])) ||
                previous is null && (row["recordedAt"] != head["createdAt"] || original is not null && !InvoiceRecords.Equal(row["beforeFields"], original))) throw Bad();
            previous = row; recent.Enqueue(row); if (recent.Count > 20) recent.Dequeue();
        }
        if (head is not null && (previous is null || head["revision"] != revision || head["versionId"] != previous["_id"] || head["updatedAt"] != previous["recordedAt"] ||
            !InvoiceRecords.Equal(head["binding"], B.Binding(binding)))) throw Bad();
        if (head is null) foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "operation", B.Operation }, { "result.declarationId", id } }),
            ("auditEvents", new() { { "kind", B.Operation }, { "result.declarationId", id } }),
            ("outboxEvents", new() { { "kind", B.Operation }, { "payload.declarationId", id } })
        }) if (await Rows(name).Find(tx, filter).Limit(1).AnyAsync(ct)) throw Bad();
        return new(head, [.. recent.Reverse()]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor }, { "operation", B.Operation }, { "key", key } }).SingleOrDefaultAsync(ct);
        var rows = await Rows(B.Versions).Find(tx, new BsonDocument { { "recordedBy", actor }, { "requestKey", key } }).Limit(2).ToListAsync(ct);
        if (receipt is null && rows.Count == 0) return null;
        if (receipt is null || rows.Count != 1) throw Bad();
        return await VersionAsync(tx, rows[0]["_id"].AsInt64, ct);
    }
    internal async Task TouchAsync(IClientSessionHandle tx, BsonDocument? head, CancellationToken ct) {
        if (head is not null && (await Rows(B.Heads).UpdateOneAsync(tx, head, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw PaymentDraftRecords.Changed();
    }
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Bad() => new("payment_review_integrity_unavailable", "The complete payment review history is unavailable");
}
