using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementSettlementBson
{
    internal const string Events = "procurementSettlements", Policies = "procurementSettlementPolicies", Operation = "procurement.settlement.verify", Prefix = "procurement.settlement.";
    internal const string ClaimOwner = "procurementDisbursementSettlement";
    internal static string Hash(BsonDocument value) => BankReceiptBson.Hash(value);
    internal static BsonDocument Snapshot(BsonDocument value) => BankReceiptBson.Snapshot(value);
    internal static BsonDocument Input(ProcurementSettlementInput value) {
        value = ProcurementSettlementRules.Copy(value);
        return new() { { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision }, { "bankImportId", value.BankImportId }, { "bankRowNumber", value.BankRowNumber },
            { "observedBeneficiary", ReceivingAccountBson.Details(value.ObservedBeneficiary) }, { "beneficiaryVerificationReference", value.BeneficiaryVerificationReference }, { "transferVerificationReference", value.TransferVerificationReference } };
    }
    internal static ProcurementSettlementInput Input(BsonValue value) => ProcurementSettlementRules.Copy(new(value["policyId"].AsInt64, value["policyRevision"].AsInt64,
        value["bankImportId"].AsInt64, value["bankRowNumber"].AsInt32, ReceivingAccountBson.Details(value["observedBeneficiary"]), value["beneficiaryVerificationReference"].AsString, value["transferVerificationReference"].AsString));
    internal static object Body(long disbursementId, long revision, ProcurementSettlementInput input, string binding, bool attested) => new { disbursementId, revision, input = ProcurementSettlementRules.Copy(input), binding, attested };
    internal static string Binding(long actor, long grantVersion, BsonDocument record, ProcurementSettlementInput input, BsonDocument debit, BsonDocument policy, BsonDocument accounting, string date) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "disbursementId", record["_id"] }, { "disbursementHash", record["snapshotHash"] },
        { "input", Input(input) }, { "debit", debit }, { "policy", Snapshot(policy) }, { "accounting", accounting }, { "postingDate", date } });
    internal static BsonDocument Result(BsonDocument row) => new() { { "id", row["_id"] }, { "disbursementId", row["disbursementId"] }, { "amount", row["amount"] },
        { "status", "bank_debit_verified" }, { "recordedAt", row["recordedAt"] }, { "postingDate", row["postingDate"] }, { "journalId", row["journalId"] } };
    internal static BsonDocument PolicyView(BsonDocument row) { var view = Snapshot(row); view.Remove("schemaVersion"); view.Remove("_id"); view.InsertAt(0, new("id", row["_id"])); return view; }
    internal static BsonDocument DebitView(BsonValue row, BsonValue reference) => new() { { "importId", row["importId"] }, { "rowNumber", row["rowNumber"] }, { "transactionId", row["transactionId"] },
        { "bookedDate", row["bookedDate"] }, { "amount", InvoiceBson.Money(row["amount"]).ToString() }, { "direction", "debit" }, { "reference", reference } };
    internal static BsonDocument DisbursementView(BsonDocument row) => new() {
        { "id", row["_id"] }, { "version", 1L }, { "draftId", row["draftId"] }, { "location", row["location"] }, { "authorization", ProcurementDisbursementBson.AuthorizationView(row["source"]["funding"].AsBsonDocument) },
        { "amount", row["input"]["amount"] }, { "transferDate", row["input"]["transferDate"] }, { "transferReference", row["input"]["transferReference"] }, { "recordedAt", row["recordedAt"] },
        { "beneficiary", ProcurementDisbursementBson.BeneficiaryView(row["source"]["beneficiary"].AsBsonDocument) }, { "payingAccount", row["source"]["payingAccount"] },
        { "original", new BsonDocument { { "draftId", row["draftId"] }, { "draftRevision", 1L }, { "reservationId", row["reservationId"] }, { "reference", row["evidence"] } } } };
    internal static BsonDocument View(BsonDocument row) { var view = Result(row); view.Add("debit", DebitView(row["debit"], row["debitReference"])); view.Add("policy", PolicyView(row["policy"].AsBsonDocument));
        view.Add("accounting", row["accounting"]); foreach (var field in new[] { "beneficiaryVerificationReference", "transferVerificationReference" }) view.Add(field, row["input"][field]); view.Add("reason", row["reason"]); return view; }
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "location", row["location"] }, { "disbursementId", row["disbursementId"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static BsonDocument Claim(BsonDocument row) {
        if (row["snapshotHash"] != Hash(row) || row["postingFence"] != 1L || row["journalId"].IsBsonNull || row["journalFingerprint"].IsBsonNull || row["operation"] != Operation) throw Broken();
        var debit = row["debit"];
        if (debit["snapshotHash"] != Hash(debit.AsBsonDocument) || row["canonicalIdentity"] != debit["canonicalIdentity"]) throw Broken();
        var claim = new BsonDocument { { "_id", row["canonicalIdentity"] }, { "schemaVersion", 2 }, { "canonicalIdentity", row["canonicalIdentity"] }, { "direction", "debit" }, { "ownerKind", ClaimOwner },
            { "ownerId", row["_id"] }, { "ownerHash", row["snapshotHash"] }, { "actorId", row["actorId"] }, { "recordedAt", row["recordedAt"] } };
        foreach (var name in new[] { "scope", "physicalIdentity", "identityNamespace", "transactionId", "bookedDate", "amount", "importId", "rowNumber", "importHash", "rowHash" }) claim.Add(name, debit[name].DeepClone());
        claim.Add("snapshotHash", Hash(claim)); return claim;
    }
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        if (!policy["active"].AsBoolean || policy["rule"] != "independent_exact_bank_debit" || policy["currency"] != "IDR" ||
            policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime) throw Changed("procurement_settlement_policy_unavailable");
        if (!policy["allowReferenceOnly"].AsBoolean) throw Changed("procurement_settlement_bank_evidence_required");
        foreach (var name in new[] { "bankDebitVerificationProcedureReference", "beneficiaryVerificationProcedureReference", "trustedBankChannelReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[name].AsString, 256)) throw Broken();
    }
    internal static AuthorizationDeniedException Broken() => new("procurement_settlement_integrity_unavailable");
    internal static PersistenceConflictException Changed(string code = "procurement_settlement_changed") => new(code, "Reload the exact recorded payment, bank debit and approved accounting policy");
}
