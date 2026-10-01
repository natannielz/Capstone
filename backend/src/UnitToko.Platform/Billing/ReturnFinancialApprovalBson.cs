using MongoDB.Bson;
using UnitToko.Modules.Billing;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;
internal static class ReturnFinancialApprovalBson
{
    internal const string Events = "returnFinancialApprovals", Policies = "returnFinancialApprovalPolicies", Operation = "billing.return.financial.approve";
    internal static object Body(long caseId, long returnId, string action, string binding, ReturnFinancialApprovalInput input) =>
        new { caseId, returnId, action, binding, input = ReturnFinancialApprovalPolicy.Copy(input) };
    internal static BsonDocument Input(ReturnFinancialApprovalInput input) {
        input = ReturnFinancialApprovalPolicy.Copy(input);
        return new() { { "expectedInvoiceVersion", input.ExpectedInvoiceVersion }, { "releases", new BsonArray(input.Releases.Select(line => new BsonDocument {
            { "originalAllocationId", line.OriginalAllocationId.Value }, { "expectedReceiptVersion", line.ExpectedReceiptVersion }, { "amount", InvoiceBson.Money(line.Amount) } })) } };
    }
    internal static ReturnFinancialApprovalInput Input(BsonDocument input) => ReturnFinancialApprovalPolicy.Copy(new(input["expectedInvoiceVersion"].AsInt64,
        [.. input["releases"].AsBsonArray.Select(line => new ReturnFinancialReleaseInput(new(line["originalAllocationId"].AsInt64), line["expectedReceiptVersion"].AsInt64, InvoiceBson.Money(line["amount"])))]));
    internal static string Binding(BsonDocument request, BsonDocument policy, BsonDocument invoiceBefore, BsonArray receipts, BsonArray links, ReturnFinancialApprovalInput input) =>
        B.Hash(new() { { "request", request }, { "policy", B.Snapshot(policy) }, { "invoiceBefore", invoiceBefore }, { "receipts", receipts }, { "links", links }, { "input", Input(input) } });
    internal static ReturnFinancialApprovalResult Review(BsonDocument row) {
        var party = B.Party(row["party"].AsBsonDocument); var before = row["invoiceBefore"];
        var invoice = new InvoiceFunds(new(row["invoiceId"].AsInt64), before["version"].AsInt64, party, "IDR", InvoiceBson.Money(row["invoice"]["total"]), InvoiceBson.Money(before["paid"]), InvoiceBson.Money(before["credited"]));
        return ReturnFinancialApprovalPolicy.Review(invoice, InvoiceBson.Money(row["request"]["credit"]), InvoiceBson.Money(row["credit"]), row["action"].AsString,
            [.. row["links"].AsBsonArray.Select(link => {
                var receipt = row["receipts"].AsBsonArray.Single(value => value["receiptId"] == link["receiptId"]); var state = receipt["before"];
                return new ReturnFinancialAllocationCapacity(new(link["originalAllocationId"].AsInt64), new(row["invoiceId"].AsInt64),
                    new(new(receipt["receiptId"].AsInt64), state["version"].AsInt64, party, "IDR", InvoiceBson.Money(receipt["receiptTotal"]), InvoiceBson.Money(state["allocated"]), InvoiceBson.Money(state["reservedForRefund"])),
                    InvoiceBson.Money(link["originallyAllocated"]), InvoiceBson.Money(link["previouslyReleased"]));
            })], Input(row["input"].AsBsonDocument), row["policy"]["creditRule"].AsString, row["policy"]["reservationRule"].AsString);
    }
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        if (!policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime ||
            policy["creditRule"] != "credit_original_components" || policy["reservationRule"] != "reserve_released_allocations" || policy["independenceRule"] != "request_and_funding_makers")
            throw B.Changed("return_financial_policy_unavailable");
        foreach (var key in new[] { "arMapping", "refundLiabilityMapping", "refundLiabilityAccountCode" })
            if (!ReceivingAccountPolicy.Text(policy[key].AsString, 80)) throw B.Broken();
        if (policy["refundLiabilityAccountId"].AsInt64 <= 0 || policy["arMapping"] == policy["refundLiabilityMapping"]) throw B.Broken();
        foreach (var key in new[] { "creditNoteProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "refundReservationProcedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[key].AsString, 256)) throw B.Broken();
    }
    internal static BsonDocument Result(BsonDocument row) {
        var result = Review(row);
        return new() { { "financialApprovalId", row["_id"] }, { "caseId", row["caseId"] }, { "returnId", row["returnId"] }, { "invoiceId", row["invoiceId"] }, { "action", row["action"] },
            { "invoiceVersion", row["invoiceVersion"] }, { "credit", row["credit"] }, { "reservedRefund", row["reservedRefund"] }, { "remainingInvoice", row["remainingInvoice"] },
            { "recordedAt", row["recordedAt"] }, { "operation", Operation }, { "state", row["action"] == "refund" ? "refund_reserved" : "invoice_credited" },
            { "receipts", new BsonArray(result.Receipts.Select(receipt => new BsonDocument { { "receiptId", receipt.After.ReceiptId.Value }, { "version", receipt.After.Version },
                { "released", InvoiceBson.Money(receipt.Released) }, { "reservedForRefund", InvoiceBson.Money(receipt.After.ReservedForRefund) }, { "available", InvoiceBson.Money(receipt.Available) } })) },
            { "releases", new BsonArray(result.Releases.Select(link => new BsonDocument { { "originalAllocationId", link.OriginalAllocationId.Value }, { "invoiceId", row["invoiceId"] }, { "amount", InvoiceBson.Money(link.Amount) } })) } };
    }
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
