using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentManualReviewRecords(IMongoDatabase database)
{
    internal const string Policies = "paymentManualReviewPolicies", Versions = "paymentManualReviews", Operation = "billing.payment.review.manual";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Snapshot(BsonDocument row) => new(row.Where(e => e.Name != "fence"));
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(e => e.Name is not ("fence" or "snapshotHash"))));
    internal static string Binding(BsonDocument source, BsonDocument failure, BsonDocument policy, long revision) =>
        Hash(new() { { "source", source }, { "failure", failure }, { "policy", Snapshot(policy) }, { "revision", revision } });
    internal static object Body(long id, long revision, PaymentManualReviewInput input) => new { declarationId = id, expectedReviewRevision = revision, input };
    internal static BsonDocument Result(BsonDocument row) => new() { { "declarationId", row["declarationId"] }, { "submissionId", row["source"]["submissionId"] },
        { "reviewRevision", row["revision"] }, { "manualReviewId", row["_id"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Review(BsonDocument row) => new() { { "kind", "manual" }, { "revision", row["revision"] }, { "versionId", row["_id"] },
        { "versionHash", row["snapshotHash"] }, { "failureId", row["failure"]["failureId"] }, { "fields", row["fields"].DeepClone() } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "snapshotHash", row["snapshotHash"] }, { "bodyHash", row["bodyHash"] },
        { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at, string failure) {
        foreach (var field in new[] { "procedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[field].AsString, 256)) throw PaymentVerificationBson.Broken();
        if (!policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime ||
            !policy["allowedFailures"].AsBsonArray.Contains(failure)) throw PaymentVerificationBson.Changed("payment_manual_review_policy_unavailable");
    }
    internal async Task<BsonDocument> VersionAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(Versions).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & PaymentManualReviewSchemas.Validator(Versions)).SingleOrDefaultAsync(ct) ?? throw PaymentVerificationBson.Broken();
        var input = PaymentManualReviewPolicy.Copy(new(row["binding"].AsString, true, PaymentCorrectionBson.Fields(row["fields"].AsBsonArray)));
        PaymentVerificationRecords.Equal(row["snapshotHash"], Hash(row)); PaymentVerificationRecords.Equal(row["fields"], PaymentCorrectionBson.Fields(input.Fields));
        PaymentVerificationRecords.Equal(row["declarationId"], row["source"]["declarationId"]);
        if (row["binding"] != Binding(row["source"].AsBsonDocument, row["failure"].AsBsonDocument, row["policy"].AsBsonDocument, row["revision"].AsInt64 - 1) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(Body(row["declarationId"].AsInt64, row["revision"].AsInt64 - 1, input), row["reason"].AsString) ||
            row["failure"]["failedAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw PaymentVerificationBson.Broken();
        PolicyAt(row["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()), row["failure"]["safeFailure"].AsString);
        foreach (var name in new[] { "reason", "correlationId", "requestKey" }) if (!ReceivingAccountPolicy.Text(row[name].AsString, name == "reason" ? 1000 : 128)) throw PaymentVerificationBson.Broken();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(p => p.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(
            people.SingleOrDefault(p => p.UserId == row["actorId"].AsInt64), people.SingleOrDefault(p => p.UserId == row["source"]["recordedBy"].AsInt64))) throw PaymentVerificationBson.Broken();
        PaymentVerificationRecords.Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw PaymentVerificationBson.Broken(), Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw PaymentVerificationBson.Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw PaymentVerificationBson.Broken();
        PaymentVerificationRecords.Equal(receipt["bodyHash"], row["bodyHash"]); PaymentVerificationRecords.Equal(receipt["result"], Result(row));
        var revision = row["source"]["revision"].AsInt64;
        PaymentVerificationRecords.Equal(receipt.GetValue(PersonApprovalReceipt.Field, BsonNull.Value), PersonApprovalReceipt.Create(Permissions.PaymentReviewManual, new(revision, revision, RecordedBy: row["source"]["recordedBy"].AsInt64)));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + id)).SingleOrDefaultAsync(ct) ?? throw PaymentVerificationBson.Broken();
        PaymentVerificationRecords.Equal(effect["kind"], Operation); PaymentVerificationRecords.Equal(effect["actorId"], row["actorId"]);
        PaymentVerificationRecords.Equal(effect["requestKey"], row["requestKey"]); PaymentVerificationRecords.Equal(effect["payload"], Result(row)); PaymentVerificationRecords.Equal(effect["createdAt"], row["recordedAt"]);
        return row;
    }
    internal async Task<BsonDocument?> LatestAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        BsonDocument? previous = null; long revision = 0;
        using var cursor = await Rows(Versions).Find(tx, new BsonDocument("declarationId", source["declarationId"])).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            var row = await VersionAsync(tx, candidate["_id"].AsInt64, ct);
            PaymentVerificationRecords.Equal(row["source"], source);
            if (row["revision"] != ++revision || row["previousVersionId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["snapshotHash"]) || previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw PaymentVerificationBson.Broken();
            previous = row;
        }
        if (previous is null) foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "operation", Operation }, { "result.declarationId", source["declarationId"] } }),
            ("auditEvents", new() { { "kind", Operation }, { "result.declarationId", source["declarationId"] } }),
            ("outboxEvents", new() { { "kind", Operation }, { "payload.declarationId", source["declarationId"] } })
        }) if (await Rows(name).Find(tx, filter).AnyAsync(ct)) throw PaymentVerificationBson.Broken();
        return previous;
    }
    internal async Task TouchAsync(IClientSessionHandle tx, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(tx, row, new BsonDocument("$inc", new BsonDocument("fence", 1L)), cancellationToken: ct)).ModifiedCount != 1)
            throw PaymentVerificationBson.Changed();
    }
}
