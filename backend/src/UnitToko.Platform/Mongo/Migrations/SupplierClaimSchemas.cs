using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.SupplierClaimBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class SupplierClaimSchemas
{
    internal const string PhysicalHeads = "procurementQuarantineDispositionHeads", PhysicalAllocations = "procurementQuarantineDispositions",
        ShortageHeads = "supplierClaimShortageHeads", ShortageAllocations = "supplierClaimShortageAllocations";
    internal static readonly string[] Collections = [B.Requests, B.Decisions, PhysicalHeads, PhysicalAllocations, ShortageHeads, ShortageAllocations];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Money => new() { { "bsonType", "string" }, { "pattern", @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument Int(int min = 1, int max = 1000000) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Total(long min = 0) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument value, int min = 1, int max = 100) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", value } };
    internal static BsonDocument Line() => S.Object(new() {
        { "basis", S.Choice("physical_received", "shortage_observed") }, { "inspectionId", S.Number(1) }, { "inspectionLineNumber", Int(1, 100) },
        { "packages", Int() }, { "issue", S.Choice("damaged", "expired", "opened", "wrong_item", "shortage", "other") },
        { "invoiceDocumentId", Nullable(S.Number(1)) }, { "invoiceDocumentLineNumber", Nullable(Int(1, 100)) },
        { "invoiceMatchId", Nullable(S.Number(1)) }, { "invoiceMatchLineNumber", Nullable(Int(1, 100)) } });
    internal static BsonDocument Input() => S.Object(new() {
        { "location", ProcurementRequestSchemas.Location() }, { "orderId", S.Number(1) }, { "orderVersion", S.Number(1, 1) }, { "orderApprovalId", S.Number(1) },
        { "claimReference", S.Text(128) }, { "businessDate", Day }, { "responseDueOn", Day }, { "deadlineBasisReference", S.Text(256) },
        { "requestedResolution", S.Choice("replacement", "credit", "cash_refund", "investigation") }, { "reason", S.Text(1000) }, { "lines", Array(Line()) } });
    internal static BsonDocument Capacity() => S.Object(new() {
        { "basis", S.Choice("physical_received", "shortage_observed") }, { "inspectionId", S.Number(1) }, { "inspectionLineNumber", Int(1, 100) },
        { "inspectionHash", Hash }, { "headId", Nullable(S.Number(1)) }, { "version", Total() }, { "totalPackages", Total() },
        { "occupiedPackages", Total() }, { "remainingPackages", Total() } });
    private static BsonDocument ResolvedLine() => S.Object(new() { { "lineNumber", Int(1, 100) }, { "input", Line() },
        { "inspection", S.Object(new() { { "id", S.Number(1) }, { "version", S.Number(1, 1) }, { "hash", Hash }, { "inspectionDate", Day },
            { "inspectionReference", S.Text(128) }, { "noticeId", S.Number(1) }, { "intakeId", S.Number(1) }, { "line", ProcurementInspectionSchemas.Line() } }) },
        { "invoice", Nullable(S.Object(new() { { "documentId", S.Number(1) }, { "documentRevision", S.Number(1, 1) }, { "documentReference", S.Text(128) },
            { "documentDate", Nullable(Day) }, { "documentLineNumber", Int(1, 100) },
            { "documentLine", SupplierDocumentSchemas.Input()["properties"]["lines"]["items"].DeepClone().AsBsonDocument },
            { "matchId", Nullable(S.Number(1)) }, { "matchLineNumber", Nullable(Int(1, 100)) }, { "sourceInvoiceAmount", Nullable(Money) } })) } });
    private static BsonDocument AllocationLink() => S.Object(new() {
        { "basis", S.Choice("physical_received", "shortage_observed") }, { "allocationId", S.Number(1) }, { "headId", S.Number(1) },
        { "inspectionId", S.Number(1) }, { "inspectionLineNumber", Int(1, 100) }, { "inspectionHash", Hash }, { "packages", Int() },
        { "before", Capacity() }, { "after", Capacity() }, { "previousAllocationId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Requests => [new() { { "location.organizationId", 1 }, { "location.shopId", 1 }, { "location.warehouseId", 1 }, { "location.channelId", 1 }, { "supplierId", 1 }, { "orderId", 1 }, { "claimReference", 1 } },
            new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("claimId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        PhysicalHeads or ShortageHeads => [new() { { "inspectionId", 1 }, { "inspectionLineNumber", 1 } }],
        PhysicalAllocations or ShortageAllocations => [new() { { "claimId", 1 }, { "claimLineNumber", 1 } }, new() { { "inspectionId", 1 }, { "inspectionLineNumber", 1 }, { "sequence", 1 } }, new() { { "headId", 1 }, { "sequence", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "location", ProcurementRequestSchemas.Location() } };
        var checks = new BsonArray(); var dates = new List<string>();
        if (name is B.Requests or B.Decisions) {
            var request = name == B.Requests;
            fields.AddRange(new BsonDocument { { "claimId", S.Number(1) }, { "version", S.Number(1, 1) }, { "orderId", S.Number(1) }, { "supplierId", S.Number(1) },
                { "binding", Hash }, { "operation", request ? S.Choice(B.Prefix + "request") : S.Choice(B.Prefix + "approve", B.Prefix + "reject") },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash },
                { "people", Array(S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } }), request ? 1 : 2, request ? 1 : 2) },
                { "capacity", Array(Capacity()) } }); S.Metadata(fields); dates.Add("recordedAt");
            if (request) {
                fields.Add("claimReference", S.Text(128)).Add("input", Input()).Add("source", ProcurementFundingSchemas.OrderSource()).Add("lines", Array(ResolvedLine())).Add("allocations", Array(AllocationLink()));
                foreach (var (left, right) in new[] { ("_id", "claimId"), ("location", "input.location"), ("location", "source.order.location"), ("orderId", "input.orderId"),
                    ("orderId", "source.order._id"), ("input.orderVersion", "source.order.version"), ("input.orderApprovalId", "source.order.approvalId"), ("claimReference", "input.claimReference"), ("reason", "input.reason") }) checks.Add(Op("$eq", "$" + left, "$" + right));
                checks.Add(Op("$lte", "$input.businessDate", "$input.responseDueOn")); checks.Add(Op("$lte", "$source.order.recordedAt", "$recordedAt"));
                foreach (var field in new[] { "lines", "capacity", "allocations" }) checks.Add(Op("$eq", new BsonDocument("$size", "$" + field), new BsonDocument("$size", "$input.lines")));
            } else {
                fields.Add("requestHash", Hash).Add("decision", S.Choice("approve", "reject")).Add("input", S.Object(new() { { "decision", S.Choice("approve", "reject") }, { "explanation", S.Text(1000) } }));
                checks.Add(Op("$eq", "$decision", "$input.decision")); checks.Add(Op("$eq", "$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
            }
        } else {
            var physical = name is PhysicalHeads or PhysicalAllocations; var head = name is PhysicalHeads or ShortageHeads;
            fields.AddRange(new BsonDocument { { "basis", S.Choice(physical ? "physical_received" : "shortage_observed") }, { "inspectionId", S.Number(1) },
                { "inspectionLineNumber", Int(1, 100) }, { "inspectionHash", Hash } });
            if (head) {
                fields.AddRange(new BsonDocument { { "orderId", S.Number(1) }, { "supplierId", S.Number(1) }, { "version", Total(1) }, { "totalPackages", Total() },
                    { "occupiedPackages", Total(1) }, { "remainingPackages", Total() }, { "lastAllocationId", S.Number(1) }, { "lastHash", Hash }, { "fence", Total() }, { "createdAt", Date }, { "updatedAt", Date } });
                dates.AddRange(["createdAt", "updatedAt"]); checks.Add(Op("$gte", "$updatedAt", "$createdAt")); checks.Add(Op("$eq", "$totalPackages", Op("$add", "$occupiedPackages", "$remainingPackages")));
            } else {
                fields.AddRange(new BsonDocument { { "kind", S.Choice("supplier_claim") }, { "claimId", S.Number(1) }, { "claimLineNumber", Int(1, 100) }, { "requestHash", Hash },
                    { "headId", S.Number(1) }, { "packages", Int() }, { "sequence", Total(1) }, { "before", Capacity() }, { "after", Capacity() },
                    { "previousAllocationId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "recordedAt", Date }, { "snapshotHash", Hash } }); dates.Add("recordedAt");
                foreach (var field in new[] { "basis", "inspectionId", "inspectionLineNumber", "inspectionHash" }) {
                    checks.Add(Op("$eq", "$" + field, "$before." + field)); checks.Add(Op("$eq", "$" + field, "$after." + field));
                }
                checks.Add(Op("$eq", "$headId", "$after.headId")); checks.Add(Op("$eq", "$sequence", "$after.version")); checks.Add(Op("$eq", "$sequence", Op("$add", "$before.version", 1L)));
                checks.Add(Op("$eq", "$after.occupiedPackages", Op("$add", "$before.occupiedPackages", "$packages")));
                checks.Add(Op("$eq", "$after.remainingPackages", Op("$subtract", "$before.remainingPackages", "$packages")));
                checks.Add(Op("$eq", "$before.totalPackages", "$after.totalPackages"));
                foreach (var field in new[] { "previousAllocationId", "previousHash" }) checks.Add(Op("$eq", Op("$eq", "$sequence", 1L), Op("$eq", "$" + field, BsonNull.Value)));
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static BsonDocument Op(string name, params BsonValue[] args) => new(name, new BsonArray(args));
}
