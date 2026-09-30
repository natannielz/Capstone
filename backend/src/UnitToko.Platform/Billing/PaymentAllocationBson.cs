using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;
internal static class PaymentAllocationBson
{
    internal const string Policies = "paymentAllocationPolicies", Events = "paymentAllocations", Receipts = "paymentReceiptBalances", Invoices = "paymentInvoiceBalances";
    internal const string Operation = "billing.payment.allocate", Prefix = "billing.payment.allocation.";
    internal static string Hash(BsonDocument row) => BankReceiptBson.Hash(row);
    internal static BsonDocument Snapshot(BsonDocument row) => BankReceiptBson.Snapshot(row);
    internal static PaymentPartyKey Party(BsonDocument row) => new(new(row["buyingOrganizationId"].AsInt64), new(row["fulfillmentOrganizationId"].AsInt64), new(row["shopId"].AsInt64), new(row["channelId"].AsInt64));
    internal static object Body(long verificationId, string binding, PaymentAllocationInput input) => new { verificationId, binding, input = PaymentAllocationPolicy.Copy(input) };
    internal static string Binding(BsonValue verificationHash, BsonValue receiptHash, BsonDocument policy, BsonDocument receiptBefore, BsonArray lines) => Hash(new() {
        { "verificationHash", verificationHash }, { "receiptHash", receiptHash }, { "policy", Snapshot(policy) }, { "receiptBefore", receiptBefore },
        { "lines", new BsonArray(lines.Select(line => new BsonDocument { { "invoiceId", line["invoiceId"] }, { "source", line["source"] }, { "before", line["before"] }, { "amount", InvoiceBson.Money(line["amount"]).ToString() } })) } });
    internal static PaymentAllocationInput Input(BsonDocument row) => new(row["receiptBefore"]["version"].AsInt64,
        [.. row["lines"].AsBsonArray.Select(line => new PaymentAllocationLine(new(line["invoiceId"].AsInt64), line["before"]["version"].AsInt64, InvoiceBson.Money(line["amount"])))]);
    internal static BsonDocument Result(BsonDocument row) => new() { { "allocationId", row["_id"] }, { "verificationId", row["verificationId"] }, { "receiptId", row["receiptId"] },
        { "receiptVersion", row["receiptVersion"] }, { "amount", row["amount"] }, { "unapplied", row["unapplied"] }, { "recordedAt", row["recordedAt"] }, { "operation", Operation },
        { "invoices", new BsonArray(row["lines"].AsBsonArray.Select(line => new BsonDocument { { "invoiceId", line["invoiceId"] }, { "version", line["version"] }, { "amount", line["amount"] }, { "remaining", line["remaining"] } })) } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static AuthorizationDeniedException Broken() => new("payment_allocation_unavailable");
    internal static PersistenceConflictException Changed(string code = "payment_allocation_stale") => new(code, "Reload the canonical receipt and invoice balances");
}
