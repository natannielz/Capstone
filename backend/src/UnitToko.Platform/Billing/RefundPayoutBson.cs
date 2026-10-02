using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;
internal static class RefundPayoutBson
{
    internal const string Policies = "refundPayoutPolicies", Beneficiaries = "refundBeneficiaries", Drafts = "refundPayoutDrafts", Payouts = "refundPayouts";
    internal const string DraftOperation = "billing.refund.payout.draft", Operation = "billing.refund.payout.record";
    internal static string Hash(BsonDocument row) => B.Hash(row);
    internal static BsonDocument Snapshot(BsonDocument row) => B.Snapshot(row);
    internal static BsonDocument BeneficiarySnapshot(BsonDocument row) {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy;
    }
    internal static string DraftHash(BsonDocument row) {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("recordedPayoutId"); return Hash(copy);
    }
    internal static object DraftBody(long approvalId, string binding, RefundPayoutInput input) => new { approvalId, binding, input = RefundPayoutPolicy.Copy(input) };
    internal static object RecordBody(long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding) =>
        new { draftId, expectedRevision, reservationId, evidence = reference, binding };
    internal static BsonDocument Input(RefundPayoutInput input) {
        input = RefundPayoutPolicy.Copy(input);
        return new() { { "beneficiaryId", input.BeneficiaryId }, { "expectedBeneficiaryRevision", input.ExpectedBeneficiaryRevision },
            { "payingAccountId", input.PayingAccountId }, { "payingAccountVersionId", input.PayingAccountVersionId }, { "amount", InvoiceBson.Money(input.Amount) },
            { "transferDate", input.TransferDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, { "transferReference", input.TransferReference } };
    }
    internal static RefundPayoutInput Input(BsonDocument row) => RefundPayoutPolicy.Copy(new(row["beneficiaryId"].AsInt64, row["expectedBeneficiaryRevision"].AsInt64,
        row["payingAccountId"].AsInt64, row["payingAccountVersionId"].AsInt64, InvoiceBson.Money(row["amount"]), InvoiceBson.Date(row["transferDate"]), row["transferReference"].AsString));
    internal static string Binding(BsonDocument source, RefundPayoutInput input) => Hash(new() { { "source", source }, { "input", Input(input) } });
    internal static string RecordBinding(string draftHash, long revision, long reservationId, EvidenceRef reference) => Hash(new() {
        { "draftHash", draftHash }, { "revision", revision }, { "reservationId", reservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) } });
    internal static RefundPayoutResult Review(BsonDocument source, RefundPayoutInput input, DateOnly businessDate) {
        var approval = source["approval"]; var beneficiary = source["beneficiary"]; var paying = source["payingBank"]; var book = paying["book"];
        if (input.PayingAccountId != source["policy"]["payingAccountId"].AsInt64 || input.PayingAccountVersionId != source["policy"]["payingAccountVersionId"].AsInt64)
            throw Changed("refund_payout_bank_not_permitted");
        return RefundPayoutPolicy.Review(InvoiceBson.Money(approval["reservedRefund"]), B.Party(approval["party"].AsBsonDocument), paying["currency"].AsString,
            new(beneficiary["_id"].AsInt64, beneficiary["revision"].AsInt64, beneficiary["buyingOrganizationId"].AsInt64, beneficiary["details"]["currency"].AsString),
            new(paying["accountId"].AsInt64, paying["versionId"].AsInt64, new OperationalBookKey(book["organizationId"].AsInt64, book["shopId"].AsInt64, book["channelId"].AsInt64, book["currency"].AsString)),
            input, source["policy"]["rule"].AsString, businessDate);
    }
    internal static void PolicyAt(BsonDocument row, DateTimeOffset at) {
        if (row["rule"] != RefundPayoutPolicy.ExactReservedRefund || !row["active"].AsBoolean || row["approvedAt"].ToUniversalTime() > at.UtcDateTime || row["expiresAt"].ToUniversalTime() <= at.UtcDateTime)
            throw Changed("refund_payout_policy_unavailable");
        if (row["payingAccountId"].AsInt64 <= 0 || row["payingAccountVersionId"].AsInt64 <= 0) throw Broken();
        foreach (var field in new[] { "beneficiaryVerificationProcedureReference", "payoutRecordingProcedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(row[field].AsString, 256)) throw Broken();
    }
    internal static void BeneficiaryAt(BsonDocument row, DateTimeOffset at) {
        if (!row["active"].AsBoolean || row["approvedAt"].ToUniversalTime() > at.UtcDateTime || row["expiresAt"].ToUniversalTime() <= at.UtcDateTime)
            throw Changed("refund_beneficiary_unavailable");
        var details = ReceivingAccountBson.Details(row["details"]);
        if (row["physicalIdentity"] != ReceivingAccountBson.PhysicalIdentity(details) || row["snapshotHash"] != Hash(row) || row["method"] is not BsonString method || method.Value is not ("bank-contact" or "authenticated-bank-record")) throw Broken();
        foreach (var field in new[] { "authorizationReference", "trustedSourceReference", "trustedChannelReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(row[field].AsString, 256)) throw Broken();
        var users = new[] { row["proposedBy"].AsInt64, row["verifiedBy"].AsInt64, row["approvedBy"].AsInt64 }.Distinct().Order().ToArray();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Any(person => person.Verification != PersonVerification.Verified) || !people.Select(person => person.UserId).SequenceEqual(users)) throw Broken();
        ReceivingAccountPolicy.RequireIndependent(people.Single(person => person.UserId == row["approvedBy"].AsInt64),
            people.Single(person => person.UserId == row["verifiedBy"].AsInt64), people.Single(person => person.UserId == row["proposedBy"].AsInt64));
    }
    internal static BsonDocument DraftResult(BsonDocument row) => new() { { "draftId", row["_id"] }, { "revision", row["revision"] }, { "approvalId", row["financialApprovalId"] },
        { "state", "awaiting_private_evidence" }, { "amount", row["input"]["amount"] }, { "createdAt", row["createdAt"] } };
    internal static BsonDocument Result(BsonDocument row) => new() { { "payoutId", row["_id"] }, { "draftId", row["draftId"] }, { "approvalId", row["financialApprovalId"] },
        { "state", "recorded_unverified" }, { "amount", row["input"]["amount"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument DraftAudit(BsonDocument row) => Audit(row, true);
    internal static BsonDocument Audit(BsonDocument row) => Audit(row, false);
    private static BsonDocument Audit(BsonDocument row, bool draft) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", draft ? DraftOperation : Operation },
        { "operation", draft ? DraftOperation : Operation }, { "actorId", row[draft ? "userId" : "actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row[draft ? "createdAt" : "recordedAt"] }, { "result", draft ? DraftResult(row) : Result(row) } };
    internal static string SourceKey(BsonDocument row) => row["operation"].AsString + ":" + row["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static AuthorizationDeniedException Broken() => new("refund_payout_integrity_unavailable");
    internal static PersistenceConflictException Changed(string code = "refund_payout_stale") => new(code, "Reload the approved refund, beneficiary, bank account and exact evidence");
}
