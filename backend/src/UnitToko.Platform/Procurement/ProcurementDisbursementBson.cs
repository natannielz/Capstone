using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using F = UnitToko.Platform.Procurement.ProcurementFundingBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementDisbursementBson
{
    internal const string Policies = "procurementDisbursementPolicies", Drafts = "procurementDisbursementDrafts", Records = "procurementDisbursementRecords",
        Heads = "procurementDisbursementHeads", Allocations = "procurementDisbursementAllocations", Prefix = "procurement.disbursement.", DraftOperation = Prefix + "draft", RecordOperation = Prefix + "record";
    internal static string Hash(BsonDocument row) => P.Hash(row);
    internal static string DraftHash(BsonDocument row) => Hash(new BsonDocument(row.Where(field => field.Name != "recordedDisbursementId")));
    internal static BsonDocument Input(ProcurementDisbursementInput input) { input = ProcurementDisbursementRules.Copy(input); return new() {
        { "location", R.Location(input.Location) }, { "fundingRequestId", input.FundingRequestId }, { "fundingDecisionId", input.FundingDecisionId }, { "authorizationId", input.AuthorizationId },
        { "supplierId", input.SupplierId }, { "beneficiaryVersionId", input.BeneficiaryVersionId }, { "beneficiaryVersion", input.BeneficiaryVersion },
        { "payingAccountId", input.PayingAccountId }, { "payingAccountVersionId", input.PayingAccountVersionId }, { "policyId", input.PolicyId }, { "policyRevision", input.PolicyRevision },
        { "amount", input.Amount.ToString() }, { "transferDate", P.Date(input.TransferDate) }, { "transferReference", input.TransferReference } }; }
    internal static ProcurementDisbursementInput Input(BsonValue row) => ProcurementDisbursementRules.Copy(new(R.Location(row["location"]), row["fundingRequestId"].AsInt64, row["fundingDecisionId"].AsInt64,
        row["authorizationId"].AsInt64, row["supplierId"].AsInt64, row["beneficiaryVersionId"].AsInt64, row["beneficiaryVersion"].AsInt64, row["payingAccountId"].AsInt64,
        row["payingAccountVersionId"].AsInt64, row["policyId"].AsInt64, row["policyRevision"].AsInt64, Money.Parse(row["amount"].AsString), P.Date(row["transferDate"]), row["transferReference"].AsString));
    internal static BsonDocument PolicySnapshot(BsonDocument value) => F.PolicySnapshot(value);
    internal static BsonDocument PolicyView(BsonDocument value) => F.PolicyView(value);
    internal static object DraftBody(ProcurementDisbursementInput input, string binding) => new { input = ProcurementDisbursementRules.Copy(input), binding };
    internal static object RecordBody(long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding) => new { draftId, expectedRevision, reservationId, evidence = reference, binding };
    internal static string Binding(long actor, long grant, ProcurementDisbursementInput input, BsonDocument source, BsonDocument capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grant }, { "input", Input(input) }, { "source", source }, { "capacity", capacity } });
    internal static string RecordBinding(long actor, long grant, string draftHash, long reservationId, EvidenceRef reference, BsonDocument capacity) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grant }, { "draftHash", draftHash }, { "revision", 1L }, { "reservationId", reservationId }, { "evidence", PaymentSubmissionBson.Reference(reference) }, { "capacity", capacity } });
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementDisbursementRules.Invalid(); }
    internal static BsonDocument Result(BsonDocument row) => new() { { "draftId", row["draftId"] }, { "draftVersion", 1L }, { "authorizationId", row["authorizationId"] },
        { "disbursementId", row["operation"] == RecordOperation ? row["_id"] : BsonNull.Value }, { "amount", row["input"]["amount"] }, { "status", row["operation"] == RecordOperation ? "recorded_unverified" : "draft" } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "location", row["location"] }, { "authorizationId", row["authorizationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static BsonDocument AuthorizationView(BsonDocument source) {
        var request = source["request"]; var input = request["input"]; var order = request["orderSource"]; var decision = source["decision"];
        return new() { { "id", source["authorization"]["_id"] }, { "requestId", request["_id"] }, { "decisionId", decision["_id"] }, { "orderId", input["orderId"] },
            { "orderVersion", input["orderVersion"] }, { "orderApprovalId", input["orderApprovalId"] }, { "route", order["request"]["input"]["route"] },
            { "supplier", P.SupplierView(order["proposal"]["supplier"].AsBsonDocument) }, { "authorizedAmount", decision["authorizedAmount"] },
            { "businessDate", input["businessDate"] }, { "neededBy", input["neededBy"] }, { "approvedAt", decision["recordedAt"] } };
    }
    internal static BsonDocument BeneficiaryView(BsonDocument source) => new() { { "supplierId", source["supplierId"] }, { "beneficiaryId", source["beneficiaryId"] }, { "versionId", source["versionId"] }, { "version", source["version"] },
        { "bankCode", source["details"]["bankCode"] }, { "maskedAccount", UnitToko.Modules.Billing.ReceivingAccountPolicy.Mask(ReceivingAccountBson.Details(source["details"])) }, { "currency", source["details"]["currency"] } };
    internal static BsonDocument BankSnapshot(RefundPayoutBankSource source) => new() {
        { "accountId", source.Account["accountId"] }, { "versionId", source.Account["_id"] }, { "headHash", BankReceiptBson.Hash(source.AccountHead) }, { "versionHash", source.Account["snapshotHash"] },
        { "physicalIdentity", source.Account["physicalIdentity"] }, { "identityNamespace", source.IdentityNamespace }, { "anchorHash", source.Anchor["snapshotHash"] },
        { "firstReceiptId", source.FirstReceipt["_id"] }, { "firstReceiptHash", source.FirstReceipt["snapshotHash"] }, { "cashJournalId", source.CashJournal["_id"] }, { "cashJournalFingerprint", source.CashJournal["fingerprint"] },
        { "scope", source.Scope.DeepClone() }, { "book", source.Book.DeepClone() }, { "cashAccountId", source.CashAccount["accountId"] }, { "cashAccountCode", source.CashAccount["accountCode"] }, { "currency", source.Account["details"]["currency"] } };
    internal static BsonDocument AccountView(BsonDocument account, string bankName) => new() { { "id", account["accountId"] }, { "versionId", account["_id"] }, { "version", account["version"] },
        { "bankCode", account["details"]["bankCode"] }, { "bankName", bankName }, { "maskedAccount", UnitToko.Modules.Billing.ReceivingAccountPolicy.Mask(ReceivingAccountBson.Details(account["details"])) }, { "currency", account["details"]["currency"] } };
    internal static PersistenceConflictException Broken() => new("procurement_disbursement_integrity_unavailable", "The complete disbursement evidence and authorization history is unavailable");
    internal static PersistenceConflictException Changed(string code = "procurement_disbursement_changed") => new(code, "Reload the approved advance, verified accounts, policy and remaining amount");
}
