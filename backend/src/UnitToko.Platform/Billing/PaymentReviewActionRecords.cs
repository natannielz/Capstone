using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentReviewActionRecords(IMongoDatabase database)
{
    internal const string Versions = "paymentReviewActions", Operation = "billing.payment.review.manage";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static string Binding(BsonDocument source, long revision) => B.Hash(new() { { "source", source }, { "revision", revision } });
    internal static object Body(long id, long revision, PaymentReviewActionInput input) => input.PublicExplanation is null
        ? new { declarationId = id, expectedReviewRevision = revision, input = new { input.Binding, input.Action } }
        : new { declarationId = id, expectedReviewRevision = revision, input };
    internal static BsonDocument Result(BsonDocument row) => new() { { "declarationId", row["declarationId"] }, { "submissionId", row["source"]["submissionId"] },
        { "reviewRevision", row["revision"] }, { "actionId", row["_id"] }, { "action", row["action"] }, { "state", row["state"] },
        { "assigneeId", row["assigneeId"] }, { "recordedAt", row["recordedAt"] }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "snapshotHash", row["snapshotHash"] }, { "bodyHash", row["bodyHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal async Task<BsonDocument?> LatestAsync(IClientSessionHandle tx, long id, BsonDocument? source, CancellationToken ct) {
        BsonDocument? previous = null; long revision = 0; string state = "pending"; long? assignee = null;
        // ponytail: validate the full linear history; add verified checkpoints only if measured review volume requires them.
        using var cursor = await Rows(Versions).Find(tx, new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            var row = await Rows(Versions).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", candidate["_id"]) & PaymentReviewExplanationSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            PaymentVerificationRecords.Equal(row["snapshotHash"], B.Hash(row));
            PaymentVerificationRecords.Equal(row["source"]["declarationId"], new BsonInt64(id));
            if (source is not null) PaymentVerificationRecords.Equal(row["source"], source);
            if (previous is not null) PaymentVerificationRecords.Equal(row["source"], previous["source"]);
            if (row["revision"] != ++revision || row["previousVersionId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["snapshotHash"]) || previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw B.Broken();
            var input = PaymentReviewActionPolicy.Copy(new(row["binding"].AsString, row["action"].AsString, row["schemaVersion"] == 2 ? row["publicExplanation"].AsString : null));
            if (input.Binding != Binding(row["source"].AsBsonDocument, revision - 1) || row["bodyHash"] != AuthorizationStore.CommandHash(Body(id, revision - 1, input), row["reason"].AsString)) throw B.Broken();
            var expected = PaymentReviewActionPolicy.Apply(state, assignee, input.Action, row["actorId"].AsInt64);
            if (row["state"] != expected.State || row["assigneeId"] != (expected.AssigneeId is { } user ? new BsonInt64(user) : BsonNull.Value)) throw B.Broken();
            var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
            if (people.Select(p => p.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(people.SingleOrDefault(p => p.UserId == row["actorId"].AsInt64),
                people.SingleOrDefault(p => p.UserId == row["source"]["recordedBy"].AsInt64))) throw B.Broken();
            foreach (var field in new[] { "reason", "requestKey", "correlationId" }) if (!ReceivingAccountPolicy.Text(row[field].AsString, field == "reason" ? 1000 : 128)) throw B.Broken();
            PaymentVerificationRecords.Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), Audit(row));
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
            PaymentVerificationRecords.Equal(receipt["bodyHash"], row["bodyHash"]); PaymentVerificationRecords.Equal(receipt["result"], Result(row));
            var declaredRevision = row["source"]["revision"].AsInt64;
            PaymentVerificationRecords.Equal(receipt.GetValue(PersonApprovalReceipt.Field, BsonNull.Value), PersonApprovalReceipt.Create(Permissions.PaymentReviewManage, new(declaredRevision, declaredRevision, RecordedBy: row["source"]["recordedBy"].AsInt64)));
            var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            PaymentVerificationRecords.Equal(effect["kind"], Operation); PaymentVerificationRecords.Equal(effect["actorId"], row["actorId"]);
            PaymentVerificationRecords.Equal(effect["requestKey"], row["requestKey"]); PaymentVerificationRecords.Equal(effect["payload"], Result(row)); PaymentVerificationRecords.Equal(effect["createdAt"], row["recordedAt"]);
            previous = row; state = expected.State; assignee = expected.AssigneeId;
        }
        if (previous is null) foreach (var (name, field) in new[] { ("idempotencyRecords", "result.declarationId"), ("auditEvents", "result.declarationId"), ("outboxEvents", "payload.declarationId") })
            if (await Rows(name).Find(tx, new BsonDocument { { name == "idempotencyRecords" ? "operation" : "kind", Operation }, { field, id } }).AnyAsync(ct)) throw B.Broken();
        return previous;
    }
    internal async Task RequireOpenAsync(IClientSessionHandle tx, long id, BsonDocument? source, CancellationToken ct) {
        var row = await LatestAsync(tx, id, source, ct);
        if (row is not null && row["state"].AsString is "rejected" or "replacement_requested") throw B.Changed("payment_review_closed");
    }
}
