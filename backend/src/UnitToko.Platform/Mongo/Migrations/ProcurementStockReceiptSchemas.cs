using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static partial class ProcurementStockReceiptSchemas
{
    internal static readonly string[] Collections = [B.Receipts];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Long(long min = 0) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Int(int max = 1000000) => new() { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", max } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    internal static BsonDocument Input() => S.Object(new() {
        { "location", ProcurementRequestSchemas.Location() }, { "orderId", Long(1) }, { "orderVersion", S.Number(1, 1) }, { "orderApprovalId", Long(1) },
        { "inspectionId", Long(1) }, { "inspectionLineNumber", Int(100) }, { "packages", Int() }, { "poolId", Long(1) }, { "poolRevision", Long(1) },
        { "receiptReference", S.Text(128) }, { "lotReference", S.Text(128) }, { "expiresOn", Nullable(Day) }, { "reason", S.Text(1000) } });
    private static BsonDocument RowSchema(BsonDocument validator) => validator["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument;
    internal static BsonDocument PoolSource() => S.Object(new() {
        { "poolId", Long(1) }, { "requestedLocation", ProcurementRequestSchemas.Location() },
        { "configuration", StockSourceSuspensionSchemas.Validator()["$and"][0]["$jsonSchema"]["properties"]["configuration"].DeepClone() },
        { "policyView", S.Object(new() {
            { "proposal", RowSchema(StockSourcePolicySchemas.Validator("stockSourcePolicyProposals")) },
            { "decision", RowSchema(StockSourcePolicySchemas.Validator("stockSourcePolicyDecisions")) },
            { "policy", RowSchema(StockSourcePolicySchemas.Validator("stockSourcePolicies")) } }) },
        { "poolFence", Long() }, { "suspensionRevision", Long() } });
    internal static BsonDocument[] Keys(string name) => name == B.Receipts ? [
        new() { { "location.organizationId", 1 }, { "location.shopId", 1 }, { "location.warehouseId", 1 }, { "location.channelId", 1 }, { "supplierId", 1 }, { "orderId", 1 }, { "receiptReference", 1 } },
        new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1), new("lotId", 1), new("balanceId", 1), new("movementId", 1)
    ] : throw new ArgumentOutOfRangeException(nameof(name));
    internal static BsonDocument Validator(string name) {
        if (name != B.Receipts) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", Long(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "receiptId", Long(1) }, { "revision", S.Number(1, 1) }, { "location", ProcurementRequestSchemas.Location() }, { "orderId", Long(1) }, { "supplierId", Long(1) },
            { "inspectionId", Long(1) }, { "inspectionLineNumber", Int(100) }, { "poolId", Long(1) }, { "receiptReference", S.Text(128) }, { "input", Input() },
            { "source", ProcurementFundingSchemas.OrderSource() }, { "inspection", RowSchema(ProcurementInspectionSchemas.Validator("procurementInspectionSubmissions")) },
            { "poolSource", PoolSource() }, { "before", SupplierClaimSchemas.Capacity() }, { "after", SupplierClaimSchemas.Capacity() },
            { "disposition", S.Object(new() { { "allocationId", Long(1) }, { "headId", Long(1) }, { "previousAllocationId", Nullable(Long(1)) }, { "previousHash", Nullable(Hash) } }) },
            { "lotId", Long(1) }, { "balanceId", Long(1) }, { "movementId", Long(1) }, { "binding", Hash },
            { "people", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", S.Object(new() {
                { "userId", Long(1) }, { "personId", Long(1) }, { "verification", S.Choice("Verified") }, { "version", Long(1) } }) } } },
            { "valuationState", S.Choice("pending") }, { "valuation", new BsonDocument("bsonType", "null") }, { "state", S.Choice(B.State) },
            { "operation", S.Choice(B.Operation) }, { "actorId", Long(1) }, { "actorGrantVersion", Long(1) }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } };
        S.Metadata(fields); var checks = new BsonArray();
        foreach (var (left, right) in new[] { ("_id", "receiptId"), ("location", "input.location"), ("location", "source.order.location"),
            ("location", "poolSource.requestedLocation"), ("orderId", "input.orderId"), ("orderId", "source.order._id"),
            ("input.orderVersion", "source.order.version"), ("input.orderApprovalId", "source.order.approvalId"), ("inspectionId", "input.inspectionId"),
            ("inspectionId", "inspection._id"), ("inspectionLineNumber", "input.inspectionLineNumber"), ("poolId", "input.poolId"), ("poolId", "poolSource.poolId"),
            ("receiptReference", "input.receiptReference"), ("reason", "input.reason"), ("disposition.headId", "after.headId") }) checks.Add(Op("$eq", "$" + left, "$" + right));
        checks.Add(Op("$eq", "$before.basis", "physical_received")); checks.Add(Op("$eq", "$after.basis", "physical_received"));
        checks.Add(Op("$eq", "$after.version", Op("$add", "$before.version", 1L)));
        checks.Add(Op("$eq", "$after.occupiedPackages", Op("$add", "$before.occupiedPackages", "$input.packages")));
        checks.Add(Op("$eq", "$after.remainingPackages", Op("$subtract", "$before.remainingPackages", "$input.packages")));
        checks.Add(Op("$eq", "$before.totalPackages", "$after.totalPackages")); checks.Add(Op("$lte", "$inspection.recordedAt", "$recordedAt"));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)), S.DateRange("recordedAt") });
    }
    // The exact applied M71 branch remains intact; only this new owner gets the v2 branch.
    internal static BsonDocument DispositionValidator() {
        var previous = SupplierClaimSchemas.Validator(SupplierClaimSchemas.PhysicalAllocations); var next = previous.DeepClone().AsBsonDocument;
        var schema = next["$and"][0]["$jsonSchema"].AsBsonDocument; var fields = schema["properties"].AsBsonDocument;
        fields["schemaVersion"]["enum"] = new BsonArray { 2 }; fields["kind"] = S.Choice("held_receipt");
        fields.Remove("claimId"); fields.Remove("claimLineNumber"); fields.Remove("requestHash"); fields.Add("receiptId", Long(1)).Add("receiptHash", Hash);
        schema["required"] = new BsonArray(fields.Names);
        return new("$or", new BsonArray { previous, next });
    }
    private static BsonDocument Op(string name, params BsonValue[] args) => new(name, new BsonArray(args));
}
