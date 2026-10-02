using MongoDB.Bson;
using UnitToko.Modules.Billing;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;

namespace UnitToko.Platform.Billing;

internal static class RefundSettlementBson
{
    internal const string Events = "refundSettlements", Policies = "refundSettlementPolicies", Operation = "billing.refund.payout.verify";
    internal static string Hash(BsonDocument row) => B.Hash(row);
    internal static object Body(long payoutId, string binding, RefundSettlementInput input, bool attested) =>
        new { payoutId, binding, input = RefundSettlementPolicy.Copy(input), attested };
    internal static BsonDocument Input(RefundSettlementInput input) {
        input = RefundSettlementPolicy.Copy(input);
        return new() { { "bankImportId", input.BankImportId }, { "bankRowNumber", input.BankRowNumber },
            { "beneficiaryVerificationReference", input.BeneficiaryVerificationReference }, { "transferVerificationReference", input.TransferVerificationReference } };
    }
    internal static RefundSettlementInput Input(BsonDocument input) => RefundSettlementPolicy.Copy(new(input["bankImportId"].AsInt64,
        input["bankRowNumber"].AsInt32, input["beneficiaryVerificationReference"].AsString, input["transferVerificationReference"].AsString));
    internal static BsonArray Releases(BsonDocument approval) => new(A.Review(approval).Releases.OrderBy(line => line.OriginalAllocationId.Value).Select(line => new BsonDocument {
        { "originalAllocationId", line.OriginalAllocationId.Value }, { "receiptId", line.ReceiptId.Value },
        { "originallyAllocated", InvoiceBson.Money(line.OriginallyAllocated) }, { "previouslyReleased", InvoiceBson.Money(line.PreviouslyReleased) },
        { "amount", InvoiceBson.Money(line.Amount) }, { "releasedAfter", InvoiceBson.Money(line.ReleasedAfter) } }));
    internal static string Binding(BsonDocument payout, BsonDocument debit, BsonDocument policy, BsonArray receipts, BsonArray releases, RefundSettlementInput input) =>
        Hash(new() { { "payoutId", payout["_id"] }, { "payoutHash", payout["snapshotHash"] }, { "debit", debit },
            { "policy", B.Snapshot(policy) }, { "receipts", receipts }, { "releases", releases }, { "input", Input(input) } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "settlementId", row["_id"] }, { "payoutId", row["payoutId"] },
        { "financialApprovalId", row["financialApprovalId"] }, { "caseId", row["caseId"] }, { "returnId", row["returnId"] }, { "invoiceId", row["invoiceId"] },
        { "amount", row["amount"] }, { "operation", Operation }, { "state", "bank_debit_verified" }, { "recordedAt", row["recordedAt"] },
        { "receipts", new BsonArray(row["receipts"].AsBsonArray.Select(receipt => new BsonDocument { { "receiptId", receipt["receiptId"] },
            { "version", receipt["version"] }, { "paid", receipt["paidAfter"] }, { "pending", receipt["pending"] },
            { "reservedForRefund", receipt["before"]["reservedForRefund"] }, { "available", receipt["available"] } })) } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        if (policy["rule"] != "independent_exact_bank_debit" || !policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime)
            throw B.Changed("refund_settlement_policy_unavailable");
        foreach (var name in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[name].AsString, 256)) throw B.Broken();
    }
}
