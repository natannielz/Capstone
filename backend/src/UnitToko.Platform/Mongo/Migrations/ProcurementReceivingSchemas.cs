using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementReceivingSchemas
{
    internal static readonly string[] Collections = [B.Notices, B.Heads, B.Intakes];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Day => new() { { "bsonType", "string" }, { "pattern", @"\A[2-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Count(int minimum = 0) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", 1000000 } };
    private static BsonDocument Total => S.Number(0, long.MaxValue - 2);
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    internal static BsonDocument NoticeInput() => S.Object(new() { { "location", ProcurementRequestSchemas.Location() }, { "orderId", S.Number(1) },
        { "orderVersion", S.Number(1, 1) }, { "orderApprovalId", S.Number(1) }, { "noticeDate", Day }, { "supplierDocumentReference", S.Text(128) },
        { "ddoRequestReference", Nullable(S.Text(128)) }, { "ddoTelephoneReference", Nullable(S.Text(256)) }, { "declaredContainers", Count(1) }, { "note", S.Text(1000) } });
    internal static BsonDocument IntakeInput() => S.Object(new() { { "receivedDate", Day }, { "intakeReference", S.Text(128) },
        { "matchedContainers", Count() }, { "excessContainers", Count() }, { "heldContainers", Count() },
        { "condition", S.Choice("intact", "opened", "damaged", "mixed", "unverified") }, { "discrepancy", Nullable(S.Text(1000)) } });
    internal static BsonDocument Capacity() => S.Object(new() { { "version", Total }, { "declaredContainers", Count(1) }, { "matchedContainers", Count() },
        { "remainingContainers", Count() }, { "excessContainers", Total }, { "heldContainers", Total }, { "receivedContainers", Total } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Notices => [new("sourceKey", 1), new("auditId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Heads => [new("_id", 1)],
        B.Intakes => [new() { { "noticeId", 1 }, { "version", 1 } }, new() { { "noticeId", 1 }, { "input.intakeReference", 1 } },
            new("auditId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        var checks = new BsonArray(); var dates = new List<string>();
        if (name == B.Heads) {
            fields.AddRange(new BsonDocument { { "noticeId", S.Number(1) }, { "noticeHash", Hash }, { "version", Total },
                { "lastIntakeId", Nullable(S.Number(1)) }, { "lastHash", Nullable(Hash) }, { "matchedContainers", Count() },
                { "excessContainers", Total }, { "heldContainers", Total }, { "receivedContainers", Total }, { "createdAt", Date }, { "updatedAt", Date }, { "fence", Total } });
            checks.Add(Op("$eq", "$_id", "$noticeId")); checks.Add(Op("$gte", "$updatedAt", "$createdAt"));
            foreach (var field in new[] { "lastIntakeId", "lastHash" }) checks.Add(Op("$eq", Op("$eq", "$version", 0L), Op("$eq", "$" + field, BsonNull.Value)));
            Totals(checks, "$"); checks.Add(Op("$eq", Op("$eq", "$version", 0L), Op("$eq", "$receivedContainers", 0L)));
            dates.AddRange(["createdAt", "updatedAt"]);
        } else {
            fields.AddRange(new BsonDocument { { "operation", S.Choice(name == B.Notices ? B.NoticeOperation : B.IntakeOperation) },
                { "version", name == B.Notices ? S.Number(1, 1) : S.Number(1, long.MaxValue - 2) }, { "location", ProcurementRequestSchemas.Location() }, { "orderId", S.Number(1) },
                { "binding", Hash }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "people", new BsonDocument {
                    { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", Person } } },
                { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } }); S.Metadata(fields); dates.Add("recordedAt");
            checks.Add(Op("$eq", "$actorId", new BsonDocument("$arrayElemAt", new BsonArray { "$people.userId", 0 })));
            if (name == B.Notices) {
                fields.AddRange(new BsonDocument { { "orderVersion", S.Number(1, 1) }, { "orderApprovalId", S.Number(1) }, { "supplierId", S.Number(1) },
                    { "route", S.Choice("omi_regular", "ddo", "smart", "pasar_kering") }, { "input", NoticeInput() }, { "source", ProcurementFundingSchemas.OrderSource() },
                    { "sources", ProcurementRequestSchemas.Sources() }, { "sourceKey", Hash } });
                foreach (var field in new[] { "location", "orderId", "orderVersion", "orderApprovalId" }) checks.Add(Op("$eq", "$" + field, "$input." + field));
                foreach (var (left, right) in new[] { ("orderId", "source.order._id"), ("orderVersion", "source.order.version"), ("orderApprovalId", "source.order.approvalId"),
                    ("supplierId", "source.proposal.input.supplierId"), ("location", "source.order.location"), ("route", "source.request.input.route"),
                    ("source.order.approvalId", "source.approval._id"), ("source.order.proposalId", "source.proposal._id"),
                    ("source.order.approvalHash", "source.approval.snapshotHash"), ("source.order.proposalHash", "source.proposal.snapshotHash") }) checks.Add(Op("$eq", "$" + left, "$" + right));
                checks.Add(Op("$eq", "$source.approval.decision", "approve")); checks.Add(Op("$lte", "$source.order.recordedAt", "$recordedAt"));
                foreach (var field in new[] { "ddoRequestReference", "ddoTelephoneReference" }) checks.Add(Op("$eq", Op("$eq", "$route", "ddo"), Op("$ne", "$input." + field, BsonNull.Value)));
                checks.Add(ValidDay("$input.noticeDate")); checks.Add(Op("$lte", "$input.noticeDate", RecordedDay())); checks.Add(Op("$gte", "$input.noticeDate", "$source.proposal.input.businessDate"));
                foreach (var (part, kind, id, parent) in new[] { ("organization", "fulfillmentOrganization", "organizationId", (string?)null),
                    ("shop", "shop", "shopId", "organizationId"), ("warehouse", "warehouse", "warehouseId", "shopId"), ("channel", "channel", "channelId", "organizationId") }) {
                    checks.Add(Op("$eq", "$sources." + part + ".target.id", "$location." + id)); checks.Add(Op("$eq", "$sources." + part + ".target.kind", kind));
                    checks.Add(Op("$eq", "$sources." + part + ".target.parentId", parent is null ? BsonNull.Value : "$location." + parent));
                    checks.Add(Op("$lte", "$sources." + part + ".profile.approvedAt", "$recordedAt")); dates.Add("sources." + part + ".profile.approvedAt");
                }
            } else {
                fields.AddRange(new BsonDocument { { "noticeId", S.Number(1) }, { "noticeHash", Hash }, { "previousIntakeId", Nullable(S.Number(1)) },
                    { "previousHash", Nullable(Hash) }, { "input", IntakeInput() }, { "before", Capacity() }, { "after", Capacity() } });
                foreach (var field in new[] { "previousIntakeId", "previousHash" }) checks.Add(Op("$eq", Op("$eq", "$version", 1L), Op("$eq", "$" + field, BsonNull.Value)));
                foreach (var prefix in new[] { "$before.", "$after." }) {
                    Totals(checks, prefix); checks.Add(Op("$eq", prefix + "declaredContainers", Op("$add", prefix + "matchedContainers", prefix + "remainingContainers")));
                    checks.Add(Op("$eq", Op("$eq", prefix + "version", 0L), Op("$eq", prefix + "receivedContainers", 0L)));
                }
                checks.Add(Op("$eq", "$version", "$after.version")); checks.Add(Op("$eq", "$version", Op("$add", "$before.version", 1L)));
                checks.Add(Op("$eq", "$before.declaredContainers", "$after.declaredContainers"));
                foreach (var field in new[] { "matchedContainers", "excessContainers", "heldContainers" }) checks.Add(Op("$eq", "$after." + field, Op("$add", "$before." + field, "$input." + field)));
                var total = Op("$add", "$input.matchedContainers", "$input.excessContainers");
                checks.Add(Op("$gte", total, 1)); checks.Add(Op("$lte", total, 1000000)); checks.Add(Op("$lte", "$input.excessContainers", "$input.heldContainers")); checks.Add(Op("$lte", "$input.heldContainers", total));
                checks.Add(Op("$or", Op("$and", Op("$eq", "$input.condition", "intact"), Op("$eq", "$input.heldContainers", 0), Op("$eq", "$input.excessContainers", 0)), Op("$ne", "$input.discrepancy", BsonNull.Value)));
                checks.Add(Op("$or", Op("$not", Op("$in", "$input.condition", new BsonArray { "opened", "damaged", "unverified" })), Op("$eq", "$input.heldContainers", total)));
                checks.Add(Op("$or", Op("$ne", "$input.condition", "mixed"), Op("$gt", "$input.heldContainers", 0)));
                checks.Add(ValidDay("$input.receivedDate")); checks.Add(Op("$lte", "$input.receivedDate", RecordedDay()));
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static void Totals(BsonArray checks, string prefix) {
        checks.Add(Op("$eq", prefix + "receivedContainers", Op("$add", prefix + "matchedContainers", prefix + "excessContainers")));
        checks.Add(Op("$lte", prefix + "excessContainers", prefix + "heldContainers")); checks.Add(Op("$lte", prefix + "heldContainers", prefix + "receivedContainers"));
    }
    private static BsonDocument RecordedDay() => new("$dateToString", new BsonDocument { { "date", "$recordedAt" }, { "format", "%Y-%m-%d" }, { "timezone", "Asia/Jakarta" } });
    private static BsonDocument ValidDay(string path) => Op("$ne", new BsonDocument("$dateFromString", new BsonDocument { { "dateString", path }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value);
    private static BsonDocument Op(string operation, params BsonValue[] values) => new(operation, new BsonArray(values));
}
