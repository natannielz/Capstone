using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.RefundPayoutBson;

namespace UnitToko.Platform.Billing;

internal sealed class RefundPayoutRecords(IMongoDatabase database, IClock clock, MongoUnitOfWork unit)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!InvoiceRecords.Equal(actual, expected)) throw Broken(); }
    internal static PersistenceConflictException Broken() => new("refund_payout_integrity_unavailable", "The exact payout history is unavailable");
    internal static PersistenceConflictException Changed() => new("refund_payout_changed", "Read the current payout before continuing");
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await Rows(name).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), RefundPayoutSchemas.Validator(name) })).SingleOrDefaultAsync(ct) ?? throw Broken();
    internal async Task TouchAsync(IClientSessionHandle tx, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }
    internal async Task<BsonDocument> DraftAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Drafts, id, ct);
        Equal(row["snapshotHash"], B.DraftHash(row));
        CheckSource(row, row["createdAt"]);
        Equal(row["operation"], B.DraftOperation); Equal(row["revision"], 1L);
        Equal(row["binding"], B.Binding(row["source"].AsBsonDocument, B.Input(row["input"].AsBsonDocument)));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.DraftBody(row["financialApprovalId"].AsInt64, row["binding"].AsString, B.Input(row["input"].AsBsonDocument)), row["reason"].AsString));
        CheckPeople(row, "userId");
        await EffectsAsync(tx, row, B.DraftOperation, B.DraftResult(row), B.DraftAudit(row), row["userId"].AsInt64, row["createdAt"], false, ct);
        if (!row["recordedPayoutId"].IsBsonNull) {
            var payout = await RowAsync(tx, B.Payouts, row["recordedPayoutId"].AsInt64, ct);
            Equal(payout["draftId"], row["_id"]); Equal(payout["draftHash"], row["snapshotHash"]); Equal(payout["snapshotHash"], B.Hash(payout));
        }
        return row;
    }
    internal async Task<BsonDocument> PayoutAsync(IClientSessionHandle tx, long id, bool pending, CancellationToken ct) {
        var row = await RowAsync(tx, B.Payouts, id, ct);
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["operation"], B.Operation); Equal(row["state"], "recorded_unverified");
        CheckSource(row, row["recordedAt"]); CheckPeople(row, "actorId"); Equal(row["recordedBy"], row["actorId"]);
        var draft = await DraftAsync(tx, row["draftId"].AsInt64, ct);
        Equal(draft["snapshotHash"], row["draftHash"]); Equal(draft["userId"], row["actorId"]);
        if (!pending || !draft["recordedPayoutId"].IsBsonNull) Equal(draft["recordedPayoutId"], row["_id"]);
        foreach (var field in new[] { "financialApprovalId", "approvalHash", "caseId", "returnId", "invoiceId", "party", "scope", "scopes", "source", "input" }) Equal(row[field], draft[field]);
        if (draft["createdAt"].CompareTo(row["recordedAt"]) > 0) throw Broken();
        var reference = PaymentSubmissionBson.Reference(row["evidence"].AsBsonDocument);
        Equal(row["draftRevision"], 1L); Equal(row["binding"], B.RecordBinding(draft["snapshotHash"].AsString, 1, row["reservationId"].AsInt64, reference));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.RecordBody(row["draftId"].AsInt64, 1, row["reservationId"].AsInt64, reference, row["binding"].AsString), row["reason"].AsString));
        await EffectsAsync(tx, row, B.Operation, B.Result(row), B.Audit(row), row["actorId"].AsInt64, row["recordedAt"], pending, ct);
        if (!pending) {
            var pin = await EvidencePinRecords.ReadAsync(database, tx, row["pinId"].AsInt64, ct);
            await new EvidenceService(database, unit, new(database), clock).ValidateHistoricalPinAsync(tx, pin, ct);
            foreach (var (field, expected) in new (string, BsonValue)[] { ("associationKind", "RefundPayout"), ("associationId", row["_id"]), ("refundPayoutDraftId", row["draftId"]),
                ("refundPayoutDraftRevision", 1L), ("reference", row["evidence"]), ("reservationId", row["reservationId"]), ("actorId", row["actorId"]),
                ("actorGrantVersion", row["actorGrantVersion"]), ("associationHash", row["snapshotHash"]), ("createdAt", row["recordedAt"]), ("reason", row["reason"]), ("correlationId", row["correlationId"]) }) Equal(pin[field], expected);
        }
        return row;
    }
    private void CheckSource(BsonDocument row, BsonValue time) {
        var source = row["source"].AsBsonDocument; var approval = source["approval"].AsBsonDocument;
        foreach (var field in new[] { "caseId", "returnId", "invoiceId", "party" }) Equal(row[field], approval[field]);
        Equal(row["financialApprovalId"], approval["id"]); Equal(row["approvalHash"], approval["hash"]);
        Equal(row["input"], B.Input(B.Input(row["input"].AsBsonDocument)));
        if (!time.IsBsonDateTime || time.ToUniversalTime() > clock.UtcNow.UtcDateTime || approval["recordedAt"].CompareTo(time) > 0) throw Broken();
        var at = new DateTimeOffset(time.ToUniversalTime());
        B.PolicyAt(source["policy"].AsBsonDocument, at); B.BeneficiaryAt(source["beneficiary"].AsBsonDocument, at);
        _ = B.Review(source, B.Input(row["input"].AsBsonDocument), AccountingCalendar.BusinessDate(at));
    }
    private static void CheckPeople(BsonDocument row, string actorField) {
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        var approver = row["source"]["approval"]["approvedBy"].AsInt64; var actor = row[actorField].AsInt64;
        if (people.Length != 2 || !people.Select(p => p.UserId).Order().SequenceEqual(new[] { actor, approver }.Distinct().Order()) ||
            people.Any(p => p.Verification != PersonVerification.Verified || p.PersonId is null) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(p => p.UserId == actor), people.Single(p => p.UserId == approver))) throw Broken();
        Equal(people.Single(p => p.UserId == approver).PersonId ?? throw Broken(), row["source"]["approval"]["approvedByPersonId"]);
        if (actorField == "userId") Equal(row["personId"], people.Single(p => p.UserId == actor).PersonId ?? throw Broken());
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, string operation, BsonDocument result, BsonDocument audit, long actor, BsonValue time, bool pending, CancellationToken ct) {
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(time) > 0) throw Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]);
        Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.DisbursementRecord, new(1, 1, ApprovedBy: row["source"]["approval"]["approvedBy"].AsInt64)));
        if (pending) { if (receipt["result"].AsBsonDocument.ElementCount != 0) throw Broken(); }
        else Equal(receipt["result"], result);
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw Broken(), audit);
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", operation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct) ?? throw Broken();
        foreach (var (field, expected) in new (string, BsonValue)[] { ("kind", operation), ("actorId", actor), ("requestKey", row["requestKey"]), ("payload", result), ("createdAt", time) }) Equal(effect[field], expected);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var draft = operation == B.DraftOperation; var field = draft ? "userId" : "actorId";
        var row = await Rows(draft ? B.Drafts : B.Payouts).Find(tx, new BsonDocument { { field, actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return draft ? await DraftAsync(tx, row["_id"].AsInt64, ct) : await PayoutAsync(tx, row["_id"].AsInt64, false, ct);
        foreach (var (table, kind) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(tx, new BsonDocument { { kind, operation }, { "actorId", actor }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw Broken();
        return null;
    }
}
