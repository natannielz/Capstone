using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

internal static class BankReceiptBson
{
    internal const string Policies = "bankReceiptPolicies", Accounts = "bankReceiptAccounts", Receipts = "bankReceipts", Confirmations = "bankReceiptConfirmations";
    internal const string Operation = "billing.bank-receipt.confirm", Prefix = "billing.bank-receipt.";
    internal static BsonDocument Snapshot(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); copy.Remove("snapshotHash"); return copy; }
    internal static string Hash(BsonDocument row) => BankStatementBson.Hash(Snapshot(row));
    internal static BankStatementRow Line(BsonDocument row) => new(row["number"].AsInt32, row["transactionId"].IsBsonNull ? null : row["transactionId"].AsString,
        InvoiceBson.Date(row["bookedDate"]), row["direction"].AsString, InvoiceBson.Money(row["amount"]), row["payerName"].IsBsonNull ? null : row["payerName"].AsString,
        row["reference"].IsBsonNull ? null : row["reference"].AsString, row["identityCandidate"].IsBsonNull ? null : row["identityCandidate"].AsString);
    internal static BsonDocument Observation(BankReceiptObservation value) => new() { { "accountNumber", value.AccountNumber }, { "transactionId", value.TransactionId }, { "bookedDate", InvoiceBson.Date(value.BookedDate) }, { "amount", InvoiceBson.Money(value.Amount) } };
    internal static BankReceiptObservation Observation(BsonValue value) => new(value["accountNumber"].AsString, value["transactionId"].AsString, InvoiceBson.Date(value["bookedDate"]), InvoiceBson.Money(value["amount"]));
    internal static object Body(long importId, int rowNumber, string binding, BankReceiptObservation observed, string sourceReference, bool attested) =>
        new { ImportId = importId, RowNumber = rowNumber, Binding = binding, AccountNumber = observed.AccountNumber, TransactionId = observed.TransactionId,
            BookedDate = InvoiceBson.Date(observed.BookedDate), Amount = observed.Amount.ToString(), SourceReference = sourceReference, Attested = attested };
    internal static object Body(BsonDocument row) => Body(row["importId"].AsInt64, row["rowNumber"].AsInt32, row["binding"].AsString, Observation(row["observed"]), row["sourceReference"].AsString, row["attested"].AsBoolean);
    internal static string Binding(BankStatementReceiptSource source, BsonDocument policy) => Hash(new() { { "importHash", source.Import["snapshotHash"] }, { "rowHash", Hash(source.Row) }, { "policyHash", Hash(policy) } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "confirmationId", row["_id"] }, { "receiptId", row["receiptId"] }, { "importId", row["importId"] },
        { "rowNumber", row["rowNumber"] }, { "createdReceipt", row["createdReceipt"] }, { "recordedAt", row["recordedAt"] }, { "operation", Operation } };
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64;
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "result", Result(row) }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] } };
    internal static AuthorizationDeniedException Broken() => new("bank_receipt_unavailable");
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Changed(string code = "bank_receipt_review_changed") => new(code, "Reload the independently reviewed bank source");
}
