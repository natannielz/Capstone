using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Fulfillment;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;
internal static class ReturnEntitlementBson
{
    internal const string Policies = "returnEntitlementPolicies", Requests = "returnEntitlementRequests", Pools = "invoiceReturnEntitlementPools", Operation = "billing.return.entitlement.request";
    internal static object Body(long caseId, long returnId, string action, long version, string binding, ReturnEntitlementInput input) =>
        new { caseId, returnId, action, expectedVersion = version, binding, input = ReturnEntitlementPolicy.Copy(input) };
    internal static BsonDocument Source(FinancialReturnSource returned, FinancialInvoiceSource invoice) {
        var row = invoice.Invoice;
        if (returned.Root["source"]["invoiceId"] != row["_id"] || returned.Root["source"]["invoiceHash"] != row["snapshotHash"]) throw B.Broken();
        return new() { { "returnId", returned.Receipt["_id"] }, { "caseId", returned.Root["_id"] }, { "invoiceId", row["_id"] },
            { "caseHash", returned.Root["snapshotHash"] }, { "custodyHash", returned.Receipt["snapshotHash"] }, { "inspectionHash", returned.Inspection["snapshotHash"] }, { "proposalHash", returned.Proposal["snapshotHash"] },
            { "requestedBy", returned.Root["requestedBy"] }, { "receivedBy", returned.Receipt["actorId"] }, { "inspectedBy", returned.Inspection["actorId"] }, { "proposedBy", returned.Proposal["actorId"] },
            { "participants", new BsonArray(returned.Participants.Select(id => new BsonInt64(id))) }, { "action", returned.Proposal["input"]["action"] },
            { "invoice", invoice.MoneySource.DeepClone() }, { "issuedLines", IssuedLines(row) }, { "originalAccounts", invoice.OriginalAccounts.DeepClone() },
            { "inspectionLines", ReturnResolutionRecords.Source(returned.Inspection)["lines"].DeepClone() } };
    }
    internal static BsonArray IssuedLines(BsonDocument invoice) => new(invoice["lines"].AsBsonArray.OrderBy(line => line["receiptId"].AsInt64).ThenBy(line => line["skuId"].AsInt64).Select(line => new BsonDocument {
        { "receiptId", line["receiptId"] }, { "skuId", line["skuId"] }, { "acceptedBaseUnits", line["acceptedBaseUnits"] }, { "previousBaseUnits", line["previousBaseUnits"] },
        { "previousNet", line["previousNet"] }, { "net", line["net"] }, { "tax", line["tax"] }, { "charges", line["charges"] },
        { "packagePrice", line["price"]["unitPrice"] }, { "baseUnitsPerPackage", new BsonInt64(line["definition"]["baseUnitsPerPackage"].ToInt64()) }, { "rounding", invoice["terms"]["rounding"] } }));
    internal static IssuedReturnLine Issued(BsonValue line) => new(line["receiptId"].AsInt64, line["skuId"].AsInt64, line["acceptedBaseUnits"].AsInt64, line["previousBaseUnits"].AsInt64,
        InvoiceBson.Money(line["previousNet"]), InvoiceBson.Money(line["net"]), InvoiceBson.Money(line["tax"]), InvoiceBson.Money(line["charges"]), InvoiceBson.Money(line["packagePrice"]),
        checked((int)line["baseUnitsPerPackage"].ToInt64()), line["rounding"].AsString);
    internal static BsonDocument Input(ReturnEntitlementInput input) => new("lines", new BsonArray(ReturnEntitlementPolicy.Copy(input).Lines.Select(line => new BsonDocument {
        { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "tax", InvoiceBson.Money(line.Tax) }, { "charges", InvoiceBson.Money(line.Charges) } })));
    internal static ReturnEntitlementInput Input(BsonDocument input) => ReturnEntitlementPolicy.Copy(new([.. input["lines"].AsBsonArray.Select(line => new ReturnEntitlementLineInput(
        line["receiptId"].AsInt64, line["skuId"].AsInt64, InvoiceBson.Money(line["tax"]), InvoiceBson.Money(line["charges"])))]));
    internal static BsonArray Lines(ReturnEntitlementResult result) => new(result.Lines.Select(line => new BsonDocument { { "receiptId", line.ReceiptId }, { "skuId", line.SkuId },
        { "attributedBaseUnits", line.AttributedBaseUnits }, { "net", InvoiceBson.Money(line.Net) }, { "tax", InvoiceBson.Money(line.Tax) }, { "charges", InvoiceBson.Money(line.Charges) } }));
    internal static ReturnEntitlementResult Review(BsonDocument source, BsonDocument before, BsonDocument policy, ReturnEntitlementInput input) {
        var capacities = source["inspectionLines"].AsBsonArray.Select(inspected => {
            if (inspected["identity"] != policy["requiredIdentity"]) throw ReturnEntitlementPolicy.Invalid("return_entitlement_identity_unverified");
            var issued = source["issuedLines"].AsBsonArray.SingleOrDefault(line => SameLine(line, inspected)) ?? throw B.Broken();
            var prior = before["lines"].AsBsonArray.SingleOrDefault(line => SameLine(line, inspected)) ?? throw B.Broken();
            return new ReturnLineCapacity(Issued(issued), prior["attributedBaseUnits"].AsInt64, InvoiceBson.Money(prior["net"]), InvoiceBson.Money(prior["tax"]), InvoiceBson.Money(prior["charges"]), inspected["inspectedBaseUnits"].AsInt64);
        }).ToArray();
        return ReturnEntitlementPolicy.Review([.. capacities], input, policy["netRule"].AsString, policy["componentRule"].AsString);
    }
    internal static bool SameLine(BsonValue a, BsonValue b) => a["receiptId"] == b["receiptId"] && a["skuId"] == b["skuId"];
    internal static string Binding(BsonDocument source, BsonDocument before, BsonDocument policy) => B.Hash(new() { { "source", source }, { "before", before }, { "policy", B.Snapshot(policy) } });
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        if (!policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime ||
            policy["netRule"] != "tail_backout" || policy["componentRule"] != "manual_component_allocation" || policy["requiredIdentity"] != "matched") throw B.Changed("return_entitlement_policy_unavailable");
        foreach (var field in new[] { "attributionProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "creditNoteProcedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[field].AsString, 256)) throw B.Broken();
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "returnId", row["_id"] }, { "caseId", row["caseId"] }, { "invoiceId", row["invoiceId"] }, { "action", row["action"] },
        { "version", row["version"] }, { "requestedAt", row["requestedAt"] }, { "net", row["net"] }, { "tax", row["tax"] }, { "charges", row["charges"] }, { "credit", row["credit"] },
        { "lines", row["lines"].DeepClone() }, { "state", "financial_review_requested" }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "reason", row["reason"] }, { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["requestedAt"] }, { "result", Result(row) } };
}
