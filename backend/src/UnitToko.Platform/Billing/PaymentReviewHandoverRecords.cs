using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentReviewHandoverRecords(IMongoDatabase database)
{
    internal const string Collection = "paymentReviewHandovers";
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static Task StorageAsync(IMongoDatabase database, CancellationToken ct) => InventoryRepository.RequireStorageAsync(database,
        Collection, PaymentReviewHandoverSchemas.Validator(), PaymentReviewHandoverSchemas.Keys().Where(key => key.Unique).Select(key => key.Key).ToArray(), ct);
    internal static object Body(long id, long revision, PaymentReviewActionInput input, PaymentReviewHandoverInput? handover) =>
        handover is null ? W.Body(id, revision, input) : new { command = W.Body(id, revision, input), handover };
    internal static PaymentReviewHandoverInput Input(BsonDocument row) => PaymentReviewHandoverPolicy.Copy(new(row["delegationId"].AsInt64,
        row["delegationRevision"].AsInt64, row["delegationEventId"].AsInt64, row["originalAssigneeId"].AsInt64, row["declarationRevision"].AsInt64));
    internal static bool Matches(BsonDocument head, BsonDocument source, long assignee) {
        var request = R.Request(head["request"]);
        return head["approvedBy"].AsInt64 > 0 && (assignee == head["sourceUserId"].AsInt64 || assignee == request.BeneficiaryUserId) &&
            request.Permissions.Contains(Permissions.PaymentReviewManage) &&
            PaymentDraftBson.Scopes(source["scopes"].AsBsonArray).All(new ScopeClause(1, 1, assignee, request.Scope, []).Matches);
    }
    internal static string State(BsonDocument head, DateTimeOffset now) => head["status"] == "revoked" ? "revoked" :
        now >= R.Time(head["request"]["expiresAt"]) ? "expired" : now < R.Time(head["request"]["effectiveAt"]) ? "not_yet_effective" : "active";
    internal async Task<BsonDocument> CaptureAsync(IClientSessionHandle tx, BsonDocument source, BsonDocument? previous,
        PaymentReviewHandoverInput input, long actorId, CancellationToken ct) {
        _ = PaymentReviewHandoverPolicy.Copy(input);
        if (previous is null || previous["state"] != "assigned" || previous["assigneeId"] != input.OriginalAssigneeId ||
            input.OriginalAssigneeId == actorId || source["revision"] != input.DeclarationRevision) throw B.Changed();
        var head = await R.HeadAsync(database, tx, input.DelegationId, ct);
        if (head["revision"] != input.DelegationRevision || head["eventId"] != input.DelegationEventId ||
            !Matches(head, source, input.OriginalAssigneeId)) throw B.Changed();
        // The delegation may be expired, revoked or have an unavailable source. The payment owner
        // independently authorizes the current claimant; historical delegation is only the handover basis.
        await R.ExactAsync(database, tx, R.Heads, head, ct);
        await R.ExactAsync(database, tx, W.Versions, previous, ct); return head;
    }
    internal async Task RecordAsync(IClientSessionHandle tx, BsonDocument action, BsonDocument previous,
        BsonDocument delegation, PaymentReviewHandoverInput input, CancellationToken ct) {
        var row = new BsonDocument { { "_id", action["_id"] }, { "schemaVersion", 1 }, { "declarationId", action["declarationId"] },
            { "declarationRevision", input.DeclarationRevision }, { "submissionId", action["source"]["submissionId"] },
            { "sourceHash", B.Hash(action["source"].AsBsonDocument) }, { "reviewRevision", action["revision"] },
            { "previousActionId", previous["_id"] }, { "previousActionHash", previous["snapshotHash"] },
            { "delegationId", input.DelegationId }, { "delegationRevision", input.DelegationRevision }, { "delegationEventId", input.DelegationEventId },
            { "originalAssigneeId", input.OriginalAssigneeId }, { "actorId", action["actorId"] }, { "operation", W.Operation },
            { "requestKey", action["requestKey"] }, { "bodyHash", action["bodyHash"] }, { "actionHash", action["snapshotHash"] },
            { "auditId", action["auditId"] }, { "delegationState", State(delegation, R.Time(action["recordedAt"])) },
            { "recordedAt", action["recordedAt"] } };
        row.Add("snapshotHash", B.Hash(row)); await Rows(Collection).InsertOneAsync(tx, row, cancellationToken: ct);
    }
    internal async Task<PaymentReviewHandoverInput?> ReadAsync(IClientSessionHandle tx, BsonDocument action, BsonDocument? previous, CancellationToken ct) {
        var raw = await Rows(Collection).Find(tx, new BsonDocument("_id", action["_id"])).SingleOrDefaultAsync(ct);
        if (raw is null) return null; // The owner's recomputed body hash below makes removal fail closed.
        var row = await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.And(new BsonDocument("_id", action["_id"]),
            PaymentReviewHandoverSchemas.Validator())).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (row != raw || row["snapshotHash"] != B.Hash(row) || previous is null || action["action"] != "claim" || action["state"] != "assigned" ||
            action["assigneeId"] != action["actorId"] || previous["state"] != "assigned" || previous["assigneeId"] != row["originalAssigneeId"] ||
            action["actorId"] == row["originalAssigneeId"] || row["previousActionId"] != previous["_id"] || row["previousActionHash"] != previous["snapshotHash"] ||
            row["sourceHash"] != B.Hash(action["source"].AsBsonDocument) || row["declarationRevision"] != action["source"]["revision"] ||
            row["submissionId"] != action["source"]["submissionId"] || row["reviewRevision"] != action["revision"] ||
            row["actionHash"] != action["snapshotHash"] || row["declarationId"] != action["declarationId"] ||
            new[] { "actorId", "requestKey", "bodyHash", "auditId", "recordedAt" }.Any(field => row[field] != action[field])) throw B.Broken();
        var input = Input(row); var head = await R.HeadAsync(database, tx, input.DelegationId, ct);
        var ev = await R.EventAsync(database, tx, input.DelegationEventId, ct); var at = ev["after"].AsBsonDocument;
        if (ev["delegationId"] != input.DelegationId || ev["revision"] != input.DelegationRevision || head["revision"].AsInt64 < input.DelegationRevision ||
            ev["recordedAt"].CompareTo(row["recordedAt"]) > 0 || !Matches(at, action["source"].AsBsonDocument, input.OriginalAssigneeId) ||
            row["delegationState"] != State(at, R.Time(row["recordedAt"]))) throw B.Broken();
        await R.ExactAsync(database, tx, Collection, row, ct); return input;
    }
    internal async Task RequireNoOrphansAsync(IClientSessionHandle tx, long declarationId, IReadOnlyCollection<long> actionIds, CancellationToken ct) {
        var ids = await Rows(Collection).Find(tx, new BsonDocument("declarationId", declarationId)).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (ids.Any(row => !actionIds.Contains(row["_id"].AsInt64))) throw B.Broken();
        // An orphan audit has no requestKey in the existing format. Match its immutable owner
        // result, so deletion of all other key-bearing rows cannot become "not confirmed".
        foreach (var (name, field) in new[] {("idempotencyRecords", "result"), ("auditEvents", "result"), ("outboxEvents", "payload")}) {
            var rows = await Rows(name).Find(tx, new BsonDocument {{name == "idempotencyRecords" ? "operation" : "kind", W.Operation},
                {field + ".declarationId", declarationId}}).Project(new BsonDocument(field + ".actionId", 1)).ToListAsync(ct);
            if (rows.Any(row => !row.TryGetValue(field, out var owner) || !owner.IsBsonDocument || !owner.AsBsonDocument.TryGetValue("actionId", out var id) || !id.IsInt64 || !actionIds.Contains(id.AsInt64))) throw B.Broken();
        }
    }
}
