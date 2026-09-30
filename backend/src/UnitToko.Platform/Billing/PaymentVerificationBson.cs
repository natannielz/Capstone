using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;

internal static class PaymentVerificationBson
{
    internal const string Policies = "paymentVerificationPolicies", Decisions = "paymentVerifications", Parties = "paymentReceiptParties";
    internal const string Operation = "billing.payment.verify", Prefix = "billing.payment.verification.";
    internal static string Hash(BsonDocument row) => BankReceiptBson.Hash(row);
    internal static BsonDocument Snapshot(BsonDocument row) => BankReceiptBson.Snapshot(row);
    internal static object Body(long id, long receiptId, PaymentVerificationInput input) => new { declarationId = id, receiptId, input = PaymentVerificationPolicy.Copy(input) };
    internal static PaymentVerificationInput Input(BsonDocument row) => new(row["binding"].AsString, row["payerVerificationReference"].AsString,
        row["imageReviewed"].AsBoolean, row["bankReviewed"].AsBoolean, row["payerVerified"].AsBoolean,
        [.. row["resolutions"].AsBsonArray.Select(item => new PaymentDiscrepancyResolution(item["field"].AsString, item["reason"].AsString))]);
    internal static BsonArray Resolutions(PaymentVerificationInput input) => new(input.Resolutions.Select(item => new BsonDocument { { "field", item.Field }, { "reason", item.Reason } }));
    internal static string Binding(BsonDocument source, BankReceiptPaymentSource bank, BsonDocument review, BsonDocument policy) => Hash(new() {
        { "declaration", source }, { "receiptId", bank.Receipt["_id"] }, { "receiptHash", bank.Receipt["snapshotHash"] }, { "review", review }, { "policy", Snapshot(policy) } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "verificationId", row["_id"] }, { "declarationId", row["declarationId"] },
        { "receiptId", row["receiptId"] }, { "reviewRevision", row["review"]["revision"] }, { "recordedAt", row["recordedAt"] }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64;
    internal static PersistenceConflictException Changed(string code = "payment_verification_changed") => new(code, "Reload the complete payment verification before confirming");
    internal static AuthorizationDeniedException Broken() => new("payment_verification_unavailable");
}
