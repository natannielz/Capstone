using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;
internal static class PaymentAllocationCorrectionBson
{
    internal const string Events = "paymentAllocationCorrections", Policies = "paymentAllocationCorrectionPolicies", Operation = "billing.payment.allocation.correct";
    internal static object Body(long allocationId, string binding, PaymentAllocationInput input) => new { allocationId, binding, input = PaymentAllocationPolicy.Copy(input) };
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        PaymentAllocationRecords.PolicyAt(policy, at);
        if (!ReceivingAccountPolicy.Text(policy["correctionProcedureReference"].AsString, 256)) throw B.Broken();
    }
    internal static string Binding(BsonDocument original, BsonDocument correctionPolicy, BsonDocument policy, BsonDocument before, BsonArray lines) => B.Hash(new() {
        { "originalAllocationId", original["_id"] }, { "originalAllocationHash", original["snapshotHash"] }, { "correctionPolicy", B.Snapshot(correctionPolicy) },
        { "balanceBinding", B.Binding(original["verificationHash"], original["receiptHash"], policy, before, lines) },
        { "capacities", new BsonArray(lines.Select(line => new BsonDocument { { "invoiceId", line["invoiceId"] }, { "originallyAllocated", line["originallyAllocated"] }, { "previouslyReversed", line["previouslyReversed"] } })) } });
    internal static BsonDocument Result(BsonDocument row) {
        var result = B.Result(row); result.Remove("allocationId"); result.Add("correctionId", row["_id"]); result.Add("originalAllocationId", row["originalAllocationId"]); result["operation"] = Operation; return result;
    }
    internal static BsonDocument Audit(BsonDocument row) {
        var audit = B.Audit(row); audit["kind"] = Operation; audit["operation"] = Operation; audit["result"] = Result(row); return audit;
    }
    internal static string SourceKey(BsonDocument row) => Operation + ":" + row["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
